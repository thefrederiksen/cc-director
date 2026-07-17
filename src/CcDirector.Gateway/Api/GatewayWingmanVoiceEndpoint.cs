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
        HttpClient? ttsHttpClient = null)
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
        Task<SessionVerbClient?> ResolveRouteAsync(string sid) =>
            SessionVerbClient.ResolveAsync(sid, registry, pushedSessions, stale, owners, sendCommand);

        // The session's title, which the wingman speaks before the summary so a listener who cannot
        // see the screen knows which session is talking (WingmanTranslator.FidelityPrompt v5.2). Same
        // push-store read as ResolveRouteAsync above - no dial. Null (unknown session, or no name) is
        // the honest answer and simply means no title is spoken; see GatewayHost.ResolveSessionTitle.
        string? SessionTitle(string sid)
        {
            var name = pushedSessions?.TryLocate(TenantId.Local, sid, stale)?.Session.Name;
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        // The single Gateway owner of speech-to-text (issue #839): both batch transcribe paths below
        // (the resumable /wingman/utterance/complete and the one-shot /wingman/transcribe) go through
        // it, so they resolve the mode + key and pick the hosted endpoint exactly the same way every
        // other batch caller does - no second resolver.
        var transcription = new Transcription.GatewayTranscriptionService(vault);

        // Which voice sessions have a ready, playable spoken summary right now (the phone's list
        // shows a play button on these and can play without entering).
        app.MapGet("/wingman/voice/ready", () => Results.Json(new { sids = voice.ReadySessionIds() }));

        // The precomputed spoken summary for a session (instant on entry - no re-read needed).
        app.MapGet("/sessions/{sid}/wingman/voice", (string sid) =>
        {
            var v = voice.Get(sid);
            return v is null
                ? Results.Json(new { ready = false })
                : Results.Json(new { ready = true, spoken = v.Spoken, reply = v.Reply, generatedAt = v.AtUtc });
        });

        // The precomputed audio for a session - streamed so the list can play it with one tap.
        app.MapGet("/sessions/{sid}/wingman/voice/audio", (string sid) =>
        {
            var audio = voice.GetAudio(sid);
            return audio is { Length: > 0 }
                ? Results.Bytes(audio, voice.GetAudioContentType(sid) ?? "audio/mpeg", enableRangeProcessing: true)
                : Results.Json(new { error = "no voice ready for this session" }, statusCode: StatusCodes.Status404NotFound);
        });

        // Turn voice off for a session (issue #859): unmark it as a voice session so the gateway
        // STOPS spending the per-turn Opus translation + hosted text-to-speech on it. This is the
        // counterpart to the marking that POST /sessions/{sid}/wingman/explain performs on entry; the
        // phone's "Turn voice off" calls it alongside the Director's /voice-mode { enabled:false }.
        // Gateway-side only and read-only - it clears the voice marker + cached clip and sends nothing
        // into the session. Idempotent: stopping a session that was not a voice session is a no-op 200.
        app.MapPost("/sessions/{sid}/wingman/voice/stop", (string sid) =>
        {
            FileLog.Write($"[GatewayWingmanVoice] voice/stop sid={sid}");
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest);
            voice.Unmark(sid);
            return Results.Json(new { stopped = true });
        });
        // Resumable, idempotent piece-by-piece upload store (the same one the native app path uses):
        // chunks land on disk under a stable upload id and survive between retry attempts, so the
        // phone can keep re-sending pieces until the whole recording is through.
        var uploads = new VoiceUploadStore();

        // ===== Resumable transcription upload (issue #531: drive-safe, keeps trying) =====
        // The phone records locally (works offline), then ships the recording in pieces here and
        // keeps retrying until every piece lands - no user buttons. When all pieces are in, the
        // assembled audio is transcribed, the validated dictionary correction is applied (the same
        // engine every other surface uses; raw is returned in local mode or on any cleanup error),
        // and the corrected text is returned.
        //   POST   /wingman/utterance/upload                  -> { upload_id }
        //   PUT    /wingman/utterance/{id}/chunk/{i}           -> { ok }   (idempotent)
        //   POST   /wingman/utterance/{id}/complete {total}    -> { transcript } | 409 { missing }
        app.MapPost("/wingman/utterance/upload", (HttpContext ctx) =>
        {
            var key = ctx.Request.Headers["Idempotency-Key"].ToString();
            var id = uploads.Register(string.IsNullOrWhiteSpace(key) ? null : key);
            return Results.Json(new { upload_id = id });
        });

        app.MapPut("/wingman/utterance/{uploadId}/chunk/{index:int}", async (string uploadId, int index, HttpContext ctx, CancellationToken ct) =>
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

        app.MapPost("/wingman/utterance/{uploadId}/complete", async (string uploadId, UtteranceCompleteRequest? req, CancellationToken ct) =>
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
            try
            {
                // Per-attempt deadline derived from the text length + one retry (TtsSynthesis), so a
                // stalled upstream voice worker fails fast and retries instead of freezing the caller,
                // while a legitimately long read still gets the time it actually needs.
                // The client is the shared static (see SharedTtsHttp) - never a per-call one.
                using var resp = await TtsSynthesis.PostAsync(ttsHttp, url, key, new { model, voice, input, response_format = "mp3" }, input.Length, ct);
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
                FileLog.Write($"[GatewayWingmanVoice] tts ok: provider={mode.ToConfigString()}, chars={input.Length}, bytes={bytes.Length}, model={model}, voice={voice}, type={contentType}");
                return Results.Bytes(bytes, contentType);
            }
            // TtsSynthesis exhausted its attempts: the worker never answered inside the per-attempt cap.
            // That is a GATEWAY TIMEOUT (504), not a bad gateway (502). The two used to be the same 502,
            // which is the collapse that makes an outage unreadable from the outside: "the upstream
            // returned an error" and "the upstream never answered" want different responses from a
            // client and different answers from support, so they must not share a status.
            catch (TimeoutException ex)
            {
                FileLog.Write($"[GatewayWingmanVoice] tts TIMED OUT: {ex.Message}");
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

        app.MapPost("/sessions/{sid}/wingman/voice-turn", async (string sid, WingmanVoiceTurnRequest? req, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}, textLen={req?.Text?.Length ?? 0}");
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest);
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.Json(new { error = "text is required" }, statusCode: StatusCodes.Status400BadRequest);

            var route = await ResolveRouteAsync(sid);
            if (route is null)
                return Results.Json(new { error = "session not found on any director" }, statusCode: StatusCodes.Status404NotFound);

            // Menu handling (issue #531): if the agent is RIGHT NOW showing an on-screen menu, the
            // person's words are a CHOICE, not a new prompt. Detect it, map the words to an option,
            // and PRESS that option (raw keystrokes) - never type the spoken words as a prompt.
            var menu = await DetectMenuAtAsync(route, translator, sid, ct);
            if (menu.IsMenu)
            {
                var idx = WingmanMenuLogic.MatchOption(menu, req.Text);
                if (idx < 0) idx = await translator.MapChoiceAsync(menu, req.Text, ct);
                if (idx >= 0 && idx < menu.Options.Count)
                {
                    var opt = menu.Options[idx];
                    var submit = string.Equals(menu.SelectionMode, "multiple", StringComparison.OrdinalIgnoreCase) ? menu.Submit : "";
                    FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}: menu choice -> option {idx + 1}");
                    return await PressAndSummarizeAsync(route, translator, voice, sid, SessionTitle(sid), opt.Send, submit, $"Selecting option {idx + 1}. ", "voice-menu", ct);
                }
                // Heard them, but no confident option: re-read the menu and send NOTHING (don't burn the turn).
                FileLog.Write($"[GatewayWingmanVoice] voice-turn sid={sid}: menu present, choice unclear");
                return Results.Json(new { reply = "", spoken = "I didn't catch which one. " + menu.Spoken, needsChoice = true, menu = MenuJson(menu) });
            }

            // We are about to start a new turn, so the cached spoken summary + audio are now stale.
            // Clear them DETERMINISTICALLY here (do not rely on observing the Working state, which is
            // racy for fast turns) - the list stops showing it ready and nothing stale plays. The
            // fresh summary is stored below once the agent replies.
            voice.OnSessionWorking(sid);

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
            voice.BeginGenerating(sid);
            try
            {
                // Full context: prior exchanges from the pre-send snapshot + the current question,
                // so the wingman can resolve references like "that file" or "the bug I mentioned".
                var recentContext = string.IsNullOrWhiteSpace(priorContext)
                    ? "You: " + req.Text.Trim()
                    : priorContext + "\n\nYou: " + req.Text.Trim();
                var t = await translator.TranslateAsync(recentContext, reply, SessionTitle(sid), CancellationToken.None);
                await voice.StoreSpokenAsync(sid, t.Spoken, reply, CancellationToken.None);   // make it a voice session + cache audio
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
            finally { voice.EndGenerating(sid); }
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
        app.MapPost("/sessions/{sid}/wingman/explain", async (string sid, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayWingmanVoice] explain sid={sid}");
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest);

            var route = await ResolveRouteAsync(sid);
            if (route is null)
                return Results.Json(new { error = "session not found on any director" }, statusCode: StatusCodes.Status404NotFound);

            var turns = await route.GetTurnsAsync(sid, ct);
            var widgets = turns?.Widgets ?? new List<TurnWidgetDto>();
            var lastReply = widgets.LastOrDefault(w => w.Kind == "Text")?.Content;
            // Recent conversation so the wingman can give context to a short/terse reply.
            var recentContext = WingmanTranslator.BuildRecentContext(widgets);

            voice.Mark(sid);   // opening voice on a session makes it a voice session (kept fresh on turn-end)
            if (string.IsNullOrWhiteSpace(lastReply))
            {
                // A fresh or text-only session with nothing to read yet: a truthful canned line,
                // no brain call.
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
            voice.BeginGenerating(sid);
            try
            {
                var t = await translator.TranslateAsync(recentContext, lastReply, SessionTitle(sid), CancellationToken.None);
                await voice.StoreSpokenAsync(sid, t.Spoken, lastReply, CancellationToken.None);   // cache spoken + audio, ready to play
                FileLog.Write($"[GatewayWingmanVoice] explain sid={sid}: replyLen={lastReply.Length}, spokenLen={t.Spoken.Length}");
                // Training capture (no-op unless the setting is on); fire-and-forget so it adds no latency.
                _ = voice.CaptureTrainingAsync(route, sid, "explain", lastReply, recentContext, t.Spoken, t.ReplySeconds, CancellationToken.None);
                return Results.Json(new { reply = lastReply, spoken = t.Spoken, replySeconds = t.ReplySeconds });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[GatewayWingmanVoice] explain sid={sid} FAILED: {ex.Message}");
                return Results.Json(new { error = "wingman could not summarize: " + ex.Message },
                    statusCode: StatusCodes.Status502BadGateway);
            }
            finally { voice.EndGenerating(sid); }
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
        app.MapGet("/sessions/{sid}/wingman/menu", async (string sid, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayWingmanVoice] menu sid={sid}");
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest);
            var route = await ResolveRouteAsync(sid);
            if (route is null)
                return Results.Json(new { error = "session not found on any director" }, statusCode: StatusCodes.Status404NotFound);
            var menu = await DetectMenuAtAsync(route, translator, sid, ct);
            return Results.Json(MenuJson(menu));
        });

        // Press a specific menu option (the phone's option-button tap): send the exact keystrokes,
        // then wait for the agent's result and translate it back. { send, submit? } -> { reply, spoken }.
        app.MapPost("/sessions/{sid}/wingman/menu-press", async (string sid, WingmanMenuPressRequest? req, CancellationToken ct) =>
        {
            FileLog.Write($"[GatewayWingmanVoice] menu-press sid={sid}");
            if (!Guid.TryParse(sid, out _))
                return Results.Json(new { error = "invalid session id format" }, statusCode: StatusCodes.Status400BadRequest);
            if (req is null || string.IsNullOrEmpty(req.Send))
                return Results.Json(new { error = "send is required" }, statusCode: StatusCodes.Status400BadRequest);
            var route = await ResolveRouteAsync(sid);
            if (route is null)
                return Results.Json(new { error = "session not found on any director" }, statusCode: StatusCodes.Status404NotFound);
            return await PressAndSummarizeAsync(route, translator, voice, sid, SessionTitle(sid), req.Send, req.Submit, null, "menu-press", ct);
        });
    }

    /// <summary>Fetch the session terminal and, only when it cheaply looks like a menu, ask the warm
    /// brain to extract it. Returns IsMenu=false on any miss - the caller treats input as a prompt.</summary>
    private static async Task<WingmanMenu> DetectMenuAtAsync(
        SessionVerbClient route, WingmanTranslator translator, string sid, CancellationToken ct)
    {
        Contracts.BufferResponse? buf;
        try { buf = await route.GetBufferAsync(sid, lines: null, raw: false, since: null, ct); }
        catch { buf = null; }
        var terminal = buf?.Text ?? "";
        if (!WingmanMenuLogic.LooksLikeMenu(terminal)) return new WingmanMenu { IsMenu = false };
        return await translator.DetectMenuAsync(terminal, ct);
    }

    /// <summary>Press an option's keystrokes (then the multi-select submit, if any), wait for the
    /// agent's resulting turn, translate it, cache it, and return the spoken summary. Shared by the
    /// option-button tap (menu-press) and the spoken-choice path (voice-turn).</summary>
    private static async Task<IResult> PressAndSummarizeAsync(
        SessionVerbClient route, WingmanTranslator translator, WingmanVoiceService voice,
        string sid, string? sessionTitle, string send, string? submit, string? confirmPrefix, string source, CancellationToken ct)
    {
        voice.OnSessionWorking(sid);   // a new turn is coming; drop the stale cached summary
        var before = await CountTextWidgetsAsync(route, sid, ct);

        var (ok, _, err) = await route.PostPromptAsync(sid, new PromptRequest { Text = send, AppendEnter = false }, ct);
        if (!ok)
            return Results.Json(new { error = "press failed: " + err }, statusCode: StatusCodes.Status502BadGateway);
        if (!string.IsNullOrEmpty(submit))
        {
            try { await Task.Delay(300, ct); } catch (OperationCanceledException) { }
            await route.PostPromptAsync(sid, new PromptRequest { Text = submit, AppendEnter = false }, CancellationToken.None);
        }
        FileLog.Write($"[GatewayWingmanVoice] {source} sid={sid}: pressed send=\"{Escape(send)}\" submit=\"{Escape(submit)}\"");

        var prefix = confirmPrefix ?? "";
        var reply = await WaitForReplyAsync(route, sid, before, ct);
        if (string.IsNullOrWhiteSpace(reply))
            return Results.Json(new { reply = "", spoken = prefix + "Done. The agent is working - I'll have the result shortly.", pressed = true });

        voice.BeginGenerating(sid);
        try
        {
            var t = await translator.TranslateAsync("(you picked a menu option)", reply, sessionTitle, CancellationToken.None);
            var spoken = prefix + t.Spoken;
            await voice.StoreSpokenAsync(sid, spoken, reply, CancellationToken.None);
            _ = voice.CaptureTrainingAsync(route, sid, source, reply, "(menu pick)", spoken, t.ReplySeconds, CancellationToken.None);
            FileLog.Write($"[GatewayWingmanVoice] {source} sid={sid}: replyLen={reply.Length}, spokenLen={spoken.Length}");
            return Results.Json(new { reply, spoken, replySeconds = t.ReplySeconds, pressed = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayWingmanVoice] {source} sid={sid} translate FAILED: {ex.Message}");
            return Results.Json(new { error = "wingman translation failed: " + ex.Message }, statusCode: StatusCodes.Status502BadGateway);
        }
        finally { voice.EndGenerating(sid); }
    }

    private static string Escape(string? s) => (s ?? "").Replace("\r", "\\r").Replace("\n", "\\n");

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

    private static async Task<int> CountTextWidgetsAsync(
        SessionVerbClient route, string sid, CancellationToken ct)
    {
        var turns = await route.GetTurnsAsync(sid, ct);
        return turns?.Widgets?.Count ?? 0;
    }
}

/// <summary>Body of the wingman voice-turn and ask-direct routes: the person's message.</summary>
public sealed class WingmanVoiceTurnRequest
{
    public string Text { get; set; } = "";
}

/// <summary>Body of the menu-press route: the exact keystrokes that pick an option, and (for a
/// multi-select menu) the completing submit keystroke.</summary>
public sealed class WingmanMenuPressRequest
{
    public string Send { get; set; } = "";
    public string? Submit { get; set; }
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
