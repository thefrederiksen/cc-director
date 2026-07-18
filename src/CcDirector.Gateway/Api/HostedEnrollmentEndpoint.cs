using System;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Pairing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// Hosted device enrollment (Hosted Multi-Tenancy increment 1): a REMOTE Director enrolls with the HOSTED
/// Gateway by presenting its OWN verified DevThrottle (Supabase) account token, and receives a per-device
/// key bound to that account's tenant. This is the hosted counterpart to the loopback-only, single-owner
/// <see cref="SignedInEnrollmentEndpoint"/>: on the hosted Gateway there is NO single signed-in Gateway
/// account, so the caller's OWN account token is the authorization, and each distinct account (subject) maps
/// to its own tenant.
///
///   POST /devices/enroll-hosted
///     Authorization: Bearer &lt;the caller's Supabase access token&gt;
///     { deviceId, machineName, platform, deviceType }
///   -&gt; 200 { deviceKey, ... }   mint (or return, if already present) this device's key, bound to the tenant
///   -&gt; 400                       deviceId missing
///   -&gt; 401                       missing / invalid / expired account token (no verified subject to bind)
///
/// The account token is validated (signature + expiry + audience + issuer) and its stable subject extracted;
/// the subject maps (mint-or-lookup) to a tenant; the device is registered and bound to (subject, tenant), so
/// the tunnel can later resolve the tenant from the SAME per-device key. The route is in the AuthMiddleware
/// public set because it carries its OWN authorization (the account token) - a fresh remote Director has no
/// Gateway device key yet. Security: the subject and email are personally identifying, so NEITHER is logged.
/// </summary>
internal static class HostedEnrollmentEndpoint
{
    public const string Path = "/devices/enroll-hosted";

    /// <summary>The outcome of <see cref="Enroll"/>, extracted so the enrollment logic is unit-tested without a
    /// web host. <see cref="Response"/> is set only on <see cref="Status"/> 200.</summary>
    public sealed record EnrollResult(int Status, DeviceRegistrationResponse? Response, string Error);

    public static void Map(IEndpointRouteBuilder app, DeviceRegistry devices,
        Tenancy.TenantRegistry tenants, JwtAccessTokenValidator accountTokenValidator)
    {
        if (devices is null) throw new ArgumentNullException(nameof(devices));
        if (tenants is null) throw new ArgumentNullException(nameof(tenants));
        if (accountTokenValidator is null) throw new ArgumentNullException(nameof(accountTokenValidator));

        app.MapPost(Path, (EnrollSignedInRequest req, HttpContext ctx) =>
        {
            var result = Enroll(ReadBearer(ctx), req, devices, tenants, accountTokenValidator);
            return result.Status == StatusCodes.Status200OK
                ? Results.Json(result.Response, statusCode: StatusCodes.Status200OK)
                : Results.Json(new { error = result.Error }, statusCode: result.Status);
        });
    }

    /// <summary>
    /// The enrollment decision as a pure function (Hosted Multi-Tenancy increment 1), so the security-relevant
    /// steps - validate the account token, extract the subject, map it to a tenant, bind the device - are
    /// unit-tested without a web host. Validates the account token fully (signature + expiry + audience +
    /// issuer); a token that is not authorization-valid or carries no subject is a 401 (no verified account to
    /// bind). The email is display metadata only, never the mapping key. Nothing personally identifying is
    /// logged.
    /// </summary>
    public static EnrollResult Enroll(string? bearer, EnrollSignedInRequest? req, DeviceRegistry devices,
        Tenancy.TenantRegistry tenants, JwtAccessTokenValidator accountTokenValidator)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.DeviceId))
            return new EnrollResult(StatusCodes.Status400BadRequest, null, "deviceId is required");

        if (bearer is null)
            return new EnrollResult(StatusCodes.Status401Unauthorized, null, "an account access token is required");

        var validation = accountTokenValidator.ValidateForAuthorization(bearer);
        if (!validation.IsValid || string.IsNullOrEmpty(validation.Subject))
        {
            FileLog.Write("[HostedEnrollment] REJECTED: the account token is not authorization-valid (no subject to bind)");
            return new EnrollResult(StatusCodes.Status401Unauthorized, null, "the account token is not valid");
        }

        // The email is DISPLAY METADATA only (never the mapping key). Read it from the same verified token.
        var email = JwtIdentityReader.Read(bearer)?.Email;

        // Mint-or-lookup the tenant for this verified subject (same account -> same tenant).
        var tenant = tenants.MintOrLookupBySubject(validation.Subject, email);

        // The device id is CLIENT-supplied, so it must NEVER be the registry key on its own: two different
        // accounts presenting the same deviceId would otherwise collide, and RegisterIfAbsent's idempotency
        // would hand one account the OTHER's key (which SetAccountBinding then rebinds to the new tenant) -
        // letting a pre-enroller keep a key that becomes bound to the victim's tenant. Namespacing the
        // registry id with the VERIFIED subject makes cross-account collision impossible: each account gets
        // its own device space, so RegisterIfAbsent only ever returns THIS account's own key, and the binding
        // is always to this account's tenant. The subject is a fixed Supabase id (prepended), so no
        // client-supplied suffix can make one account's id collide with another's.
        var scopedDeviceId = validation.Subject + "|" + req.DeviceId;
        var response = devices.RegisterIfAbsent(scopedDeviceId, req.MachineName, req.Platform, req.DeviceType);
        devices.SetAccountBinding(scopedDeviceId, validation.Subject, tenant.Value);

        FileLog.Write($"[HostedEnrollment] enrolled deviceId={req.DeviceId}, machine={req.MachineName} " +
                      $"-> bound to its account tenant (no subject/email logged), deviceCount={response.DeviceCount}");
        return new EnrollResult(StatusCodes.Status200OK, response, "");
    }

    private static string? ReadBearer(HttpContext ctx)
    {
        if (!ctx.Request.Headers.TryGetValue("Authorization", out var header))
            return null;
        var raw = header.ToString();
        const string prefix = "Bearer ";
        if (!raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        var token = raw.Substring(prefix.Length).Trim();
        return string.IsNullOrEmpty(token) ? null : token;
    }
}
