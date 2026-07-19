using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Prompts;

/// <summary>
/// The Gateway's prompt-log front door (issue #1551).
///
/// POST /prompts - a Director pushes what it captured. The Director keeps no copy; this is the single
/// copy, which is why the write is acknowledged with a real count rather than fire-and-forget.
///
/// GET /prompts  - anyone asking for history asks here. That is the point of the log living on the
/// Gateway: it already has the whole fleet's record, so nothing has to go hunting across machines.
///
/// TENANT-SCOPED (issue #1848). "The whole fleet's record" means the REQUESTING ACCOUNT'S fleet. Both verbs
/// resolve the request's tenant from the authenticated device key with the same seam the cockpit read path
/// uses, and write into / read out of only that tenant's partition. Before this, neither handler took an
/// <c>HttpContext</c> at all - so neither could resolve a tenant even in principle, and a hosted GET returned
/// every account's full prompt TEXT. On hosted a request whose key has no bound tenant is DENIED (403); it is
/// never served the Local partition. Self-host (no boundary) is always Local, exactly as before.
/// </summary>
public static class PromptEndpoints
{
    public static void Map(IEndpointRouteBuilder app, GatewayPromptLog log,
        Tenancy.HostedTenantBoundary? tenantBoundary = null)
    {
        var store = log ?? throw new ArgumentNullException(nameof(log));

        app.MapPost("/prompts", (HttpContext ctx, PromptIngestRequest? request) =>
        {
            var tenant = ResolveTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            if (request?.Records is null || request.Records.Count == 0)
                return Results.BadRequest(new { error = "records is required and must not be empty" });

            var written = store.Append(tenant.Value, request.Records);
            FileLog.Write($"[PromptEndpoints] POST /prompts: tenant={tenant.Value.ToLogString()}, received {request.Records.Count}, wrote {written}");
            return Results.Ok(new PromptIngestResponse { Written = written });
        });

        app.MapGet("/prompts", (HttpContext ctx, string? from, string? to) =>
        {
            var tenant = ResolveTenant(ctx, tenantBoundary);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            // Default to today so a bare GET /prompts is useful rather than an error.
            var fromUtc = ParseDay(from) ?? DateTime.UtcNow.Date;
            var toUtc = ParseDay(to) ?? DateTime.UtcNow.Date;
            if (toUtc < fromUtc)
                return Results.BadRequest(new { error = "'to' is earlier than 'from'" });

            var records = store.Read(tenant.Value, fromUtc, toUtc);
            return Results.Ok(new { count = records.Count, records });
        });
    }

    /// <summary>
    /// Resolve the request's tenant from the AUTHENTICATED device key the auth middleware stashed - the same
    /// seam the tenant-aware cockpit read path uses. Null means DENY: on hosted an authenticated request whose
    /// key has no bound tenant is refused, never served the Local partition. Self-host, or no boundary (older
    /// callers and tests), is always Local.
    /// </summary>
    private static TenantId? ResolveTenant(HttpContext ctx, Tenancy.HostedTenantBoundary? boundary)
        => boundary is null ? TenantId.Local : boundary.ResolveRequestTenant(ctx);

    /// <summary>Parse a yyyy-MM-dd day, or null when absent/unparseable.</summary>
    private static DateTime? ParseDay(string? value)
        => DateTime.TryParse(value, null,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed.Date
            : null;
}
