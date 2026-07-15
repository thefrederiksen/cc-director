using CcDirector.Core.Utilities;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Browser device enrollment (issue #908 for the phone, generalized for the desktop Cockpit in issue
/// #1088): <c>POST /m/enroll</c>. The ONE enrollment seam every browser shell uses - the mobile PWA and
/// the desktop Cockpit both exchange their cloud device key here. The device signs in on
/// devthrottle.com, is registered as a device on the account, and receives its per-device key; it POSTs
/// that key here, and the Gateway confirms (account-scoped) the key belongs to its OWN signed-in
/// account and issues the device a LOCAL device key it validates offline. The account session is never
/// sent here - only the per-device key. The platform field decides the recorded device type
/// (android/ios -> phone, "browser" -> browser; see MobileDeviceEnrollmentService.DeviceTypeForPlatform).
///
/// The route lives under <c>/m/</c>, which the auth middleware lets through without the host-wide token
/// (the sign-in screens must load before the device has any credential). Like
/// <c>/devices/enroll-signed-in</c>,
/// it carries its OWN authorization - the account-scoped device key - so opening the route does not
/// weaken the trust model: a call with no valid device key on this account is answered 403 with no key
/// issued.
///
/// The device key is never written to the log (security rule DT-05); only the device id and platform are.
/// This endpoint delegate is the boundary, so it owns the single try/catch: a cloud failure during the
/// verify becomes a clear 502, never a fabricated success.
/// </summary>
internal static class MobileEnrollmentEndpoint
{
    public static void Map(IEndpointRouteBuilder app, MobileDeviceEnrollmentService service)
    {
        if (service is null) throw new ArgumentNullException(nameof(service));

        app.MapPost("/m/enroll", async (MobileEnrollmentRequest? req, HttpContext ctx) =>
        {
            try
            {
                FileLog.Write($"[MobileEnrollment] POST /m/enroll: deviceId={req?.DeviceId}, platform={req?.Platform} (device key not logged)");

                var outcome = await service
                    .EnrollAsync(req?.DeviceKey, req?.DeviceId, req?.Name, req?.Platform, ctx.RequestAborted)
                    .ConfigureAwait(false);

                // On success, ALSO set the issued local device key as the cc-gateway-token cookie
                // SERVER-side, with the same options POST /login uses. The client mirrors the key into
                // this cookie via document.cookie for the terminal WebSocket, but a JavaScript write is
                // silently REFUSED when a stale HttpOnly cookie of the same name already exists (for
                // example from a previous raw-token /login) - the stale credential then rides every
                // document navigation and WebSocket handshake while the Bearer calls work, stranding
                // the browser half-authenticated. A Set-Cookie response header replaces the old cookie
                // atomically regardless of its HttpOnly flag (found live during issue #1088 proving).
                if (outcome.Kind == MobileEnrollmentOutcome.ResultKind.Ok
                    && !string.IsNullOrEmpty(outcome.LocalDeviceKey))
                {
                    ctx.Response.Cookies.Append(Util.AuthMiddleware.CookieName, outcome.LocalDeviceKey, new CookieOptions
                    {
                        HttpOnly = true,
                        SameSite = SameSiteMode.Lax,
                        Expires = DateTimeOffset.UtcNow.AddDays(30),
                        IsEssential = true,
                    });
                }

                return outcome.Kind switch
                {
                    MobileEnrollmentOutcome.ResultKind.Ok =>
                        Results.Json(new MobileEnrollmentResponse { DeviceKey = outcome.LocalDeviceKey ?? "" }),
                    MobileEnrollmentOutcome.ResultKind.BadRequest =>
                        Results.BadRequest(new { error = outcome.Message }),
                    MobileEnrollmentOutcome.ResultKind.NotSignedIn =>
                        Results.Json(new { error = outcome.Message }, statusCode: StatusCodes.Status409Conflict),
                    MobileEnrollmentOutcome.ResultKind.Rejected =>
                        Results.Json(new { error = outcome.Message }, statusCode: StatusCodes.Status403Forbidden),
                    _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
                };
            }
            catch (Exception ex)
            {
                FileLog.Write($"[MobileEnrollment] POST /m/enroll FAILED: {ex.Message}");
                return Results.Json(
                    new { error = "Could not reach the DevThrottle account service to enroll this device. Try again shortly." },
                    statusCode: StatusCodes.Status502BadGateway);
            }
        });
    }
}
