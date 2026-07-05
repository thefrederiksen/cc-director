using System.Collections.Concurrent;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.HostedAi;
using CcDirector.Gateway.Transcription;
using CcDirector.Gateway.Util;
using CcDirector.Gateway.Voice;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Durable, server-owned dictation upload (issue #1006).
///
/// The mobile app persists the raw recorded audio locally (IndexedDB) the instant Send is pressed,
/// then streams it here in SHA-checked chunks. Once the clip is fully uploaded the GATEWAY assembles
/// it, transcribes it, and injects the resulting text into the owning session ITSELF. So once the
/// audio reaches the server a dead tab, a page refresh, or a dropped connection can no longer lose a
/// recorded utterance: the client only has to get the bytes up (resumable, retry-per-chunk from the
/// durable local copy), and the server finishes the turn.
///
///   POST /dictation/upload               { sessionId, baselineBufferBytes } + Idempotency-Key -> { upload_id }
///   PUT  /dictation/{uploadId}/chunk/{i}  octet-stream + X-Chunk-Sha256                        -> { ok }
///   POST /dictation/{uploadId}/complete   { sessionId,totalChunks,mime,ext,before,after,baselineBufferBytes,resumed }
///                                          -> 200 { submitted, movedOn, transcript } | 409 { missing } | 402 | 5xx
///
/// A retried complete is single-flighted per uploadId so the turn is submitted at most once. Abandoned
/// uploads are swept after ~1 hour (<see cref="VoiceUploadStore.SweepAbandoned"/> + <see cref="SweepCompletes"/>).
/// Every route is token-gated via <see cref="AuthMiddleware.HasValidToken"/> so it holds even when the
/// production tray Gateway runs with the global auth middleware off.
/// </summary>
internal static class GatewayDictationEndpoint
{
    // How much the session's terminal output may grow after a RESUMED clip was recorded before we treat
    // it as "moved on" and refuse to inject the now-stale dictation (issue #1006 guard). Immediate
    // (non-resumed) sends always inject; the 1-hour staging sweep is the hard backstop for staleness.
    private const long MovedOnBufferGrowthBytes = 512;

    // Single-flight + idempotency cache for complete, keyed by uploadId: concurrent or retried completes
    // await the SAME work, and a terminal outcome is cached so a retry after a dropped response returns
    // it instead of submitting a second turn. Non-terminal outcomes (error/incomplete/no-key) are NOT
    // cached, so a genuine retry re-runs. Swept by age (SweepCompletes).
    private sealed record CompleteEntry(DateTime At, Lazy<Task<DictationOutcome>> Task);
    private static readonly ConcurrentDictionary<string, CompleteEntry> _completes = new();

    public static void Map(IEndpointRouteBuilder app, DirectorRegistry registry, DirectorEndpointClient client,
        SessionOwnerCache? owners, string token, GatewayTranscriptionService transcription,
        TranscribingSessions transcribingSessions, VoiceUploadStore uploads, Pairing.DeviceRegistry devices)
    {
        app.MapPost("/dictation/upload", (DictationUploadRequest? body, HttpContext ctx) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token, devices))
                return Results.Json(new { error = "missing or invalid token" }, statusCode: StatusCodes.Status401Unauthorized);
            var sid = body?.SessionId ?? "";
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "sessionId (guid) is required" }, statusCode: StatusCodes.Status400BadRequest);

            // The client's locally-generated id (its IndexedDB record id) is the Idempotency-Key AND the
            // upload id, so a resumed upload after a tab death maps back to the same staging dir.
            var key = ctx.Request.Headers["Idempotency-Key"].ToString();
            var uploadId = uploads.Register(string.IsNullOrWhiteSpace(key) ? null : key);
            try { transcribingSessions.Begin(sid); } catch { /* the orange mark is a nicety */ }
            FileLog.Write($"[GatewayDictation] upload registered sid={sid} uploadId={uploadId}");
            return Results.Json(new { upload_id = uploadId });
        });

        app.MapPut("/dictation/{uploadId}/chunk/{index:int}", async (string uploadId, int index, HttpContext ctx) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token, devices))
                return Results.Json(new { error = "missing or invalid token" }, statusCode: StatusCodes.Status401Unauthorized);
            if (!uploads.Exists(uploadId))
                return Results.Json(new { error = "unknown upload id (register it first)" }, statusCode: StatusCodes.Status404NotFound);

            var sha = ctx.Request.Headers["X-Chunk-Sha256"].ToString();
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
            try
            {
                await uploads.StoreChunkAsync(uploadId, index, ms.ToArray(), string.IsNullOrEmpty(sha) ? null : sha, ctx.RequestAborted);
                return Results.Json(new { ok = true, index });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayDictation] chunk uploadId={uploadId} index={index} FAILED: {ex.Message}");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        app.MapPost("/dictation/{uploadId}/complete", async (string uploadId, DictationCompleteRequest? req, HttpContext ctx) =>
        {
            if (!AuthMiddleware.HasValidToken(ctx, token, devices))
                return Results.Json(new { error = "missing or invalid token" }, statusCode: StatusCodes.Status401Unauthorized);
            if (req is null || req.TotalChunks <= 0 || !Guid.TryParse(req.SessionId ?? "", out _))
                return Results.Json(new { error = "sessionId (guid) and totalChunks (>0) are required" },
                    statusCode: StatusCodes.Status400BadRequest);

            var entry = _completes.GetOrAdd(uploadId, id => new CompleteEntry(
                DateTime.UtcNow,
                new Lazy<Task<DictationOutcome>>(() => RunCompleteAsync(
                    id, req, uploads, registry, client, owners, transcription, transcribingSessions))));

            DictationOutcome outcome;
            try
            {
                outcome = await entry.Task.Value;
            }
            catch (Exception ex)
            {
                _completes.TryRemove(uploadId, out _); // transient: let a retry re-run
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
            // Only a truly terminal outcome (submitted / moved-on) is kept for idempotent dedupe; anything
            // retryable (transient error, incomplete upload, out-of-credits) is dropped so the next
            // complete re-runs the real work.
            if (!outcome.Terminal) _completes.TryRemove(uploadId, out _);
            return outcome.ToResult();
        });
    }

    private static async Task<DictationOutcome> RunCompleteAsync(
        string uploadId, DictationCompleteRequest req, VoiceUploadStore uploads, DirectorRegistry registry,
        DirectorEndpointClient client, SessionOwnerCache? owners, GatewayTranscriptionService transcription,
        TranscribingSessions transcribingSessions)
    {
        var sid = req.SessionId!;
        try
        {
            // The configured mode's key must be present before we pay the reassembly + transcribe cost.
            var routing = transcription.Resolve();
            if (routing.Key is null)
                return DictationOutcome.Error(StatusCodes.Status503ServiceUnavailable,
                    $"no key configured for transcription mode {routing.Mode}");

            var assembled = await uploads.AssembleAsync(uploadId, req.TotalChunks);
            if (assembled.Status == "unknown_upload")
                return DictationOutcome.Error(StatusCodes.Status404NotFound, "unknown upload id");
            if (assembled.Status == "incomplete")
                return DictationOutcome.Incomplete(assembled.Missing);
            var audio = assembled.Audio;
            if (audio is null || audio.Length == 0)
            {
                uploads.Delete(uploadId);
                return DictationOutcome.Error(StatusCodes.Status502BadGateway, "assembled recording was empty");
            }

            var result = await transcription.TranscribeAsync(
                audio, "audio." + (req.Ext ?? "wav"), req.Mime ?? "audio/wav", applyCorrection: true, CancellationToken.None);
            if (result.Outcome == TranscriptionOutcome.OutOfCredits)
                return DictationOutcome.OutOfCredits(HostedAiErrorMapper.MapCode(result.Code));
            if (result.Outcome != TranscriptionOutcome.Ok)
                return DictationOutcome.Error(StatusCodes.Status502BadGateway, result.Error ?? "transcription failed");

            var transcript = (result.Text ?? "").Trim();
            // Compose the final message: any typed text the caret split the dictation around (before /
            // after), any earlier paused dictation segments already turned to text (prefix), and this
            // clip's transcript, space-joined skipping empties. The common voice case is transcript alone.
            var parts = new[] { req.Before, req.Prefix, transcript, req.After }
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!.Trim());
            var message = string.Join(" ", parts).Trim();
            if (message.Length == 0)
            {
                // Silent/empty clip with no typed text: nothing to submit, but the turn is genuinely done.
                uploads.Delete(uploadId);
                EndTranscribing(transcribingSessions, sid);
                return DictationOutcome.Submitted(false, false, transcript);
            }

            var (endpoint, session) = await LocateAsync(registry, client, owners, sid);
            if (endpoint is null)
                return DictationOutcome.Error(
                    session is null ? StatusCodes.Status404NotFound : StatusCodes.Status410Gone,
                    session is null ? "session not found" : "session has exited");

            // Moved-on guard (issue #1006): for a RESUMED clip, if the session's terminal output grew
            // materially since the clip was recorded, other turns happened - drop the stale dictation
            // rather than inject it into a session that has moved on. Immediate sends skip this.
            if (req.Resumed && session is not null && req.BaselineBufferBytes > 0 &&
                session.TotalBufferBytes > req.BaselineBufferBytes + MovedOnBufferGrowthBytes)
            {
                uploads.Delete(uploadId);
                EndTranscribing(transcribingSessions, sid);
                FileLog.Write($"[GatewayDictation] complete sid={sid} uploadId={uploadId}: session moved on " +
                    $"(buffer {req.BaselineBufferBytes}->{session.TotalBufferBytes}); dropped");
                return DictationOutcome.Submitted(false, true, transcript);
            }

            var (ok, _, err) = await client.PostPromptAsync(endpoint, sid, new PromptRequest { Text = message, AppendEnter = true });
            if (!ok)
                return DictationOutcome.Error(StatusCodes.Status502BadGateway, err ?? "submit to session failed");

            uploads.Delete(uploadId);
            EndTranscribing(transcribingSessions, sid);
            FileLog.Write($"[GatewayDictation] complete sid={sid} uploadId={uploadId}: submitted chars={message.Length}");
            return DictationOutcome.Submitted(true, false, transcript);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayDictation] complete sid={sid} uploadId={uploadId} FAILED: {ex.Message}");
            return DictationOutcome.Error(StatusCodes.Status502BadGateway, ex.Message);
        }
    }

    /// <summary>Drop cached complete outcomes older than <paramref name="maxAge"/> (idempotency window).</summary>
    public static int SweepCompletes(TimeSpan maxAge)
    {
        var cutoff = DateTime.UtcNow - maxAge;
        var removed = 0;
        foreach (var kv in _completes)
            if (kv.Value.At < cutoff && _completes.TryRemove(kv.Key, out _)) removed++;
        return removed;
    }

    private static void EndTranscribing(TranscribingSessions t, string sid)
    {
        try { t.End(sid); } catch { /* the Gateway's stale-mark backstop clears it if this throws */ }
    }

    // Resolve the owning Director's dialable endpoint + current session row (for the exited/moved-on gates).
    private static async Task<(string? endpoint, SessionDto? session)> LocateAsync(
        DirectorRegistry registry, DirectorEndpointClient client, SessionOwnerCache? owners, string sid)
    {
        if (owners?.OwnerOf(sid) is { } ownerId && registry.Get(ownerId) is { } cachedDir && DialEndpoint(cachedDir) is { } cachedEp)
        {
            var s = await client.GetSessionAsync(cachedEp, sid);
            if (s is not null && !IsExited(s)) return (cachedEp, s);
        }
        var (director, session) = await LocateOwningDirectorAsync(registry, client, sid);
        if (director is null || session is null) return (null, null);
        if (IsExited(session)) return (null, session);
        owners?.Remember(sid, director.DirectorId);
        return (DialEndpoint(director), session);
    }

    private static async Task<(DirectorDto? director, SessionDto? session)> LocateOwningDirectorAsync(
        DirectorRegistry registry, DirectorEndpointClient client, string sid)
    {
        var lookups = registry.ListDirectors().Select(async d =>
        {
            var ep = (d.ControlEndpoint ?? "").TrimEnd('/');
            var s = await client.GetSessionAsync(ep, sid);
            return (director: d, session: s);
        }).ToList();
        var results = await Task.WhenAll(lookups);
        foreach (var (director, session) in results)
            if (session is not null) return (director, session);
        return (null, null);
    }

    private static bool IsExited(SessionDto session)
        => string.Equals(session.Status, "Exited", StringComparison.OrdinalIgnoreCase)
        || string.Equals(session.Status, "Failed", StringComparison.OrdinalIgnoreCase)
        || string.Equals(session.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase);

    private static string? DialEndpoint(DirectorDto d)
    {
        var endpoint = !string.IsNullOrWhiteSpace(d.ControlEndpoint) ? d.ControlEndpoint : d.TailnetEndpoint;
        return string.IsNullOrWhiteSpace(endpoint) ? null : endpoint.TrimEnd('/');
    }
}

/// <summary>Register-time body: the session and the client's record-time terminal-byte baseline.</summary>
public sealed class DictationUploadRequest
{
    public string? SessionId { get; set; }
    public long BaselineBufferBytes { get; set; }
}

/// <summary>Complete-time body.</summary>
public sealed class DictationCompleteRequest
{
    public string? SessionId { get; set; }
    public int TotalChunks { get; set; }
    public string? Mime { get; set; }
    public string? Ext { get; set; }
    /// <summary>Typed text before the caret (prepended to the transcript). Empty for the voice case.</summary>
    public string? Before { get; set; }
    /// <summary>Typed text after the caret (appended to the transcript). Empty for the voice case.</summary>
    public string? After { get; set; }
    /// <summary>Earlier paused dictation segments already turned to text, joined ahead of this clip.</summary>
    public string? Prefix { get; set; }
    /// <summary>The session's TotalBufferBytes when the clip was recorded (for the moved-on guard).</summary>
    public long BaselineBufferBytes { get; set; }
    /// <summary>True when this complete is a resume after a reload/relaunch (applies the moved-on guard).</summary>
    public bool Resumed { get; set; }
}

/// <summary>Terminal or retryable outcome of a dictation complete, mapped to an HTTP result.</summary>
internal sealed class DictationOutcome
{
    private enum Kind { Submitted, Error, Incomplete, OutOfCredits }
    private readonly Kind _kind;
    private readonly bool _submitted;
    private readonly bool _movedOn;
    private readonly string _transcript;
    private readonly int _status;
    private readonly string? _error;
    private readonly HostedAiState _creditsState;
    private readonly IReadOnlyList<int> _missing;

    private DictationOutcome(Kind kind, bool submitted = false, bool movedOn = false, string transcript = "",
        int status = 0, string? error = null, HostedAiState creditsState = default, IReadOnlyList<int>? missing = null)
    {
        _kind = kind;
        _submitted = submitted;
        _movedOn = movedOn;
        _transcript = transcript;
        _status = status;
        _error = error;
        _creditsState = creditsState;
        _missing = missing ?? Array.Empty<int>();
    }

    /// <summary>Terminal: the server handled the clip (submitted, moved-on, or empty). Do not retry.</summary>
    public bool Terminal => _kind == Kind.Submitted;

    public static DictationOutcome Submitted(bool submitted, bool movedOn, string transcript)
        => new(Kind.Submitted, submitted: submitted, movedOn: movedOn, transcript: transcript);
    public static DictationOutcome Error(int status, string error) => new(Kind.Error, status: status, error: error);
    public static DictationOutcome Incomplete(IReadOnlyList<int> missing) => new(Kind.Incomplete, missing: missing);
    public static DictationOutcome OutOfCredits(HostedAiState state) => new(Kind.OutOfCredits, creditsState: state);

    public IResult ToResult() => _kind switch
    {
        Kind.Submitted => Results.Json(new { submitted = _submitted, movedOn = _movedOn, transcript = _transcript }),
        Kind.Incomplete => Results.Json(new { status = "incomplete", missing = _missing }, statusCode: StatusCodes.Status409Conflict),
        Kind.OutOfCredits => HostedAiHttp.PaymentRequiredResult(_creditsState),
        _ => Results.Json(new { error = _error }, statusCode: _status),
    };
}
