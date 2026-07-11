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
/// Auth: the route is not on the public allow-list and is not under /m/, so the host-wide auth gate already
/// requires the caller's per-device key (or the shared token), per the Gateway auth rule. The caller's own
/// credential also keys the server-side conversation context, so multi-turn works per device without any
/// history crossing the wire. The credential is used only as an opaque key and is never logged (DT-05).
/// </summary>
internal static class CarModeEndpoint
{
    public static void Map(IEndpointRouteBuilder app, CarModeBrain brain)
    {
        app.MapPost("/carmode/turn", async (HttpContext ctx, CarModeTurnRequest? req, CancellationToken ct) =>
        {
            FileLog.Write($"[CarModeEndpoint] turn: len={req?.Text?.Length ?? 0}");
            if (req is null || string.IsNullOrWhiteSpace(req.Text))
                return Results.Json(new { error = "text is required" }, statusCode: StatusCodes.Status400BadRequest);

            var deviceKey = ExtractCallerCredential(ctx);
            try
            {
                var result = await brain.RunTurnAsync(deviceKey, req.Text.Trim(), ct);
                return Results.Json(new
                {
                    spoken = result.Spoken,
                    actions = result.Actions.Select(a => new { tool = a.Tool, summary = a.Summary }),
                    pendingConfirmation = result.PendingConfirmation,
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
    }

    /// <summary>The caller's own credential (Bearer header, else the cc-gateway-token cookie), used only as
    ///  the opaque per-device conversation key. Empty when the auth gate is off (debug), which the store
    ///  maps to one shared anonymous context. Never logged.</summary>
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
