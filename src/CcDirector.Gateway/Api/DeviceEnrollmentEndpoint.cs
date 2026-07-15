using CcDirector.Gateway.Pairing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The Gateway device registry listing.
///
///   GET /devices -> the host-readable registry listing (id, machine, issued-at, status).
///                   The per-device key is NEVER returned.
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
    public static void Map(IEndpointRouteBuilder app, DeviceRegistry devices)
    {
        if (devices is null) throw new ArgumentNullException(nameof(devices));

        app.MapGet("/devices", () =>
        {
            var list = devices.List();
            return Results.Json(list);
        });
    }
}
