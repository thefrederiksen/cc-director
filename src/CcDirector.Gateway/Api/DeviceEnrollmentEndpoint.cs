using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Pairing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The Gateway device registry listing.
///
///   GET /devices -> the CALLER's own tenant's device listing (id, machine, issued-at, status).
///                   The per-device key is NEVER returned, and no other tenant's devices are.
///
/// Enrolling a NEW device is not served here. A device joins by signing in to the same DevThrottle
/// account: <see cref="SignedInEnrollmentEndpoint"/> for a co-located Director, and the mobile/browser
/// enrollment endpoints for a phone or browser.
///
/// The 4-digit local pairing code (issue #469) and its POST /devices/register route were removed with
/// the Gateway's user interface. The code was shown only on the Gateway host's own screen, so it
/// required a window a headless Gateway does not have; its only client-side caller had already been
/// replaced by the sign-in flow (epic #1069) and was dead code by then.
/// </summary>
internal static class DeviceEnrollmentEndpoint
{
    public static void Map(IEndpointRouteBuilder app, DeviceRegistry devices,
        // MTR-12: the auth-boundary tenant binder. The listing is scoped to the REQUEST's own tenant, resolved
        // from the AUTHENTICATED per-device key the auth middleware stashed (never from client input). Null
        // (self-host, tests) is the single Local tenant, so its own devices list exactly as before.
        Tenancy.HostedTenantBoundary? tenantBoundary = null)
    {
        if (devices is null) throw new ArgumentNullException(nameof(devices));

        app.MapGet("/devices", (HttpContext ctx) =>
        {
            // MTR-12: scope the listing to the caller's own tenant so an authenticated account cannot read back a
            // full multi-tenant device inventory (every id / machine name / issued time across every tenant). On
            // self-host every caller is the single Local tenant (unchanged behaviour); on hosted a request with no
            // bound tenant is DENIED (403) - never a fall-back to Local, and never another tenant's devices.
            var tenant = tenantBoundary is null ? TenantId.Local : tenantBoundary.ResolveRequestTenant(ctx);
            if (tenant is null)
                return Results.Json(new { error = "no tenant is bound to this request" },
                    statusCode: StatusCodes.Status403Forbidden);

            var list = devices.ListForTenant(tenant.Value);
            return Results.Json(list);
        });
    }
}
