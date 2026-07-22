using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.CarMode;
using CcDirector.Gateway.HostedAi;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The Car Mode brain front door (Car Mode mission, Phase 2): POST /carmode/turn. The phone hands the
/// owner's transcribed command here; the <see cref="CarModeBrain"/> runs its tool-calling loop against the
/// fleet and returns the final spoken reply, any actions taken, and whether it is holding a destructive
/// action for confirmation. The browser stays thin (decision 2): it never sees the model key or the fleet
/// logic.
///
/// Performance round: the turn response also carries a server-minted turnId and a per-stage server timing
/// block so the browser can merge its own client stamps and post ONE compact timing record to the
/// local diagnostics store below. The owner can inspect and clear it:
///   POST   /carmode/diagnostics       - the browser posts one merged record per turn.
///   GET    /carmode/diagnostics       - a self-contained HTML dashboard.
///   GET    /carmode/diagnostics/data  - this device's records as JSON.
///   DELETE /carmode/diagnostics       - clear this device's records.
///
/// Diagnostics are stored and served PER DEVICE. The write files the record under the device hash the server
/// derives from the caller's own credential, and the data read returns only that same device's records, so
/// one caller's turn timings are never disclosed to another.
///
/// Auth: the routes are not on the public allow-list and are not under /m/, so the host-wide auth gate
/// already requires the caller's per-device key (or the shared token), per the Gateway auth rule. THE
/// CREDENTIAL THE GATE ACCEPTED - not this file's own reading of the request - keys the server-side
/// conversation context and the diagnostics partition, so multi-turn works per device without any history
/// crossing the wire and a caller cannot be authenticated as one identity while being partitioned as
/// another. The credential is used only as an opaque key and a one-way device hash, and is never
/// logged (DT-05).
/// </summary>
internal static class CarModeEndpoint
{
    public static void Map(IEndpointRouteBuilder app, CarModeBrain brain, CarModeTurnCache turnCache, CarModeDiagnosticsStore diagnostics, CarModeWarmup warmup)
    {
        // Keep-warm (Car Mode performance round): the browser calls this the instant the owner taps Start,
        // and every few minutes WHILE Car Mode is open, so the hosted model + text-to-speech are hot before
        // the first utterance and stay hot for the drive. Gated on the keep-warm config (default ON for Car
        // Mode); when off it is a cheap no-op. The warmup runs in the BACKGROUND on CancellationToken.None
        // so the Start tap is never blocked and navigating away does not cancel the warm. Best-effort - a
        // warmup failure never surfaces to the owner.
        app.MapPost("/carmode/warmup", () =>
        {
            if (!CarModeKeepWarmConfig.Enabled())
                return Results.Json(new { warmed = false, reason = "keep-warm disabled" });
            _ = Task.Run(() => warmup.WarmAsync(CancellationToken.None));
            return Results.Json(new { warmed = true });
        });

        // Help Mode (issue #1441): the DIRECT, model-free front door for the "Help" button on /m/car. It
        // returns the ONE curated help content from CarModeHelp - the spoken script the button reads aloud
        // through /wingman/tts, and the small structured cheat-sheet the page shows on screen - so the button
        // is instant, reliable, and costs no credits (no model round trip). The spoken "help" / "what can you
        // do" path goes through the brain's get_help tool instead, which returns the SAME script verbatim, so
        // both triggers say the identical thing. Behind the host-wide device-key gate like the other routes.
        app.MapGet("/carmode/help", () => Results.Json(new
        {
            spoken = CarModeHelp.Script,
            cheatSheet = new
            {
                modes = CarModeHelp.CheatSheet.Modes.Select(m => new { title = m.Title, hint = m.Hint, examples = m.Examples }),
                endTurn = CarModeHelp.CheatSheet.EndTurn,
                help = CarModeHelp.CheatSheet.Help,
            },
        }));

        app.MapPost("/carmode/turn", async (HttpContext ctx, CarModeTurnRequest? req, CancellationToken ct) =>
        {
            FileLog.Write($"[CarModeEndpoint] turn: len={req?.Text?.Length ?? 0}");
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.Json(new { error = "text is required" }, statusCode: StatusCodes.Status400BadRequest);

            // The per-device conversation key is the SAME authenticated credential the diagnostics partition
            // uses. It was read from the raw headers here too, which had the same flaw: a caller could be
            // authenticated on one credential and given another device's conversation context.
            var deviceKey = AuthenticatedCredential(ctx);
            var text = req.Text.Trim();
            // Offline resilience Phase 4b (issue #1427): the client sends its durable command-audio record id
            // as the Idempotency-Key so an already-sent turn whose result was lost in a dead zone auto-retries
            // and ACTS at most once. When present it also becomes the turnId, so a retry's diagnostics tie back
            // to the original rather than double-counting. When absent (a legacy caller), the old behavior
            // holds: a fresh server turnId and a direct brain run on the request token.
            var idemKey = ExtractIdempotencyKey(ctx);
            var turnId = string.IsNullOrEmpty(idemKey) ? Guid.NewGuid().ToString("N") : idemKey;
            try
            {
                CarModeTurnResponse result;
                if (string.IsNullOrEmpty(idemKey))
                {
                    result = await brain.RunTurnAsync(deviceKey, text, ct);
                }
                else
                {
                    // Run the brain on CancellationToken.None (NOT the request token) inside the single-flight
                    // cache, so a client that drops mid-turn does NOT abort the work - it completes and caches,
                    // and the client's retry gets the cached result instead of re-acting. Await with the request
                    // token so a disconnected client still returns promptly (the underlying work continues).
                    var work = turnCache.GetOrRunAsync(deviceKey, idemKey, () => brain.RunTurnAsync(deviceKey, text, CancellationToken.None));
                    result = await work.WaitAsync(ct);
                }
                return Results.Json(new
                {
                    turnId,
                    spoken = result.Spoken,
                    actions = result.Actions.Select(a => new { tool = a.Tool, summary = a.Summary }),
                    pendingConfirmation = result.PendingConfirmation,
                    // The per-stage server timing, inline, so the browser posts it back merged with its
                    // own client stamps as one diagnostics record (performance round).
                    timing = result.Timing is null ? null : new
                    {
                        totalMs = result.Timing.TotalMs,
                        modelCallCount = result.Timing.ModelCallCount,
                        modelMsTotal = result.Timing.ModelMsTotal,
                        modelMs = result.Timing.ModelMs,
                        fleetReadCount = result.Timing.FleetReadCount,
                        fleetReadMsTotal = result.Timing.FleetReadMsTotal,
                        rounds = result.Timing.Rounds,
                    },
                });
            }
            catch (CarModeUnavailableException ex)
            {
                // A money refusal (out of credits / cap / no key): the ONE shared 402 state, so the phone
                // shows the consistent add-credit / add-key notice instead of a generic error.
                FileLog.Write($"[CarModeEndpoint] turn unavailable: {ex.State}");
                return HostedAiHttp.PaymentRequiredResult(ex.State);
            }
            catch (OperationCanceledException)
            {
                // The client navigated away / aborted the turn; nothing to report (499 = client closed).
                return Results.Json(new { error = "cancelled" }, statusCode: 499);
            }
            catch (Exception ex)
            {
                // A loud, specific failure the phone speaks (decision 8), never a silent stall.
                FileLog.Write($"[CarModeEndpoint] turn FAILED: {ex.Message}");
                return Results.Json(new { error = "Car Mode could not complete that: " + ex.Message },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });

        // The browser posts ONE merged timing record per turn here. It fills the client stamps and echoes
        // the server timing it received in the turn response; the SERVER fills the received-at time, the
        // device hash (from its own credential extraction, never trusting the client), and the Gateway
        // build, so those cannot be spoofed. A malformed body is a clear 400, never a silent drop.
        //
        // The device hash the server derives here is also the STORAGE PARTITION: the write files the record
        // under this device and the reads below return only this device's records. Nothing from the posted
        // body is ever used as the discriminator - a caller-supplied value (the turn id in particular) is
        // not trusted, and the partition is recorded at write time so records can never accumulate
        // unpartitioned behind a read filter.
        app.MapPost("/carmode/diagnostics", (HttpContext ctx, CarModeDiagnosticsPost? req) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.TurnId))
                return Results.Json(new { error = "turnId is required" }, statusCode: StatusCodes.Status400BadRequest);

            var deviceHash = DevicePartition(ctx);
            var record = new CarModeDiagnosticsRecord
            {
                TurnId = req.TurnId,
                ReceivedAtUtc = DateTime.UtcNow.ToString("o"),
                GatewayVersion = AppVersion.Full,
                PauseToTranscribeMs = req.PauseToTranscribeMs,
                TranscodeMs = req.TranscodeMs,
                BrainMs = req.BrainMs,
                TtsMs = req.TtsMs,
                FirstAudioMs = req.FirstAudioMs,
                TotalTurnMs = req.TotalTurnMs,
                TranscribeAttempts = req.TranscribeAttempts,
                Chunks = req.Chunks,
                PlayMs = req.PlayMs,
                ClipDurationMs = req.ClipDurationMs,
                PlayedToMs = req.PlayedToMs,
                Completed = req.Completed,
                PlayRejected = req.PlayRejected,
                MicReacquiredDuringPlayback = req.MicReacquiredDuringPlayback,
                SpeakingPollCount = req.SpeakingPollCount,
                ViewportInnerHeight = req.ViewportInnerHeight,
                VisualViewportHeight = req.VisualViewportHeight,
                DocumentClientHeight = req.DocumentClientHeight,
                FooterBottom = req.FooterBottom,
                FooterVisible = req.FooterVisible,
                ServerTotalMs = req.ServerTotalMs,
                ModelCallCount = req.ModelCallCount,
                ModelMsTotal = req.ModelMsTotal,
                ModelMs = req.ModelMs ?? Array.Empty<double>(),
                FleetReadCount = req.FleetReadCount,
                FleetReadMsTotal = req.FleetReadMsTotal,
                Rounds = req.Rounds,
                CommandChars = req.CommandChars,
                ReplyChars = req.ReplyChars,
                ActionsCount = req.ActionsCount,
                PendingConfirmation = req.PendingConfirmation,
            };
            var held = diagnostics.Add(deviceHash, record);
            return Results.Json(new { recorded = true, held });
        });

        // The dashboard's data read. It is scoped to the CALLER'S OWN device partition, derived from the
        // caller's credential exactly as the write derives it, so one device's turns are never handed to
        // another. The held count is this device's count too - a process-wide total would disclose other
        // devices' activity as an aggregate.
        app.MapGet("/carmode/diagnostics/data", (HttpContext ctx) =>
        {
            var deviceHash = DevicePartition(ctx);
            var limit = 200;
            if (int.TryParse(ctx.Request.Query["limit"], out var q) && q > 0) limit = Math.Min(q, 2000);
            return Results.Json(new
            {
                generatedAtUtc = DateTime.UtcNow,
                held = diagnostics.Count(deviceHash),
                records = diagnostics.Recent(deviceHash, limit),
            });
        });

        // The dashboard page itself is a static, record-free document; its data read is partitioned above.
        app.MapGet("/carmode/diagnostics", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.ContentType = "text/html; charset=utf-8";
            return ctx.Response.WriteAsync(CarModeDiagnosticsPage.Html);
        });

        app.MapDelete("/carmode/diagnostics", (HttpContext ctx) =>
            Results.Json(new { removed = diagnostics.Clear(DevicePartition(ctx)) }));
    }

    /// <summary>
    /// THE ONE caller identity in this file: THE EXACT CREDENTIAL THE AUTHENTICATION GATE ACCEPTED, which
    /// <see cref="AuthMiddleware.HasValidToken"/> stashes on the request. It is the storage partition for
    /// diagnostics and the per-device conversation key for a turn.
    ///
    /// This route deliberately does NOT read the Authorization header or the cookies itself. It used to, and
    /// that was the defect: the gate accepts a request if ANY presented credential is valid - it tries the
    /// Bearer value and then EVERY raw cc-gateway-token cookie - while a second reader here always preferred
    /// the Bearer. A caller presenting an attacker-chosen Bearer alongside their valid device cookie would
    /// therefore be authenticated on the cookie and partitioned on the Bearer, letting them read and write a
    /// partition that was not theirs. Duplicate cookies opened the same gap. Two independent readings of one
    /// request are two authentication decisions with different rules, and the disagreement between them IS
    /// the vulnerability. The identity is resolved once, by the gate whose job that is, and passed forward.
    ///
    /// Absent means no credential was authenticated at all (the host-wide gate is off in local debug). That
    /// is not an identity, so it maps to ONE shared anonymous bucket - exactly what a credential-free request
    /// got before - and never to a second reading of the headers. Never logged.
    /// </summary>
    private static string AuthenticatedCredential(HttpContext ctx)
        => ctx.Items.TryGetValue(AuthMiddleware.AuthenticatedCredentialItemKey, out var credential)
            ? credential as string ?? ""
            : "";

    /// <summary>The diagnostics storage partition: a one-way hash of the credential that authenticated this
    ///  request. Both the write and the data read derive it here, from this one helper, so the write can
    ///  never file a record under a partition the read would not ask for, and neither can be steered by
    ///  anything the caller supplies - not the posted body, not a query parameter, and not an unvalidated
    ///  credential presented alongside the one that actually authenticated.</summary>
    private static string DevicePartition(HttpContext ctx) => CarModeDeviceHash.Of(AuthenticatedCredential(ctx));

    /// <summary>The client's idempotency key for this turn (the durable command-audio record id), from the
    ///  standard <c>Idempotency-Key</c> header. Empty when absent (a legacy caller), which the handler maps
    ///  to the non-deduped legacy path. Trimmed and length-capped so a malformed header cannot bloat the
    ///  cache key.</summary>
    private static string ExtractIdempotencyKey(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue("Idempotency-Key", out var header)) return "";
        var raw = header.ToString().Trim();
        return raw.Length > 200 ? raw[..200] : raw;
    }
}

/// <summary>Body of POST /carmode/diagnostics: the browser's client stamps plus the server timing it echoes
/// back from the turn response, and small non-text turn facts. The server overrides the identity/build
/// fields from its own side, so those are not part of this body.</summary>
public sealed class CarModeDiagnosticsPost
{
    public string TurnId { get; set; } = "";
    public double PauseToTranscribeMs { get; set; }
    public double TranscodeMs { get; set; }
    public double BrainMs { get; set; }
    public double TtsMs { get; set; }
    public double FirstAudioMs { get; set; }
    public double TotalTurnMs { get; set; }
    public int TranscribeAttempts { get; set; }
    public int Chunks { get; set; }
    public double PlayMs { get; set; }
    public double ClipDurationMs { get; set; }
    public double PlayedToMs { get; set; }
    public bool Completed { get; set; }
    public bool PlayRejected { get; set; }
    public bool MicReacquiredDuringPlayback { get; set; }
    public int SpeakingPollCount { get; set; }
    public double ViewportInnerHeight { get; set; }
    public double VisualViewportHeight { get; set; }
    public double DocumentClientHeight { get; set; }
    public double FooterBottom { get; set; }
    public bool FooterVisible { get; set; }
    public double ServerTotalMs { get; set; }
    public int ModelCallCount { get; set; }
    public double ModelMsTotal { get; set; }
    public double[]? ModelMs { get; set; }
    public int FleetReadCount { get; set; }
    public double FleetReadMsTotal { get; set; }
    public int Rounds { get; set; }
    public int CommandChars { get; set; }
    public int ReplyChars { get; set; }
    public int ActionsCount { get; set; }
    public bool PendingConfirmation { get; set; }
}
