using CcDirector.Core.Utilities;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Mobile device enrollment (issue #908): <c>POST /m/enroll</c>. This is how the mobile app stops
/// receiving the master token. The phone signs in on devthrottle.com, is registered as a device on the
/// account, and receives its per-device key; it POSTs that key here, and the Gateway confirms
/// (account-scoped) the key belongs to its OWN signed-in account and issues the phone a LOCAL device key
/// it validates offline. The account session is never sent here - only the per-device key.
///
/// The route lives under <c>/m/</c>, which the auth middleware lets through without the host-wide token
/// (the mobile shell must load before the phone has any credential). Like <c>/devices/register</c>, it
/// carries its OWN authorization - the account-scoped device key - so opening the route does not weaken
/// the trust model: a call with no valid device key on this account is answered 403 with no key issued.
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
