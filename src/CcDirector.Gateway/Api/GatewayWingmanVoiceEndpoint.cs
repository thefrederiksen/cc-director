using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Core.Voice.Services;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.HostedAi;
using CcDirector.Gateway.Voice;
using CcDirector.Gateway.Wingman;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The wingman-voice surface for the Cockpit's Voice tab (issue #531). Two shapes, both
/// backed by the gateway's one persistent, configured wingman session (the warm brain) via
/// <see cref="WingmanTranslator"/> - never <c>--print</c>:
///
///   POST /sessions/{sid}/wingman/voice-turn  { text }
///        Drive ONE turn of the working session: send the text into the session (on its
///        owning Director), wait for the turn to finish, read the agent's reply, then have
///        the wingman translate it into a faithful, speakable summary. Returns
///        { reply, spoken, replySeconds }. The Voice tab shows the spoken summary silently;
///        the person taps to hear it.
///
///   POST /wingman/ask-direct  { text }
///        The direct-to-wingman path: the person talks to the wingman itself, NOT the
///        working session. Returns { spoken }.
///
/// This is the text-and-voice-shared pipeline: the Text tab and the Voice tab both call
/// /wingman/voice-turn with the person's message (typed, or spoken-then-transcribed), so the
/// text tab proves the translation and the voice tab inherits it unchanged.
/// </summary>
internal static class GatewayWingmanVoiceEndpoint
{
    /// <summary>How long to wait for one working-session turn to finish before giving up.</summary>
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromMinutes(3);

    /// <summary>Let the transcript flush after the session goes quiet before reading the reply.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(2.5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(750);

    // The old per-call TtsMaxChars = 4000 is gone (issue #1612). Its comment claimed "hosted speech
    // endpoints accept bounded input and spoken summaries are short" - both halves are now disproved:
    // the provider accepts 12,000+ characters (measured), our own metered API has no cap at all, and
    // the wingman was writing four-minute essays that the cap was quietly hiding. The runaway guard
    // now lives in Wingman.NarrationText, which tells the listener when it fires.

    /// <summary>
    /// The one HTTP client for the speech leg (issue: hosted-AI client behaviour).
    ///
    /// This used to be <c>using var http = new HttpClient()</c> INSIDE the request handler - a fresh
    /// client, and therefore a fresh connection pool, for every single synthesis. Disposing a client
    /// leaves its socket in TIME_WAIT, so a burst of turns churns through ports and the pool never gets
    /// to reuse a warm TLS connection to the proxy. One shared, never-disposed static is the pattern the
    /// rest of this codebase already uses for hosted calls (AiModelsEndpoint, CarModeChat,
    /// HostedInferenceBrain, ...), and it is safe here because the credential rides on the REQUEST
    /// (TtsSynthesis sets a per-request Authorization header), never on the client's default headers -
    /// so concurrent turns for different keys cannot bleed into each other.
    ///
    /// Timeout is INFINITE on purpose: <see cref="TtsSynthesis"/> owns the deadline (a 15-second
    /// per-attempt cap on a linked CancellationTokenSource, plus one retry). Leaving the client's own
    /// 100-second default in place would create a SECOND, slower deadline authority racing the first -
    /// exactly the kind of ambiguity that made the 2026-07-15 stall so hard to read. One timeout, one
    /// owner. Callers must go through TtsSynthesis, which always supplies the bound.
    /// </summary>
    private static readonly HttpClient SharedTtsHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>413 with a reason the client can show and a support engineer can read.</summary>
    private static IResult TooLarge(string error) =>
        Results.Json(new { error }, statusCode: StatusCodes.Status413PayloadTooLarge);

    /// <summary>
    /// The hosted refusal for the whole /wingman/utterance upload family (issue #1896), or null on
    /// self-host where nothing changes.
    ///
    /// Gated on <see cref="GatewayHostedMode.IsHosted"/> - the INDEPENDENT deployment signal - and NOT on a
    /// boundary or tenant argument being passed in. A security branch that depends on an optional argument
    /// fails OPEN when a caller omits it, which is exactly how the hosted account-status fix nearly shipped
    /// a hole: omit the argument and a hosted Gateway silently takes the self-host path. Asking hosted mode
    /// directly means this family cannot serve another account's transcript on hosted however the host is
    /// wired.
    ///
    /// 404 rather than 403: on hosted this upload family does not exist as a concept - an upload id is
    /// meaningless without a tenant to scope it to, and the store has none - so "not here" is the truthful
    /// answer. 403 would imply the right credential could reach it, and none can.
    /// </summary>
    private static IResult? DenyUtteranceOnHosted()
    {
        if (!GatewayHostedMode.IsHosted) return null;

        FileLog.Write("[GatewayWingmanVoice] DENIED on hosted: the utterance upload store is keyed only by a caller-supplied id under one shared root, so an upload id is not a tenant boundary");
        return Results.Json(
            new { error = "the wingman utterance upload is not available on the hosted gateway" },
            statusCode: StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// Read a request body into memory, giving up the moment it exceeds <paramref name="max"/>.
    ///
    /// The point is the giving up. <c>CopyToAsync(ms)</c> - what these routes used to do - is unbounded
    /// by construction: it faithfully buffers whatever arrives, so the sender chooses how much of this
    /// machine's memory to use. Here the ceiling is ours: nothing beyond <paramref name="max"/> is ever
    /// retained, and the read stops rather than continuing to drain a body we have already refused.
    ///
    /// Returns null when the body is over the cap (the caller answers 413). Never trusts Content-Length -
    /// that is the client's claim about the body, not the body.
    /// </summary>
    private static async Task<byte[]?> ReadBoundedAsync(Stream body, long max, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await body.ReadAsync(buffer, ct);
            if (read == 0) return ms.ToArray();
            if (ms.Length + read > max) return null;   // over: stop now, keep nothing
            ms.Write(buffer, 0, read);
        }
    }

    public static void Map(
        IEndpointRouteBuilder app,
        DirectorRegistry registry,
        Func<WingmanModelRole, CancellationToken, Task<IAgentBrain>> brainProvider,
        KeyVault vault,
        WingmanVoiceService voice,
        Streaming.PushedSessionStore? pushedSessions = null,
        DirectorCommandRouter.SendDirectorCommandAsync? sendCommand = null,
        SessionOwnerCache? owners = null,
        TimeSpan? streamStale = null,
        Func<string>? instructionsProvider = null,
        HttpClient? ttsHttpClient = null,
        Voice.VoiceUploadStore? uploadStore = null,
        Transcription.TranscriptionTelemetryLog? telemetry = null,
        Transcription.TranscriptionAudioArchive? audioArchive = null,
        Tenancy.HostedTenantBoundary? tenantBoundary = null)
    {
        // The speech transport: the shared static in production, an injected stub in a test. This is the
        // same seam WingmanVoiceService already exposes for its narration leg (ttsHttpClient) and it
        // exists for the same reason - the upstream base URL is a compile-time const, so without it the
        // status mapping below could only be proven by calling the real provider over the internet.
        var ttsHttp = ttsHttpClient ?? SharedTtsHttp;

        var translator = new WingmanTranslator(brainProvider, instructionsProvider: instructionsProvider);

        // Post-cut: resolve the owning Director once (from the push store) and reach its session verbs
        // (turns / buffer / prompt) through the tunnel-only SessionVerbClient, so the wingman voice surface
        // never HTTP-dials the Director.
        var stale = streamStale ?? TimeSpan.FromSeconds(GatewayConfig.DefaultStreamStaleAfterSeconds);
        Task<SessionVerbClient?> ResolveRouteAsync(TenantId tenant, string sid) =>
            SessionVerbClient.ResolveAsync(sid, tenant, registry, pushedSessions, stale, owners, sendCommand);

        // The session's title, which the wingman speaks before the summary so a listener who cannot
        // see the screen knows which session is talking (WingmanTranslator.FidelityPrompt v5.2). Same
        // push-store read as ResolveRouteAsync above - no dial. Null (unknown session, or no name) is
        // the honest answer and simply means no title is spoken; see GatewayHost.ResolveSessionTitle.
        string? SessionTitle(TenantId tenant, string sid)
        {
            var name = pushedSessions?.TryLocate(tenant, sid, stale)?.Session.Name;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        // The single Gateway owner of speech-to-text (issue #839): both batch transcribe paths below
        // (the resumable /wingman/utterance/complete and the one-shot /wingman/transcribe) go through
        // it, so they resolve the mode + key and pick the hosted endpoint exactly the same way every
        // other batch caller does - no second resolver.
        var transcription = new Transcription.GatewayTranscriptionService(vault, telemetry: telemetry, audioArchive: audioArchive);

        // Which voice sessions have a ready, playable spoken summary right now (the phone's list
        // shows a play button on these and can play without entering).
        app.MapGet("/wingman/voice/ready", (HttpContext ctx) =>
        {
            var reqTenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            return Results.Json(new { sids = voice.ReadySessionIds(reqTenant.Value) });
        });

        // The precomputed spoken summary for a session (instant on entry - no re-read needed).
        app.MapGet("/sessions/{sid}/wingman/voice", (string sid, HttpContext ctx) =>
        {
            var reqTenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            var v = voice.Get(reqTenant.Value, sid);
            return v is null
                ? Results.Json(new { ready = false })
                : Results.Json(new { ready = true, spoken = v.Spoken, reply = v.Reply, generatedAt = v.AtUtc });
        });

        // The precomputed audio for a session - streamed so the list can play it with one tap.
        app.MapGet("/sessions/{sid}/wingman/voice/audio", (string sid, HttpContext ctx) =>
        {
            var reqTenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            var audio = voice.GetAudio(reqTenant.Value, sid);
            return audio is { Length: > 0 }
                ? Results.Bytes(audio, voice.GetAudioContentType(reqTenant.Value, sid) ?? "audio/mpeg", enableRangeProcessing: true)
                : Results.Json(new { error = "no voice ready for this session" }, statusCode: StatusCodes.Status404NotFound);
        });

        // Turn voice off for a session (issue #859): unmark it as a voice session so the gateway
        // STOPS spending the per-turn Opus translation + hosted text-to-speech on it. This is the
        // counterpart to the marking that POST /sessions/{sid}/wingman/explain performs on entry; the
        // phone's "Turn voice off" calls it alongside the Director's /voice-mode { enabled:false }.
        // Gateway-side only and read-only - it clears the voice marker + cached clip and sends nothing
        // into the session. Idempotent: stopping a session that was not a voice session is a no-op 200.
        app.MapPost("/sessions/{sid}/wingman/voice/stop", (string sid, HttpContext ctx) =>
        {
            FileLog.Write($"[GatewayWingmanVoice] voice/stop sid={sid}");
            var reqTenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest);
            voice.Unmark(reqTenant.Value, sid);
            return Results.Json(new { stopped = true });
        });

        // Turn voice mode on or off for EVERY session at once (issue #1765). The fleet-wide counterpart
        // to the per-session pair the phone and Cockpit fire (POST /voice-mode on the owning Director,
        // plus mark/unmark here): ONE call, and the Gateway walks the whole roster so the caller never
        // loops it itself. The person's own use is "as I leave the house, put my whole fleet on voice;
        // when I get home, take it all off". Each session gets the SAME two effects the single path gives:
        //   - the owning Director's voice-mode flag is set over the tunnel (ViewMode = Voice, so the roster
        //     flag flips and the session persists as a voice session across navigation), and
        //   - the Gateway voice marker is set (enable) or cleared (disable), so the per-turn narration
        //     starts or stops.
        // A session whose owning computer is not tunnel-connected is SKIPPED and reported, never failing
        // the whole batch - the same "that computer is offline" story the single path tells. The
        // Director-side writes run concurrently (each is its own tunnel round-trip); the Gateway-side
        // mark/unmark is then applied SEQUENTIALLY so the persisted voice-session file is written on one
        // thread and never raced. Idempotent: enabling a session already on, or disabling one already off,
        // is a harmless no-op. This endpoint sends NO prompt into any session - it only sets voice marking,
        // exactly like the single-session /voice-mode and /wingman/voice/stop it fans out to.
        app.MapPost("/sessions/voice-mode/all", async (VoiceModeAllRequest? req, HttpContext ctx, CancellationToken ct) =>
        {
            var reqTenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            var enabled = req?.Enabled ?? true;
            FileLog.Write($"[GatewayWingmanVoice] voice-mode/all requested: enabled={enabled}");

            // The machine each Director runs on, so a skipped session names where it lives in plain English.
            var machineByDirector = registry.ListDirectors()
                .ToDictionary(d => d.DirectorId, d => d.MachineName, StringComparer.Ordinal);

            // Every session the Gateway can see right now, de-duplicated by id. A session belongs to exactly
            // one Director, but the guard keeps a duplicated roster entry from being toggled (and counted) twice.
            // Hosted Multi-Tenancy: voice-mode/all is a fleet fan-out WRITE (voice family), scoped to the
            // caller's tenant resolved from its authenticated device key - so it toggles only the requester's
            // own sessions, never another tenant's, and a request with no bound tenant is denied above.
            var byDirector = GatewayEndpoints.FleetByDirector(registry, pushedSessions, stale, reqTenant.Value);
            var targets = new List<(string DirectorId, string Machine, string Sid, string? Name)>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (directorId, sessions) in byDirector)
                foreach (var s in sessions)
                    if (!string.IsNullOrEmpty(s.SessionId) && seen.Add(s.SessionId))
                        targets.Add((directorId, machineByDirector.GetValueOrDefault(directorId, ""), s.SessionId, s.Name));

            // Set the Director-side flag on every session concurrently: each is an independent tunnel
            // round-trip, so the batch finishes in the slowest single session's time, not the sum.
            var sends = await Task.WhenAll(targets.Select(async t =>
            {
                var result = await DirectorCommandRouter.TrySendAsync(
                    sendCommand, t.DirectorId, "voice-mode", t.Sid, new { enabled }, ct,
                    machineName: string.IsNullOrEmpty(t.Machine) ? null : t.Machine);
                return (Target: t, Result: result);
            }));

            // Apply the Gateway-side mark/unmark sequentially (one thread writes the persisted set), and
            // build the per-session report so the caller sees exactly what changed and what was skipped.
            var sessionResults = new List<object>(sends.Length);
            var changed = 0;
            foreach (var (t, result) in sends)
            {
                var ok = result is { Ok: true };
                if (ok)
                {
                    if (enabled) voice.Mark(reqTenant.Value, t.Sid); else voice.Unmark(reqTenant.Value, t.Sid);
                    changed++;
                }
                var reason = ok
                    ? null
                    : result is null
                        ? string.IsNullOrEmpty(t.Machine)
                            ? "This session's computer looks offline."
                            : $"{t.Machine} looks offline."
                        : DirectorCommandRouter.DescribeFailure(result);
                sessionResults.Add(new { sessionId = t.Sid, name = t.Name, ok, reason });
            }

            FileLog.Write($"[GatewayWingmanVoice] voice-mode/all done: enabled={enabled}, total={sends.Length}, changed={changed}, skipped={sends.Length - changed}");
            return Results.Json(new
            {
                enabled,
                total = sends.Length,
                changed,
                skipped = sends.Length - changed,
                sessions = sessionResults,
            });
        });

        // Resumable, idempotent piece-by-piece upload store (the same one the native app path uses):
        // chunks land on disk under a stable upload id and survive between retry attempts, so the
        // phone can keep re-sending pieces until the whole recording is through. In production the host
        // owns this instance and passes it; when omitted it defaults to a fresh one over the same root.
        var uploads = uploadStore ?? new VoiceUploadStore();

        // ===== Resumable transcription upload (issue #531: drive-safe, keeps trying) =====
        // The phone records locally (works offline), then ships the recording in pieces here and
        // keeps retrying until every piece lands - no user buttons. When all pieces are in, the
        // assembled audio is transcribed, the validated dictionary correction is applied (the same
        // engine every other surface uses; raw is returned in local mode or on any cleanup error),
        // and the corrected text is returned.
        //   POST   /wingman/utterance/upload                  -> { upload_id }
        //   PUT    /wingman/utterance/{id}/chunk/{i}           -> { ok }   (idempotent)
        //   POST   /wingman/utterance/{id}/complete {total}    -> { transcript } | 409 { missing }
        //
        // DENIED IN WHOLE ON HOSTED (issue #1896). None of these three resolves a tenant at all, the upload
        // id is CALLER-CHOSEN (it arrives as an Idempotency-Key header), the store behind them has one
        // on-disk root shared across every account with no tenant segment in the path, and `complete`
        // returns the assembled transcript. So an account can claim another account's upload id and be
        // handed the words that account spoke - and, before the owner completes, overwrite its chunks.
        // A caller-supplied identifier is not a tenant boundary: secrecy of an identifier is not
        // authorization, and upload ids travel through client logs, retries and store-and-forward queues.
        //
        // The whole family, not just `complete`: denying only the leg that returns text would leave the
        // chunk leg able to corrupt another account's recording, which is the same boundary failure with a
        // different consequence. It is a deny rather than a partition because the staged records carry no
        // tenant to partition BY - partitioning the store is issue #1896's job and un-denying is gated on
        // it. It refuses rather than returning an empty transcript, because an empty transcript is a FALSE
        // statement ("you said nothing") where a refusal is merely an absent one.
        //
        // Self-host is COMPLETELY unchanged: one tenant, so a shared root is the owner's own root, and the
        // phone's record-locally-and-keep-retrying path works exactly as it always has.
        //
        // The group prefix reproduces the route paths character for character, so the self-host surface is
        // byte-identical; the filter also covers legs added to this family later, which a per-route guard
        // would not.
        var utterance = app.MapGroup("/wingman/utterance");
        utterance.AddEndpointFilter(async (ctx, next) =>
        {
            if (DenyUtteranceOnHosted() is { } denied) return denied;
            return await next(ctx);
        });

        utterance.MapPost("/upload", (HttpContext ctx) =>
        {
            var key = ctx.Request.Headers["Idempotency-Key"].ToString();
            var id = uploads.Register(string.IsNullOrWhiteSpace(key) ? null : key);
            return Results.Json(new { upload_id = id });
        });

        utterance.MapPut("/{uploadId}/chunk/{index:int}", async (string uploadId, int index, HttpContext ctx, CancellationToken ct) =>
        {
            if (!uploads.Exists(uploadId))
                return Results.Json(new { error = "unknown upload id (register it first)" }, statusCode: StatusCodes.Status404NotFound);

            // Refuse an oversized chunk BEFORE reading a byte of it, when the client declares its size.
            // This is the cheap door: no allocation, no read, and the client learns immediately.
            if (ctx.Request.ContentLength is { } declared && declared > VoiceUploadLimits.MaxChunkBytes)
            {
                FileLog.Write($"[GatewayWingmanVoice] chunk rejected: declared {declared} bytes > {VoiceUploadLimits.MaxChunkBytes} cap (upload={uploadId}, index={index})");
                return TooLarge($"chunk is {declared} bytes; the limit is {VoiceUploadLimits.MaxChunkBytes}");
            }

            var sha = ctx.Request.Headers["X-Chunk-Sha256"].ToString();

            // Content-Length is the client's CLAIM. It can be absent (chunked transfer-encoding) and it
            // can be wrong, so the read itself is bounded too: this stops the moment the body exceeds
            // the cap instead of faithfully buffering however much someone chooses to send.
            var bytes = await ReadBoundedAsync(ctx.Request.Body, VoiceUploadLimits.MaxChunkBytes, ct);
            if (bytes is null)
            {
                FileLog.Write($"[GatewayWingmanVoice] chunk rejected: body exceeded the {VoiceUploadLimits.MaxChunkBytes}-byte cap (upload={uploadId}, index={index})");
                return TooLarge($"chunk exceeded the {VoiceUploadLimits.MaxChunkBytes}-byte limit");
            }

            // Total across the whole upload. Excluding THIS index is what keeps a resend free: the
            // client's retry replaces chunk N, it does not add a second copy of it, and retrying is the
            // normal case on this path.
            var staged = uploads.StagedBytes(uploadId, excludeIndex: index);
            if (staged + bytes.Length > VoiceUploadLimits.MaxTotalUploadBytes)
            {
                FileLog.Write($"[GatewayWingmanVoice] chunk rejected: upload total would be {staged + bytes.Length} > {VoiceUploadLimits.MaxTotalUploadBytes} cap (upload={uploadId})");
                return TooLarge($"upload would exceed the {VoiceUploadLimits.MaxTotalUploadBytes}-byte total limit");
            }

            try { await uploads.StoreChunkAsync(uploadId, index, bytes, string.IsNullOrEmpty(sha) ? null : sha, ct); return Results.Json(new { ok = true, index }); }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest); }
        });

        utterance.MapPost("/{uploadId}/complete", async (string uploadId, UtteranceCompleteRequest? req, CancellationToken ct) =>
        {
            if (req is null || req.TotalChunks <= 0)
                return Results.Json(new { error = "totalChunks (>0) is required" }, statusCode: StatusCodes.Status400BadRequest);
            if (!uploads.Exists(uploadId))
                return Results.Json(new { error = "unknown upload id (register it first)" }, statusCode: StatusCodes.Status404NotFound);

            // The configured mode's key must be present (issue #887: both modes are key-bearing). The
            // single transcription owner resolves this; check it BEFORE assembling so a no-key request
            // does not pay the reassembly cost.
            var routing = transcription.Resolve();
            if (routing.Key is null)
                return Results.Json(new { error = $"no key configured for transcription mode {routing.Mode.ToConfigString()}" }, statusCode: StatusCodes.Status503ServiceUnavailable);

            AssembleResult assembled;
            try { assembled = await uploads.AssembleAsync(uploadId, req.TotalChunks, ct); }
            catch (Exception ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest); }

            if (assembled.Status == "unknown_upload")
                return Results.Json(new { error = "unknown upload id" }, statusCode: StatusCodes.Status404NotFound);
            if (assembled.Status == "incomplete")
                return Results.Json(new { status = "incomplete", missing = assembled.Missing }, statusCode: StatusCodes.Status409Conflict);

            var assembledAudio = assembled.Audio;
            if (assembledAudio is null || assembledAudio.Length == 0)
            {
                uploads.Delete(uploadId);
                return Results.Json(new { error = "assembled recording was empty" }, statusCode: StatusCodes.Status502BadGateway);
            }

            // Transcribe through the single owner WITH the validated dictionary correction applied (the
            // SAME engine every other surface uses; fails open to raw in local mode or on any error).
            var result = await transcription.TranscribeAsync(
                assembledAudio, "audio." + (req.Ext ?? "webm"), req.Mime ?? "audio/webm", applyCorrection: true, ct);
            uploads.Delete(uploadId);
            // Out of credits / monthly cap (issue #939): map to the shared 402 state (branch by code)
            // instead of flattening it into a generic 502 - so the client shows the consistent
            // add-credits message and keeps the recording, not "transcription failed".
            if (result.Outcome == Transcription.TranscriptionOutcome.OutOfCredits)
                return HostedAiHttp.PaymentRequiredResult(HostedAiErrorMapper.MapCode(result.Code));
            if (result.Outcome != Transcription.TranscriptionOutcome.Ok)
                return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status502BadGateway);
            FileLog.Write($"[GatewayWingmanVoice] utterance complete {uploadId}: mode={result.Mode}, chars={result.Text?.Length ?? 0}");

            // Capture-health persistence (issue #863): when the mobile dialog sent its measurements,
            // record the audio-loss deficit (recording wall-clock vs decoded audio duration) into the
            // same dictation session log the desktop and overlay write, tagged Source="mobile". The
            // assembled WAV byte count is the audio the server actually transcribed. Fire-and-forget.
            PersistMobileCaptureHealth(uploadId, req, assembledAudio.Length, result.Text);

            return Results.Json(new { transcript = result.Text });
        });

        // Text-to-speech for the mobile Voice screen + Cockpit: turn the wingman's spoken summary into
        // natural-sounding audio (the browser's own voice is robotic). Returns audio/mpeg bytes the
        // page plays in an <audio> element. DevThrottle routes to the hosted proxy's
        // provider-compatible /audio/speech. The voice is the user's choice (TtsVoiceConfig); a
        // request Voice overrides it. The credential comes from the gateway key vault.
        app.MapPost("/wingman/tts", async (WingmanTtsRequest? req, HttpContext ctx, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.Json(new { error = "text is required" }, statusCode: StatusCodes.Status400BadRequest);

            var mode = TranscriptionModeConfig.Get();
            var tts = TranscriptionEndpointResolver.ResolveTts(mode);
            var key = vault.Get(tts.KeyName);
            if (string.IsNullOrWhiteSpace(key))
                return Results.Json(new { error = "no DevThrottle account key configured in the gateway vault - sign in to DevThrottle" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            // Read-aloud of caller-supplied text. It is metered and the caller pays, so there is no
            // product limit here - only the runaway guard, which announces itself when it fires
            // (issue #1612). This used to cut at 4000 characters, silently and mid-word, to satisfy
            // OpenAI's limit long after we stopped calling OpenAI.
            var input = Wingman.NarrationText.LimitForSpeech(req.Text, out var wasCut);
            if (wasCut)
                FileLog.Write($"[GatewayWingmanVoice] tts text EXCEEDED {Wingman.NarrationText.MaxChars} chars " +
                              $"({req.Text.Length}) - spoken text cut and the listener told");
            var voice = string.IsNullOrWhiteSpace(req.Voice) ? TtsVoiceConfig.Resolve(mode) : req.Voice.Trim();
            var model = string.IsNullOrWhiteSpace(req.Model) ? TtsModelConfig.Resolve(mode) : req.Model.Trim();
            var url = tts.BaseUrl.TrimEnd('/') + "/audio/speech";
            // Time the read-aloud call (request -> done) so its speed is in the log, matching the
            // narration path (WingmanVoiceService) and transcription's transcribeMs.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // Per-attempt deadline derived from the text length + one retry (TtsSynthesis), so a
                // stalled upstream voice worker fails fast and retries instead of freezing the caller,
                // while a legitimately long read still gets the time it actually needs.
                // The client is the shared static (see SharedTtsHttp) - never a per-call one.
                // preferBackup is false here: the silent-primary backup routing (issue devthrottle_internal#405) is driven by
                // the per-session narration path (WingmanVoiceService), which owns the sticky state this
                // interactive read-aloud endpoint does not carry. It still gets the proxy's own failover.
                using var resp = await TtsSynthesis.PostAsync(ttsHttp, url, key, new { model, voice, input, response_format = "mp3" }, input.Length, preferBackup: false, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    var status = (int)resp.StatusCode;
                    var body = await resp.Content.ReadAsStringAsync(ct);
                    FileLog.Write($"[GatewayWingmanVoice] tts {mode.ToConfigString()} {status}: {body[..Math.Min(200, body.Length)]}");

                    // Out of credits / monthly cap (issue #939): map to the shared 402 state by code
                    // instead of a generic "text-to-speech returned 402".
                    if (status == HostedAiHttp.PaymentRequired)
                        return HostedAiHttp.PaymentRequiredResult(HostedAiErrorMapper.Map402(body));

                    // A 429 or a 503 is the far end telling the caller to come back later, and it often
                    // says HOW MUCH later. Both used to be flattened into a bare 502 with the hint
                    // dropped on the floor: the cloud proxy deliberately forwards the upstream status and
                    // Retry-After verbatim (it was rewritten for exactly this on 2026-07-15), and this
                    // endpoint then destroyed the evidence one hop later. A client cannot back off
                    // correctly against a status that says "the gateway is broken" when the truth is
                    // "wait four seconds" - it just retries into the same wall.
                    //
                    // This is not the Gateway inventing a policy: it does NOT sleep, retry, or decide
                    // anything here. It passes the far end's own answer through, which is the only layer
                    // that knows when to come back. WingmanVoiceService already honours this header on
                    // the narration leg (via the same RetryAfterHeader); now the endpoint does too, so
                    // the two legs cannot drift.
                    //
                    // KNOWN OVERLAP: this route already answers 503 for "no key in the vault" (above).
                    // An upstream 503 now shares that status. They are distinguishable - the transient
                    // one carries Retry-After and says "rate limited or unavailable", the setup one says
                    // "sign in to DevThrottle" - and a client that reads the body cannot confuse them.
                    // Left as-is deliberately: the setup case arguably wants a different status
                    // entirely, but that is a client-visible contract change and does not belong in a
                    // fix whose whole point is to stop DESTROYING information.
                    if (status is 429 or StatusCodes.Status503ServiceUnavailable)
                    {
                        // Normalized to seconds: RetryAfterHeader accepts both wire forms (a delta and an
                        // HTTP date) and a browser reading this should not have to.
                        if (RetryAfterHeader.Parse(resp.Headers) is { } wait)
                            ctx.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                        FileLog.Write($"[GatewayWingmanVoice] tts {status} passed through" +
                            $"{(ctx.Response.Headers.RetryAfter.Count > 0 ? $" (Retry-After {ctx.Response.Headers.RetryAfter})" : " (no Retry-After hint)")}");
                        return Results.Json(new { error = $"text-to-speech is rate limited or unavailable ({status})" },
                            statusCode: status);
                    }

                    return Results.Json(new { error = $"text-to-speech returned {status}" },
                        statusCode: StatusCodes.Status502BadGateway);
                }
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                // Pass the upstream content type through: speech providers usually return audio/mpeg.
                // may return audio/wav for some models - the browser must be told which so it can play it.
                var contentType = resp.Content.Headers.ContentType?.MediaType ?? "audio/mpeg";
                sw.Stop();
                FileLog.Write($"[GatewayWingmanVoice] tts ok: elapsedMs={sw.ElapsedMilliseconds}, provider={mode.ToConfigString()}, chars={input.Length}, bytes={bytes.Length}, model={model}, voice={voice}, type={contentType}");
                return Results.Bytes(bytes, contentType);
            }
            // TtsSynthesis exhausted its attempts: the worker never answered inside the per-attempt cap.
            // That is a GATEWAY TIMEOUT (504), not a bad gateway (502). The two used to be the same 502,
            // which is the collapse that makes an outage unreadable from the outside: "the upstream
            // returned an error" and "the upstream never answered" want different responses from a
            // client and different answers from support, so they must not share a status.
            catch (TimeoutException ex)
            {
                sw.Stop();
                FileLog.Write($"[GatewayWingmanVoice] tts TIMED OUT (elapsedMs={sw.ElapsedMilliseconds}): {ex.Message}");
                return Results.Json(new { error = "text-to-speech timed out: " + ex.Message },
                    statusCode: StatusCodes.Status504GatewayTimeout);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayWingmanVoice] tts FAILED: {ex.Message}");
                return Results.Json(new { error = "text-to-speech failed: " + ex.Message },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapPost("/sessions/{sid}/wingman/voice-turn", async (string sid, WingmanVoiceTurnRequest? req, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}, textLen={req?.Text?.Length ?? 0}");
            var reqTenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest);
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.Json(new { error = "text is required" }, statusCode: StatusCodes.Status400BadRequest);

            var route = await ResolveRouteAsync(reqTenant.Value, sid);
            if (route is null)
                return Results.Json(new { error = "session not found on any director" }, statusCode: StatusCodes.Status404NotFound);

            // THE FLOOR INVARIANT (issue #1777). This branch NEVER presses a key into a session. The spoken
            // words are typed as an ordinary prompt ONLY when the live screen is a CONFIDENT plain-text
            // composer: not the alternate screen, the hardware cursor VISIBLE, the cursor within the framed
            // composer's input column span, and NO menu marker or menu-ish structure anywhere on the grid.
            // Every menu, and every uncertain / unreadable / alternate-screen / hidden-cursor screen, types
            // NOTHING and presses NOTHING. (Fully hands-free voice-ANSWERING - the wingman pressing menu keys -
            // is its own later issue with per-agent picker profiles and a screen-version lock; it is not here.)
            var (kind, blockedSpoken) = await ClassifyLiveScreenAtAsync(route, sid, ct);
            if (kind != WaitingScreenKind.PlainText)
            {
                FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}: FAIL CLOSED - not typing (screen is {kind})");
                return Results.Json(new { reply = "", spoken = blockedSpoken, cannotType = true, lookAtTerminal = true });
            }

            // Close the snapshot-to-send race (issue #1777, floor rescope, finding 2): RE-READ the live screen
            // immediately before the send and re-confirm it is STILL a confident plain-text composer. If it
            // changed in the meantime (now a menu / alternate screen / cursor hidden / gone), fail closed - the
            // spoken words must never land on anything but the composer they were classified against.
            var (kindNow, blockedNow) = await ClassifyLiveScreenAtAsync(route, sid, ct);
            if (kindNow != WaitingScreenKind.PlainText)
            {
                FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}: screen changed before send (now {kindNow}) - fail closed, not typing");
                return Results.Json(new { reply = "", spoken = blockedNow, cannotType = true, lookAtTerminal = true });
            }

            // We are about to start a new turn, so the cached spoken summary + audio are now stale.
            // Clear them DETERMINISTICALLY here (do not rely on observing the Working state, which is
            // racy for fast turns) - the list stops showing it ready and nothing stale plays. The
            // fresh summary is stored below once the agent replies.
            voice.OnSessionWorking(reqTenant.Value, sid);

            // Snapshot the widget list BEFORE sending: gives both (a) the count for the issue #366
            // guard (only read widgets that are new after the send) and (b) the prior conversation
            // for the wingman so it can resolve references in the agent's reply. The current question
            // is appended below; BuildRecentContext is called on the pre-send snapshot so it
            // excludes the new turn and includes only what came before.
            var snapshotWidgets = (await route.GetTurnsAsync(sid, ct))?.Widgets
                ?? new List<TurnWidgetDto>();
            var widgetsBefore = snapshotWidgets.Count;
            var priorContext = WingmanTranslator.BuildRecentContext(snapshotWidgets);

            var (ok, _, sendErr) = await route.PostPromptAsync(sid, new PromptRequest { Text = req.Text, AppendEnter = true }, ct);
            if (!ok)
                return Results.Json(new { error = "send failed: " + sendErr }, statusCode: StatusCodes.Status502BadGateway);

            var reply = await WaitForReplyAsync(route, sid, widgetsBefore, ct);
            if (string.IsNullOrWhiteSpace(reply))
                return Results.Json(new { error = "the agent did not produce a reply in time" }, statusCode: StatusCodes.Status504GatewayTimeout);

            // The agent replied; now the wingman translates it. This is gateway-owned work
            // (CancellationToken.None) so navigating away does not lose the summary, and the
            // session shows YELLOW while the wingman runs, then back to red (issue #531 voice mode).
            voice.BeginGenerating(reqTenant.Value, sid);
            try
            {
                // Full context: prior exchanges from the pre-send snapshot + the current question,
                // so the wingman can resolve references like "that file" or "the bug I mentioned".
                var recentContext = string.IsNullOrWhiteSpace(priorContext)
                    ? "You: " + req.Text.Trim()
                    : priorContext + "\n\nYou: " + req.Text.Trim();
                var t = await translator.TranslateAsync(recentContext, reply, SessionTitle(reqTenant.Value, sid), CancellationToken.None);
                await voice.StoreSpokenAsync(reqTenant.Value, sid, t.Spoken, reply, CancellationToken.None);   // make it a voice session + cache audio
                FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}: replyLen={reply.Length}, spokenLen={t.Spoken.Length}");
                // Training capture (no-op unless the setting is on); fire-and-forget so it adds no latency.
                _ = voice.CaptureTrainingAsync(route, sid, "voice-turn", reply, recentContext, t.Spoken, t.ReplySeconds, CancellationToken.None);
                return Results.Json(new { reply, spoken = t.Spoken, replySeconds = t.ReplySeconds });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid} translate FAILED: {ex.Message}");
                return Results.Json(new { error = "wingman translation failed: " + ex.Message },
                    statusCode: StatusCodes.Status502BadGateway);
            }
            finally { voice.EndGenerating(reqTenant.Value, sid); }
        });

        // Transcription (issue #531 follow-up): the phone records audio locally (survives a bad
        // connection / reload), then ships the recording here to be transcribed - the same
        // record-then-ship-then-transcribe shape the native mobile app uses. Robustness lives on
        // the CLIENT (save-first + retry the upload); this endpoint is the single transcribe step.
        // Audio arrives as a multipart 'audio' file; the raw transcript then runs through the
        // validated dictionary correction (raw is returned in local mode or on any cleanup error).
        // Returns { transcript }.
        app.MapPost("/wingman/transcribe", async (HttpContext ctx, CancellationToken ct) =>
        {
            if (!ctx.Request.HasFormContentType)
                return Results.Json(new { error = "send the recording as multipart form-data with an 'audio' file" },
                    statusCode: StatusCodes.Status400BadRequest);

            var form = await ctx.Request.ReadFormAsync(ct);
            var file = form.Files.GetFile("audio") ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.Json(new { error = "no audio in the upload" }, statusCode: StatusCodes.Status400BadRequest);

            // Bound the clip before it is copied into memory below. This route has no resume and buffers
            // the file whole, so it is the wrong door for a long recording - the resumable chunk path is
            // the right one, and saying so beats quietly allocating whatever was sent.
            if (file.Length > VoiceUploadLimits.MaxOneShotFileBytes)
            {
                FileLog.Write($"[GatewayWingmanVoice] transcribe rejected: {file.Length} bytes > {VoiceUploadLimits.MaxOneShotFileBytes} cap");
                return TooLarge($"audio is {file.Length} bytes; the limit for this endpoint is " +
                    $"{VoiceUploadLimits.MaxOneShotFileBytes}. Use the resumable upload for long recordings.");
            }

            // Both modes (byo/devthrottle) require the configured key to be present in the vault
            // (issue #887). The single transcription owner resolves this and runs the right provider.
            var routing = transcription.Resolve();
            if (routing.Key is null)
                return Results.Json(new { error = $"no key configured for transcription mode {routing.Mode.ToConfigString()}" },
                    statusCode: StatusCodes.Status503ServiceUnavailable);

            byte[] bytes;
            using (var ms = new MemoryStream()) { await file.CopyToAsync(ms, ct); bytes = ms.ToArray(); }
            var fileName = string.IsNullOrWhiteSpace(file.FileName) ? "audio.webm" : file.FileName;
            var contentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType;

            // Transcribe WITH the validated dictionary correction applied (the SAME engine every other
            // surface uses; fails open to the raw transcript in local mode or on any cleanup error).
            var result = await transcription.TranscribeAsync(bytes, fileName, contentType, applyCorrection: true, ct);
            // Out of credits / monthly cap (issue #939): map to the shared 402 state (branch by code)
            // instead of flattening it into a generic 502 - so the client shows the consistent
            // add-credits message and keeps the recording, not "transcription failed".
            if (result.Outcome == Transcription.TranscriptionOutcome.OutOfCredits)
                return HostedAiHttp.PaymentRequiredResult(HostedAiErrorMapper.MapCode(result.Code));
            if (result.Outcome != Transcription.TranscriptionOutcome.Ok)
                return Results.Json(new { error = result.Error }, statusCode: StatusCodes.Status502BadGateway);
            return Results.Json(new { transcript = result.Text });
        });

        // Read-only "explain what's happening" (issue #531): the wingman reads the session's
        // LAST completed turn and speaks a faithful summary of it - WITHOUT sending anything into
        // the session. This is what the mobile Voice screen fires the moment you open a session,
        // so you get a spoken summary even though a normal (text) session never produced voice.
        app.MapPost("/sessions/{sid}/wingman/explain", async (string sid, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayWingmanVoice] explain sid={sid}");
            var reqTenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest);

            var route = await ResolveRouteAsync(reqTenant.Value, sid);
            if (route is null)
                return Results.Json(new { error = "session not found on any director" }, statusCode: StatusCodes.Status404NotFound);

            var turns = await route.GetTurnsAsync(sid, ct);
            var widgets = turns?.Widgets ?? new List<TurnWidgetDto>();
            var lastReply = widgets.LastOrDefault(w => w.Kind == "Text")?.Content;
            // Recent conversation so the wingman can give context to a short/terse reply.
            var recentContext = WingmanTranslator.BuildRecentContext(widgets);

            voice.Mark(reqTenant.Value, sid);   // opening voice on a session makes it a voice session (kept fresh on turn-end)
            if (string.IsNullOrWhiteSpace(lastReply))
            {
                // No text reply to read aloud (waiting on a prompt / menu). Record the honest "nothing to
                // narrate" fact so the Voice screen shows it via VoiceDisplayFold instead of a dead-end
                // Generate button, then return the truthful canned line - no brain call.
                voice.SetNothingToNarrate(reqTenant.Value, sid, true);
                return Results.Json(new
                {
                    reply = "",
                    spoken = "This session has not produced anything to summarize yet. Ask it something and I will read the answer back to you.",
                    replySeconds = 0.0,
                    nothingYet = true,
                });
            }

            // The GATEWAY owns this work, not the page (issue #531 voice mode): run the translation
            // and synthesis on CancellationToken.None so it COMPLETES and caches even if the phone
            // navigates away or the request is abandoned mid-read - returning to the session then
            // loads the finished summary from cache instead of losing it. Mark the session generating
            // so it shows YELLOW ("not ready yet") for the duration, then back to red.
            voice.SetNothingToNarrate(reqTenant.Value, sid, false);   // there IS a text reply - clear any stale "nothing to narrate"
            voice.BeginGenerating(reqTenant.Value, sid);
            try
            {
                var t = await translator.TranslateAsync(recentContext, lastReply, SessionTitle(reqTenant.Value, sid), CancellationToken.None);
                await voice.StoreSpokenAsync(reqTenant.Value, sid, t.Spoken, lastReply, CancellationToken.None);   // cache spoken + audio, ready to play
                FileLog.Write($"[GatewayWingmanVoice] explain sid={sid}: replyLen={lastReply.Length}, spokenLen={t.Spoken.Length}");
                // Training capture (no-op unless the setting is on); fire-and-forget so it adds no latency.
                _ = voice.CaptureTrainingAsync(route, sid, "explain", lastReply, recentContext, t.Spoken, t.ReplySeconds, CancellationToken.None);
                return Results.Json(new { reply = lastReply, spoken = t.Spoken, replySeconds = t.ReplySeconds });
            }
            catch (Exception ex) when (ex is TimeoutException or HttpRequestException)
            {
                // The model leg did not answer in time (bounded timeout) or the transport failed. This is
                // the absence of an answer, not evidence the session's computer is offline - so record the
                // calm Retrying state (the phone shows "voice on its way" and the sweep keeps trying) and
                // return a benign 200, NOT the 502 the phone used to mislabel "this session's computer looks
                // offline". Before this, a stalled model hung the request the full 180s and then 502'd, which
                // is exactly the "I hit generate and nothing happens" the owner reported.
                voice.NoteRetrying(reqTenant.Value, sid);
                FileLog.Write($"[GatewayWingmanVoice] explain sid={sid} model did not answer: {ex.Message} - Retrying (audio on its way)");
                return Results.Json(new
                {
                    reply = "",
                    spoken = "Voice is taking a moment - it will keep trying.",
                    replySeconds = 0.0,
                    retrying = true,
                });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayWingmanVoice] explain sid={sid} FAILED: {ex.Message}");
                return Results.Json(new { error = "wingman could not summarize: " + ex.Message },
                    statusCode: StatusCodes.Status502BadGateway);
            }
            finally { voice.EndGenerating(reqTenant.Value, sid); }
        });

        app.MapPost("/wingman/ask-direct", async (WingmanVoiceTurnRequest? req, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayWingmanVoice] ask-direct textLen={req?.Text?.Length ?? 0}");
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.Json(new { error = "text is required" }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var t = await translator.AskDirectAsync(req.Text, ct);
                return Results.Json(new { spoken = t.Spoken, replySeconds = t.ReplySeconds });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayWingmanVoice] ask-direct FAILED: {ex.Message}");
                return Results.Json(new { error = "wingman failed: " + ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // The DevThrottle product/docs Q&A path (issue #472): the Cockpit Learning page posts a
        // free-text question ABOUT THE PRODUCT here and the warm brain answers it, grounded in a
        // DevThrottle system prompt. The Cockpit talks only to the Gateway, never a Director - this
        // is that Gateway endpoint. Same warm brain as ask-direct, different grounding.
        app.MapPost("/wingman/ask-devthrottle", async (WingmanVoiceTurnRequest? req, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayWingmanVoice] ask-devthrottle textLen={req?.Text?.Length ?? 0}");
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.Json(new { error = "text is required" }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var t = await translator.AskAboutDevThrottleAsync(req.Text, ct);
                FileLog.Write($"[GatewayWingmanVoice] ask-devthrottle OK: answerLen={t.Spoken.Length}, replySeconds={t.ReplySeconds:F1}");
                return Results.Json(new { spoken = t.Spoken, replySeconds = t.ReplySeconds });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayWingmanVoice] ask-devthrottle FAILED: {ex.Message}");
                return Results.Json(new { error = "wingman failed: " + ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // Menu handling (issue #531): is the agent showing an on-screen menu right now, and what are
        // the options? The phone reads this on entry to render pressable option buttons and speak the
        // choices. { isMenu, question, spoken, selectionMode, submit, options:[{key,send,note,recommended}] }.
        app.MapGet("/sessions/{sid}/wingman/menu", async (string sid, HttpContext ctx, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayWingmanVoice] menu sid={sid}");
            var reqTenant = GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary);
            if (reqTenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" }, statusCode: StatusCodes.Status403Forbidden);
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest);
            var route = await ResolveRouteAsync(reqTenant.Value, sid);
            if (route is null)
                return Results.Json(new { error = "session not found on any director" }, statusCode: StatusCodes.Status404NotFound);
            var menu = await DetectMenuAtAsync(route, translator, sid, ct);
            return Results.Json(MenuJson(menu));
        });

        // NOTE (issue #1777, floor rescope): the raw POST /wingman/menu-press endpoint - which pressed menu
        // keystrokes into a session - was REMOVED. Nothing on this branch ever presses a key into a session.
        // Pressing menu options (fully hands-free voice-answering) is its own later issue, built with per-agent
        // picker profiles and a screen-version lock on every keypress. The GET /wingman/menu detection above
        // stays (read-only, for the announce step); it never leads to a keypress.
    }

    /// <summary>
    /// Classify the live waiting screen for the FLOOR (issue #1777): read the grid over the tunnel and run the
    /// pure, no-brain <see cref="WaitingScreenClassifier"/>. Returns the kind and, when it is not typeable, the
    /// line to speak. This is all voice-turn needs - it types only on <see cref="WaitingScreenKind.PlainText"/>
    /// and never presses - so no menu extraction (brain) happens on the send path.
    /// </summary>
    private static async Task<(WaitingScreenKind Kind, string BlockedSpoken)> ClassifyLiveScreenAtAsync(
        SessionVerbClient route, string sid, CancellationToken ct)
    {
        Contracts.ScreenGridResponse? grid;
        try { grid = await route.GetScreenGridAsync(sid, ct); }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayWingmanVoice] classify sid={sid}: screen-grid read threw ({ex.Message}) - fail closed");
            return (WaitingScreenKind.Blocked, BlockedUnreadableSpoken);
        }
        if (grid is null)
            return (WaitingScreenKind.Blocked, BlockedUnreadableSpoken);

        var kind = WaitingScreenClassifier.Classify(grid.Rows, grid.CursorRow, grid.CursorCol, grid.CursorVisible, grid.IsAlternateScreen, grid.HasGrid);
        // A menu says "look at the terminal"; everything else uncertain says the generic unreadable line.
        var spoken = kind == WaitingScreenKind.Menu ? BlockedMenuSpoken : BlockedUnreadableSpoken;
        return (kind, spoken);
    }

    /// <summary>What a session's live waiting screen turns out to be (issue #1777), so voice-turn can decide
    /// whether the person's spoken words may be typed. Only <see cref="PlainText"/> may be typed.</summary>
    private enum WaitingKind
    {
        /// <summary>A readable on-screen menu with pressable options - map the words to a choice and press.</summary>
        Menu,

        /// <summary>A confident plain-text prompt (a readable screen that is not a menu) - typing is safe.</summary>
        PlainText,

        /// <summary>A menu we could not read, an uncertain screen, or an unreadable one - NEVER type; fail closed.</summary>
        Blocked,
    }

    /// <summary>The classified live waiting screen for the read-only <c>GET /wingman/menu</c> path: for a menu,
    /// the extracted options; for a blocked screen, the plain-English reason and the line to speak. (The send
    /// path does not use this - it classifies with the pure classifier and only ever types.)</summary>
    private sealed class WaitingScreen
    {
        public WaitingKind Kind { get; init; }
        public WingmanMenu Menu { get; init; } = new() { IsMenu = false };
        public string BlockedReason { get; init; } = "";
        public string BlockedSpoken { get; init; } = "";
    }

    private static WaitingScreen Blocked(string reason, string spoken) =>
        new() { Kind = WaitingKind.Blocked, BlockedReason = reason, BlockedSpoken = spoken };

    private const string BlockedMenuSpoken =
        "There's a menu on this session's screen that I couldn't read clearly, so I won't answer it blindly. Open the session to pick an option.";
    private const string BlockedUnreadableSpoken =
        "I can't read this session's screen right now, so I won't type your answer in blindly. Open the session to see what it's asking.";

    /// <summary>
    /// Classify what a session is waiting on by reading the LIVE screen grid ONLY (issue #1777). The live grid
    /// is the sole source of the verdict - the scrollback never gets a vote, so an already-answered menu buried
    /// in history can never be extracted and pressed into whatever is actually on screen (the phantom-menu case
    /// the owner ruled out). The classifier types ONLY on a positive plain-text signal; a blank grid, an
    /// unrecognized alternate-screen app, or a menu whose options we cannot extract all fail closed. A brain
    /// failure during extraction also fails closed - never guess into a picker.
    ///
    /// DEFERRED (next phase, issue #1786): a tall non-full-screen menu whose top scrolled above the visible
    /// window needs the scrollback tail as a detection SUPPLEMENT - but only WITH validation that every
    /// extracted option label appears on the live grid. That validation does not exist yet, so the scrollback
    /// is not fed here at all.
    /// </summary>
    private static async Task<WaitingScreen> DetectWaitingScreenAtAsync(
        SessionVerbClient route, WingmanTranslator translator, string sid, CancellationToken ct)
    {
        // The LIVE screen grid is the ONLY read that decides the verdict (rows + cursor + alternate-screen
        // flag come from one atomic Director snapshot).
        Contracts.ScreenGridResponse? grid;
        try { grid = await route.GetScreenGridAsync(sid, ct); }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayWingmanVoice] waiting-screen sid={sid}: screen-grid read threw ({ex.Message}) - fail closed");
            return Blocked("screen-grid read failed", BlockedUnreadableSpoken);
        }
        if (grid is null)
        {
            FileLog.Write($"[GatewayWingmanVoice] waiting-screen sid={sid}: no screen-grid answer - fail closed");
            return Blocked("no screen-grid answer", BlockedUnreadableSpoken);
        }

        // TWO anchors, fail-closed default (issue #1777, round-4): a MENU is owned by its drawn selection
        // marker (cursor-independent - the Ink picker hides the cursor); TYPING requires the VISIBLE cursor
        // inside a framed composer, on the primary screen, with no menu present. Cursor visibility is the
        // discriminator, so it is passed in alongside the alternate-screen flag.
        var kind = WaitingScreenClassifier.Classify(grid.Rows, grid.CursorRow, grid.CursorCol, grid.CursorVisible, grid.IsAlternateScreen, grid.HasGrid);

        if (kind == WaitingScreenKind.Blocked)
        {
            FileLog.Write($"[GatewayWingmanVoice] waiting-screen sid={sid}: BLOCKED (rows={grid.Rows?.Count ?? 0}, alt={grid.IsAlternateScreen}, hasGrid={grid.HasGrid}) - fail closed");
            return Blocked("unrecognized / unreadable screen", BlockedUnreadableSpoken);
        }

        if (kind == WaitingScreenKind.PlainText)
        {
            FileLog.Write($"[GatewayWingmanVoice] waiting-screen sid={sid}: plain-text prompt (positive composer signal)");
            return new WaitingScreen { Kind = WaitingKind.PlainText };
        }

        // kind == Menu: the LIVE grid carries a drawn menu. Extract it off the LIVE grid text ONLY.
        var liveRows = grid.Rows ?? new List<string>();
        var liveText = string.Join("\n", liveRows);
        WingmanMenu menu;
        try { menu = await translator.DetectMenuAsync(liveText, ct); }
        catch (Exception ex)
        {
            // Looks like a menu but the brain could not read it (unreachable / no key / error). Fail closed.
            FileLog.Write($"[GatewayWingmanVoice] waiting-screen sid={sid}: menu on screen, detection FAILED ({ex.Message}) - fail closed");
            return Blocked("menu on screen, detection failed", BlockedMenuSpoken);
        }

        // A menu is usable only when it has ANSWERABLE options (issue #1777, finding 4): options exist, every
        // one has a real visible label (not a bare "1."/"2."), and every label actually appears on the live
        // grid (the model did not invent options). Anything short of that fails closed.
        if (menu.IsMenu && WingmanMenuLogic.MenuHasAnswerableOptions(menu, liveRows))
        {
            FileLog.Write($"[GatewayWingmanVoice] waiting-screen sid={sid}: MENU with {menu.Options.Count} answerable option(s)");
            return new WaitingScreen { Kind = WaitingKind.Menu, Menu = menu };
        }

        // Looks like a menu but no answerable options could be extracted. UNCERTAIN - the dangerous case the
        // old code fell through on. Fail closed and point the person at the terminal.
        FileLog.Write($"[GatewayWingmanVoice] waiting-screen sid={sid}: looks like a menu but no answerable options - fail closed");
        return Blocked("menu on screen, options not answerable", BlockedMenuSpoken);
    }

    /// <summary>Fetch the session's live screen and, only when it is a drawn menu, ask the warm brain to
    /// extract it. Returns IsMenu=false on any miss. Used ONLY by the read-only <c>GET /wingman/menu</c>
    /// endpoint (the phone's on-entry render / the later announce step) - it never leads to a keypress. The
    /// send path (voice-turn) uses the pure <see cref="ClassifyLiveScreenAtAsync"/> instead and only ever
    /// types on a plain-text composer.</summary>
    private static async Task<WingmanMenu> DetectMenuAtAsync(
        SessionVerbClient route, WingmanTranslator translator, string sid, CancellationToken ct)
    {
        var screen = await DetectWaitingScreenAtAsync(route, translator, sid, ct);
        return screen.Kind == WaitingKind.Menu ? screen.Menu : new WingmanMenu { IsMenu = false };
    }

    /// <summary>
    /// Persist a mobile dictation capture-health record (issue #863) into the shared dictation
    /// session log, but only when the client actually supplied its measurements. The record exists to
    /// carry the audio-loss deficit (recording wall-clock vs decoded audio duration), so the
    /// transcription-context fields a desktop record carries (dictionary counts, cleanup model) are
    /// left at their defaults here. Fire-and-forget; a logging failure never affects the response.
    /// </summary>
    private static void PersistMobileCaptureHealth(string uploadId, UtteranceCompleteRequest req, int wavBytes, string? cleaned)
    {
        if (req.ClientRecordedMs is not { } recordedMs) return; // client did not opt in - nothing to persist

        var decodedSeconds = req.ClientDecodedSeconds ?? 0;
        FileLog.Write($"[GatewayWingmanVoice] capture-health mobile {uploadId}: recordedMs={recordedMs:F0}, "
            + $"decodedSec={decodedSeconds:F2}, wavBytes={wavBytes}, sourceBytes={req.ClientSourceBytes ?? 0}");

        var record = new DictationSessionRecord(
            TimestampUtc: DateTime.UtcNow.ToString("o"),
            SessionId: uploadId,
            Profile: "default",
            VocabularyTermCount: 0,
            MistranscriptionPatternCount: 0,
            RecordingDurationMs: (long)recordedMs,
            StopToTranscribedMs: 0,
            StopToCleanedMs: 0,
            AudioBytesReceived: (int)Math.Min(wavBytes, int.MaxValue),
            RawTranscript: "",
            CleanedTranscript: cleaned ?? "",
            CleanupApplied: false,
            CleanupReason: null,
            CleanupModel: "",
            RemoteIp: null,
            ClientError: null,
            Source: "mobile",
            RecordedWallMs: recordedMs,
            DecodedAudioSeconds: decodedSeconds);

        Task.Run(() => DictationSessionLog.TryAppend(record));
    }

    /// <summary>Shape a <see cref="WingmanMenu"/> for the JSON response (camelCase the phone reads).</summary>
    private static object MenuJson(WingmanMenu m) => new
    {
        isMenu = m.IsMenu,
        question = m.Question,
        spoken = m.Spoken,
        selectionMode = m.SelectionMode,
        submit = m.Submit,
        options = m.Options.Select(o => new { key = o.Key, send = o.Send, note = o.Note, recommended = o.Recommended }).ToList(),
    };

    /// <summary>How long the reply transcript must stop growing before we treat the turn as done.</summary>
    private static readonly TimeSpan ReplyStable = TimeSpan.FromSeconds(2.0);

    /// <summary>
    /// Wait for the agent's reply by polling the TRANSCRIPT (not the fragile live session-state
    /// read across the gateway-to-Director hop): once a new Text widget appears beyond
    /// <paramref name="widgetsBefore"/> and stops growing for <see cref="ReplyStable"/>, that is the
    /// reply. Transient null/hiccup reads from the Director are tolerated (we just keep polling) so a
    /// busy Director mid-turn never makes us give up early - the only way out without a reply is the
    /// full <see cref="TurnTimeout"/>. Returns the reply text, or null if none landed in time.
    /// </summary>
    private static async Task<string?> WaitForReplyAsync(
        SessionVerbClient route, string sid, int widgetsBefore, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TurnTimeout;
        string? reply = null;
        var stableCount = -1;
        var stableSince = DateTime.MinValue;
        var consecutiveNulls = 0;
        while (DateTime.UtcNow < deadline)
        {
            try { await Task.Delay(PollInterval, ct); } catch (OperationCanceledException) { return reply; }

            TurnsResponse? turns;
            try { turns = await route.GetTurnsAsync(sid, ct); }
            catch { turns = null; }

            var widgets = turns?.Widgets;
            if (widgets is null)
            {
                // A transient gateway->Director hiccup (the Director is busy running the turn). Do
                // NOT give up - keep polling. Only a long run of failures (the Director is truly
                // gone) ends it, and even then we fall through to the deadline.
                consecutiveNulls++;
                if (consecutiveNulls > 40) { FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}: director unreachable for replies"); break; }
                continue;
            }
            consecutiveNulls = 0;

            var lastText = widgets.Skip(widgetsBefore).LastOrDefault(w => w.Kind == "Text");
            if (lastText is not null && !string.IsNullOrWhiteSpace(lastText.Content))
            {
                reply = lastText.Content;
                if (widgets.Count != stableCount) { stableCount = widgets.Count; stableSince = DateTime.UtcNow; }
                else if (DateTime.UtcNow - stableSince >= ReplyStable)
                {
                    FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}: reply read, len={reply.Length}");
                    return reply;
                }
            }
        }
        FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}: returning {(reply is null ? "NO reply" : "last reply len=" + reply.Length)} after wait");
        return reply;
    }

    private static async Task<string?> ReadNewReplyAsync(
        SessionVerbClient route, string sid, int widgetsBefore, CancellationToken ct)
    {
        var turns = await route.GetTurnsAsync(sid, ct);
        if (turns?.Widgets is null) return null;
        var last = turns.Widgets.Skip(widgetsBefore).LastOrDefault(w => w.Kind == "Text");
        return last?.Content;
    }
}

/// <summary>Body of the wingman voice-turn and ask-direct routes: the person's message.</summary>
public sealed class WingmanVoiceTurnRequest
{
    public string Text { get; set; } = "";
}

/// <summary>Body of the fleet-wide voice-mode route (issue #1765): <c>Enabled = true</c> turns voice
/// mode on for every session at once, <c>false</c> turns it off. An absent body defaults to enabling.</summary>
public sealed class VoiceModeAllRequest
{
    public bool Enabled { get; set; } = true;
}

/// <summary>Body of the wingman text-to-speech route: the text to speak, and an optional voice + model
/// (used by the settings "play sample" so a voice/model can be previewed before it is saved). When Voice
/// or Model is omitted the saved <see cref="Core.Configuration.TtsVoiceConfig"/> / <see cref="Core.Configuration.TtsModelConfig"/> values are used.</summary>
public sealed class WingmanTtsRequest
{
    public string Text { get; set; } = "";
    public string? Voice { get; set; }
    public string? Model { get; set; }
}

/// <summary>Body of the resumable-utterance complete route: how many chunks to reassemble and the
/// recording's MIME type / file extension (so the hosted transcriber gets a correctly-named file).</summary>
public sealed class UtteranceCompleteRequest
{
    public int TotalChunks { get; set; }
    public string? Mime { get; set; }
    public string? Ext { get; set; }

    // Capture-health (issue #863), optional - present only for the mobile dictation dialog, which
    // measures audio loss as recording wall-clock versus decoded audio duration (a compressed
    // MediaRecorder clip has no fixed bytes/sec). When present the complete handler persists a
    // dictation session record so mobile loss lands in the same log as the desktop and overlay.
    public double? ClientRecordedMs { get; set; }
    public double? ClientDecodedSeconds { get; set; }
    public long? ClientSourceBytes { get; set; }
}
