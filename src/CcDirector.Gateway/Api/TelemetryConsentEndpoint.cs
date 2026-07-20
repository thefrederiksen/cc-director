using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Gateway Centralization Phase 3 (issue #649): the fleet-wide richer-usage-telemetry consent
/// (opt-out) endpoints. The authoritative consent setting lives on the Gateway - one setting governs
/// the whole fleet - and a Director reads it to decide whether its richer usage telemetry flows. The
/// always-on login/director-startup auth-floor events (issues #628/#631) are NEVER gated by this.
///
///   GET /gateway/telemetry-consent              -> { enabled }  (default ON when never set)
///   PUT /gateway/telemetry-consent body { enabled: bool } -> { enabled }
///
/// State is the top-level config.json key <c>telemetry_consent</c> (<see cref="TelemetryConsentConfig"/>),
/// the same store the other Gateway settings use. Read at decision time, so a toggle takes effect
/// immediately - no Gateway restart. These endpoints inherit the host-wide Gateway token middleware
/// exactly like every other Gateway endpoint. The response carries no token and no user data.
///
/// DENIED IN WHOLE ON HOSTED (issue #1863). Both routes are refused on a hosted Gateway through ONE
/// group filter. The consent is a single PROCESS-GLOBAL config.json key governing the WHOLE fleet, with
/// no tenant dimension - so on shared hosted infrastructure one tenant toggling it would be answering a
/// privacy question on behalf of every other tenant. There is no correct per-tenant answer to serve
/// here. Self-host is single-tenant and this is legitimate owner function there, so on self-host nothing
/// changes.
/// </summary>
internal static class TelemetryConsentEndpoint
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The hosted refusal for both telemetry-consent routes (issue #1863), or null on self-host. Gated on
    /// <see cref="GatewayHostedMode.IsHosted"/> DIRECTLY, never on an optional or nullable argument, which
    /// fails OPEN when a caller forgets it. 404 rather than 403 for the same reason as its siblings: on
    /// hosted a fleet-wide owner consent does not exist as a concept.
    /// </summary>
    private static IResult? DenyOnHosted()
    {
        if (!GatewayHostedMode.IsHosted) return null;

        FileLog.Write("[TelemetryConsentEndpoint] DENIED on hosted: the consent is one fleet-wide setting with no tenant dimension");
        return Results.Json(
            new { error = "the telemetry consent setting is not available on the hosted gateway" },
            statusCode: StatusCodes.Status404NotFound);
    }

    /// <summary>
    /// Maps the GET/PUT telemetry-consent routes as a GROUP and returns it, so a test can map a brand-new
    /// route onto that group and find it already refused on hosted with no deny of its own.
    /// </summary>
    public static RouteGroupBuilder Map(IEndpointRouteBuilder outer)
    {
        FileLog.Write($"[TelemetryConsentEndpoint] mapping telemetry consent; hosted={GatewayHostedMode.IsHosted} - on hosted EVERY route in this group is refused (issue #1863)");

        var app = outer.MapGroup("");
        app.AddEndpointFilter(async (ctx, next) =>
        {
            if (DenyOnHosted() is { } denied) return denied;
            return await next(ctx);
        });

        app.MapGet("/gateway/telemetry-consent", () =>
        {
            var enabled = TelemetryConsentConfig.Get();
            FileLog.Write($"[TelemetryConsentEndpoint] GET /gateway/telemetry-consent: enabled={enabled}");
            return Results.Json(new { enabled });
        });

        app.MapPut("/gateway/telemetry-consent", async (HttpContext ctx) =>
        {
            try
            {
                var body = await JsonSerializer.DeserializeAsync<TelemetryConsentBody>(
                    ctx.Request.Body, JsonOpts, ctx.RequestAborted);
                if (body is null)
                    return Results.BadRequest(new { error = "body { \"enabled\": true|false } is required" });

                TelemetryConsentConfig.Set(body.Enabled);
                FileLog.Write($"[TelemetryConsentEndpoint] PUT /gateway/telemetry-consent: enabled={body.Enabled}");
                return Results.Json(new { enabled = body.Enabled });
            }
            catch (JsonException ex)
            {
                FileLog.Write($"[TelemetryConsentEndpoint] PUT /gateway/telemetry-consent bad JSON: {ex.Message}");
                return Results.BadRequest(new { error = "invalid JSON" });
            }
        });

        return app;
    }

    private sealed record TelemetryConsentBody(bool Enabled);
}
