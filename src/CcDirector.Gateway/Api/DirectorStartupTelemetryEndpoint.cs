using CcDirector.Core.Utilities;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Gateway Centralization Phase 1 (issue #631): the inbound Director-STARTUP telemetry endpoint. A
/// Director POSTs a startup event here on launch (the Director-side firing is a separate issue, #632)
/// and the Gateway RECORDS it so the startup is observable Gateway-side, then BEST-EFFORT forwards it
/// to the cloud ONLY when a startup endpoint is configured.
///
/// Wire contract: the inbound request carries the body
/// <c>{ "director_id": "...", "machine_name": "...", "app_version": "..." }</c>. The body shape is
/// PROVISIONAL pending the backend startup-event contract (the backend has no Director-startup endpoint
/// yet - see the plan's Open questions), so forwarding is GATED on configuration: the event is forwarded
/// to the cloud only when the <c>DEVTHROTTLE_STARTUP_TELEMETRY_URL</c> environment variable is set on the
/// Gateway. When it is NOT set, the Gateway records the event locally, logs that no cloud startup
/// endpoint is configured, and still answers 202 Accepted (no error - the record is the value).
///
/// Reuse (issues #628 / #629): when a startup URL IS configured the event is enqueued into the same
/// durable <see cref="TelemetryRetryQueue"/> the login relay uses, so delivery, retry-with-backoff,
/// FIFO flush, the bound, and restart survival are shared - this endpoint adds no new forwarder. The
/// enqueued per-event bearer is always null: this endpoint never carried an inbound Director token.
///
/// Gateway Centralization Phase 2 (issue #639): like the login relay, when the shared queue is wired
/// with the Gateway's token source the Gateway attaches its OWN account token at forward time, and a
/// startup forward is deferred (kept queued) until the Gateway is signed in. So the Gateway is the
/// single egress here too, and no Director token is ever attached.
/// </summary>
internal static class DirectorStartupTelemetryEndpoint
{
    /// <summary>The environment variable that configures the cloud startup endpoint on the Gateway.</summary>
    public const string TargetUrlEnvVar = "DEVTHROTTLE_STARTUP_TELEMETRY_URL";

    /// <summary>
    /// Resolves the configured cloud startup URL: the <see cref="TargetUrlEnvVar"/> environment value
    /// when set (trimmed, non-empty), otherwise null. There is NO default (unlike the login relay): the
    /// backend startup endpoint is a flagged dependency, so forwarding only happens when an operator has
    /// explicitly pointed the Gateway at one.
    /// </summary>
    public static string? ResolveTargetUrl()
    {
        var fromEnv = Environment.GetEnvironmentVariable(TargetUrlEnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return fromEnv.Trim();
        return null;
    }

    /// <summary>
    /// Maps <c>POST /telemetry/director-startup</c>. The inbound Gateway token convention (when Gateway
    /// auth is enabled) is applied by the host-wide auth middleware, exactly like the other Gateway
    /// endpoints. The event is always recorded; it is enqueued for cloud delivery into
    /// <paramref name="queue"/> only when a startup URL is configured.
    /// </summary>
    /// <param name="app">The route builder.</param>
    /// <param name="queue">The durable retry queue that owns cloud delivery (issues #628 / #629).</param>
    /// <param name="tenants">
    /// The auth-boundary tenant binder (audit MTR gap C). Resolves the SERVER-BOUND tenant of the
    /// authenticated request so the queued event is partitioned by tenant; on hosted an unresolved tenant is
    /// a 403 DENY, on self-host it is always <see cref="Core.Tenancy.TenantId.Local"/>.
    /// </param>
    /// <param name="directors">
    /// The Director registry (audit MTR gap C). On hosted it is asked whether the posted <c>director_id</c>
    /// is PROVABLY OWNED by the caller's server-resolved tenant; a request that cannot prove ownership
    /// (blank/malformed, unknown, or owned by another tenant) is rejected - a caller may not create a startup
    /// observation for a director_id it does not own.
    /// </param>
    public static void Map(IEndpointRouteBuilder app, TelemetryRetryQueue queue, HostedTenantBoundary tenants, DirectorRegistry directors)
    {
        if (queue is null)
            throw new ArgumentNullException(nameof(queue));
        if (tenants is null)
            throw new ArgumentNullException(nameof(tenants));
        if (directors is null)
            throw new ArgumentNullException(nameof(directors));

        app.MapPost("/telemetry/director-startup", async (HttpContext ctx) =>
        {
            // Resolve the tenant from the AUTHENTICATED request (never from the body). On hosted a key with
            // no bound tenant is a DENY; on self-host this is always Local. It is the partition the queue
            // bounds and flushes by.
            var tenant = tenants.ResolveRequestTenant(ctx);
            if (tenant is null)
            {
                FileLog.Write("[DirectorStartupTelemetryEndpoint] director-startup DENIED: no tenant resolved for the authenticated caller (hosted deny-by-default)");
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            // Read the inbound body (the event JSON) verbatim so it is forwarded UNCHANGED. The body
            // shape is provisional; the Gateway does not reshape it.
            string body;
            using (var reader = new StreamReader(ctx.Request.Body))
                body = await reader.ReadToEndAsync(ctx.RequestAborted);

            // Record the event Gateway-side so the startup is observable here regardless of whether a
            // cloud endpoint exists. director_id + app_version are pulled out for the record line; the
            // rest of the body is carried verbatim into the queue (when forwarding is configured).
            var (directorId, appVersion) = ReadRecordFields(body);

            // Ownership gate (audit MTR gap C): on hosted, a caller may only create a startup observation for
            // a director_id its OWN tenant PROVABLY owns. Anything the caller cannot prove it owns - a
            // blank/malformed id, an id owned by ANOTHER tenant (the "submit B's director id to create a false
            // startup observation for B" the audit describes), or an as-yet-UNKNOWN id registered to nobody -
            // is rejected, never recorded and never enqueued. A startup report that races the tunnel Hello and
            // arrives before its own id is registered is therefore dropped; that is the accepted cost of not
            // accepting a forgeable cross-tenant observation, for a best-effort, swallowed startup ping. On
            // self-host (single tenant, no forgery surface, and the id may legitimately be unregistered) this
            // gate never fires.
            if (tenants.IsHosted && !directors.IsDirectorOwnedByTenant(tenant.Value, directorId))
            {
                FileLog.Write($"[DirectorStartupTelemetryEndpoint] director-startup DENIED: director_id={directorId} is not provably owned by the caller (tenant={tenant.Value.ToLogString()}); refusing a startup observation for an unowned director id");
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }

            FileLog.Write($"[DirectorStartupTelemetryEndpoint] director-startup recorded: director_id={directorId}, app_version={appVersion}, tenant={tenant.Value.ToLogString()}");

            var targetUrl = ResolveTargetUrl();
            if (targetUrl is null)
            {
                // No cloud startup endpoint configured: record locally and return success. This is the
                // expected Phase 1 state (the backend has no startup endpoint yet) - not an error.
                FileLog.Write("[DirectorStartupTelemetryEndpoint] no cloud startup endpoint configured (DEVTHROTTLE_STARTUP_TELEMETRY_URL unset); recorded locally only, not forwarded");
            }
            else
            {
                // A startup URL is configured: enqueue for durable, retried delivery via the shared
                // queue (issues #628 / #629). No Bearer - startup is unauthenticated to the cloud here.
                // Partitioned by the server-resolved tenant so one tenant can never evict/block another's.
                FileLog.Write($"[DirectorStartupTelemetryEndpoint] forwarding configured -> enqueue for {targetUrl} (tenant={tenant.Value.ToLogString()})");
                queue.Enqueue(targetUrl, body, bearer: null, tenant.Value);
            }

            // 202 Accepted is the truthful answer on both paths: "received and recorded" - and, when a
            // URL is configured, "queued for delivery", never a guarantee the cloud has it yet.
            return Results.StatusCode(StatusCodes.Status202Accepted);
        });
    }

    /// <summary>
    /// Pulls <c>director_id</c> and <c>app_version</c> out of the inbound body for the record line.
    /// A missing or unparseable field is recorded as the literal "(none)" so the record line is always
    /// written - the record is best-effort observability, never a validation gate on the 202.
    /// </summary>
    private static (string DirectorId, string AppVersion) ReadRecordFields(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return ("(none)", "(none)");

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            var root = doc.RootElement;
            var directorId = root.TryGetProperty("director_id", out var d) && d.ValueKind == System.Text.Json.JsonValueKind.String
                ? d.GetString() ?? "(none)"
                : "(none)";
            var appVersion = root.TryGetProperty("app_version", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() ?? "(none)"
                : "(none)";
            return (directorId, appVersion);
        }
        catch (System.Text.Json.JsonException)
        {
            // A malformed body still records (with placeholders) and still returns 202 - the inbound
            // event is best-effort, and the verbatim body is what would be forwarded if configured.
            return ("(none)", "(none)");
        }
    }
}
