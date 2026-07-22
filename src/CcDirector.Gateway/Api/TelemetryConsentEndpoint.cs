using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Tenancy;
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
/// DENIED IN WHOLE ON HOSTED (issue #1863). Both routes are refused on a hosted Gateway through the shared
/// refusal primitive (<see cref="HostedRouteDeny.Group"/>), the same boundary the rest of the owner-settings
/// group and the key-vault group adopt. The consent is a single PROCESS-GLOBAL config.json key governing the
/// WHOLE fleet, with no tenant dimension - so on shared hosted infrastructure one tenant toggling it would be
/// answering a privacy question on behalf of every other tenant. There is no correct per-tenant answer to
/// serve here. Self-host is single-tenant and this is legitimate owner function there, so on self-host
/// nothing changes.
///
/// PER-ROUTE MODE, not an exclusive prefix: this family sits under <c>/gateway</c>, which carries LIVE routes
/// from other families, so an exclusive claim would take them off the air. The primitive maps a verb-less
/// refusal on each route's own pattern on hosted - the handler is never mapped - so every request shape,
/// including a verb the route never served, meets the refusal rather than a 405.
/// </summary>
internal static class TelemetryConsentEndpoint
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The single error string the hosted refusal serves, held here so a test asserts the exact
    /// string served rather than a copy that could drift.</summary>
    internal const string RefusalMessage = "the telemetry consent setting is not available on the hosted gateway";

    /// <summary>
    /// The hosted refusal payload for both telemetry-consent routes (issue #1863). Validated on construction,
    /// so a blank field fails the Gateway at startup. The primitive reads <see cref="GatewayHostedMode.IsHosted"/>
    /// DIRECTLY, never an optional argument that fails OPEN when a caller forgets it. 404 rather than 403 for
    /// the same reason as its siblings: on hosted a fleet-wide owner consent does not exist as a concept.
    /// </summary>
    private static HostedDenial Denial() => new(
        family: "telemetry-consent",
        message: RefusalMessage,
        reason: "the consent is one process-global config.json key governing the whole fleet, with no tenant " +
                "dimension - so on shared infrastructure one subscriber would answer the privacy question for all",
        unDenyInstruction: "do NOT simply remove this deny: give the consent a per-tenant home (config.json has " +
                "none today) and migrate the global value already written, then restore a tenant-scoped route",
        statusCode: StatusCodes.Status404NotFound);

    /// <summary>
    /// Maps the GET/PUT telemetry-consent routes through the shared refusal primitive and returns the denied
    /// handle, so a test can map a brand-new route onto that handle and find it already refused on hosted with
    /// no deny of its own.
    /// </summary>
    public static HostedDenyGroup Map(IEndpointRouteBuilder outer)
    {
        FileLog.Write($"[TelemetryConsentEndpoint] mapping telemetry consent; hosted={GatewayHostedMode.IsHosted} - on hosted EVERY route in this group is refused via the shared refusal primitive (issue #1863)");

        var group = HostedRouteDeny.Group(outer, "", Denial());

        // THE ROUTES ARE MAPPED WHERE `outer` IS NOT IN SCOPE - see the note in SettingsEndpoints.Map.
        // Either of these two routes could otherwise be mapped onto `outer` by a one-word edit, opening it
        // alone while the other stayed denied. Handing the typed handle to a method that never receives the
        // ungrouped builder makes that INEXPRESSIBLE.
        MapRoutes(group);
        return group;
    }

    /// <summary>
    /// The two telemetry-consent routes. Takes the denied GROUP HANDLE and nothing else, deliberately, so
    /// neither route can be mapped around the hosted refusal.
    /// </summary>
    private static void MapRoutes(HostedDenyGroup app)
    {
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
    }

    private sealed record TelemetryConsentBody(bool Enabled);
}
