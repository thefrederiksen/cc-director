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
/// telemetry store below. Two more routes serve that store:
///   POST /carmode/telemetry       - the browser posts one merged record per turn.
///   GET  /carmode/telemetry       - a self-contained HTML dashboard of recent turns and aggregates.
///   GET  /carmode/telemetry/data  - the raw records as JSON, which the dashboard fetches.
///
/// Auth: the routes are not on the public allow-list and are not under /m/, so the host-wide auth gate
/// already requires the caller's per-device key (or the shared token), per the Gateway auth rule. The
/// caller's own credential also keys the server-side conversation context, so multi-turn works per device
/// without any history crossing the wire. The credential is used only as an opaque key and a one-way
/// device hash, and is never logged (DT-05).
/// </summary>
internal static class CarModeEndpoint
{
    public static void Map(IEndpointRouteBuilder app, CarModeBrain brain, CarModeTelemetryStore telemetry, CarModeWarmup warmup)
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

        app.MapPost("/carmode/turn", async (HttpContext ctx, CarModeTurnRequest? req, CancellationToken ct) =>
        {
            FileLog.Write($"[CarModeEndpoint] turn: len={req?.Text?.Length ?? 0}");
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.Json(new { error = "text is required" }, statusCode: StatusCodes.Status400BadRequest);

            var deviceKey = ExtractCallerCredential(ctx);
            // A server-minted turn id ties the browser's posted timing record back to this turn's log line.
            var turnId = Guid.NewGuid().ToString("N");
            try
            {
                var result = await brain.RunTurnAsync(deviceKey, req.Text.Trim(), ct);
                return Results.Json(new
                {
                    turnId,
                    spoken = result.Spoken,
                    actions = result.Actions.Select(a => new { tool = a.Tool, summary = a.Summary }),
                    pendingConfirmation = result.PendingConfirmation,
                    // The per-stage server timing, inline, so the browser posts it back merged with its
                    // own client stamps as one telemetry record (performance round).
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
        app.MapPost("/carmode/telemetry", (HttpContext ctx, CarModeTelemetryPost? req) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.TurnId))
                return Results.Json(new { error = "turnId is required" }, statusCode: StatusCodes.Status400BadRequest);

            var record = new CarModeTelemetryRecord
            {
                TurnId = req.TurnId,
                ReceivedAtUtc = DateTime.UtcNow.ToString("o"),
                DeviceHash = CarModeDeviceHash.Of(ExtractCallerCredential(ctx)),
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
                MicReacquiredDuringPlayback = req.MicReacquiredDuringPlayback,
                SpeakingPollCount = req.SpeakingPollCount,
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
            var held = telemetry.Add(record);
            return Results.Json(new { recorded = true, held });
        });

        app.MapGet("/carmode/telemetry/data", (HttpContext ctx) =>
        {
            var limit = 200;
            if (int.TryParse(ctx.Request.Query["limit"], out var q) && q > 0) limit = Math.Min(q, 2000);
            return Results.Json(new
            {
                generatedAtUtc = DateTime.UtcNow,
                held = telemetry.Count(),
                records = telemetry.Recent(limit),
            });
        });

        app.MapGet("/carmode/telemetry", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.ContentType = "text/html; charset=utf-8";
            return ctx.Response.WriteAsync(CarModeTelemetryPage.Html);
        });
    }

    /// <summary>The caller's own credential (Bearer header, else the cc-gateway-token cookie), used only as
    ///  the opaque per-device conversation key and the one-way device hash. Empty when the auth gate is off
    ///  (debug), which the store maps to one shared anonymous context. Never logged.</summary>
    private static string ExtractCallerCredential(HttpContext ctx)
    {
        if (ctx.Request.Headers.TryGetValue("Authorization", out var header))
        {
            var raw = header.ToString();
            const string prefix = "Bearer ";
            if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return raw[prefix.Length..].Trim();
        }
        if (ctx.Request.Cookies.TryGetValue(AuthMiddleware.CookieName, out var cookie) && !string.IsNullOrWhiteSpace(cookie))
            return cookie;
        return "";
    }
}

/// <summary>Body of POST /carmode/telemetry: the browser's client stamps plus the server timing it echoes
/// back from the turn response, and small non-text turn facts. The server overrides the identity/build
/// fields from its own side, so those are not part of this body.</summary>
public sealed class CarModeTelemetryPost
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
    public bool MicReacquiredDuringPlayback { get; set; }
    public int SpeakingPollCount { get; set; }
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
