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
///   -&gt; 200 { deviceKey, ... }   issue this device's key (a fresh one if it is already enrolled), bound to the tenant
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

    /// <param name="entitlements">
    /// The paid-entitlement gate. NULL means no gate - that is the self-host case, where there is no billing
    /// and no boundary, and enrollment behaves exactly as it always has. On hosted this is REQUIRED, and the
    /// gate is what makes hosted a paid product rather than a free one.
    /// </param>
    public static void Map(IEndpointRouteBuilder app, DeviceRegistry devices,
        Tenancy.TenantRegistry tenants, JwtAccessTokenValidator accountTokenValidator,
        Tenancy.EntitlementRegistry? entitlements = null)
    {
        if (devices is null) throw new ArgumentNullException(nameof(devices));
        if (tenants is null) throw new ArgumentNullException(nameof(tenants));
        if (accountTokenValidator is null) throw new ArgumentNullException(nameof(accountTokenValidator));

        app.MapPost(Path, (EnrollSignedInRequest req, HttpContext ctx) =>
        {
            var result = Enroll(BearerToken.Read(ctx), req, devices, tenants, accountTokenValidator, entitlements, DateTime.UtcNow);
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
        Tenancy.TenantRegistry tenants, JwtAccessTokenValidator accountTokenValidator,
        Tenancy.EntitlementRegistry? entitlements = null, DateTime? nowUtc = null)
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

        // THE PAID GATE, between subject-verification and tenant-mint. It sits here, and not later, because
        // minting a tenant is itself giving something away: an unpaid account must leave no trace and hold no
        // key. No device key means no tunnel, no cockpit and no mobile, which is the whole meaning of
        // "approval before you can use a hosted tenant".
        //
        // THREE OUTCOMES, and keeping them apart is the entire correctness of this gate:
        //  - Entitled     -> fall through and enroll.
        //  - NotEntitled  -> 402. We LOOKED and the account has no valid entitlement. This is knowledge.
        //  - Unknown      -> 503 and RETRY. The read FAILED, so we do not know. It must not deny and it must
        //                    not grant.
        //
        // The asymmetry matters in both directions and they are not equally bad. A false 402 locks out a
        // customer who has paid. A false MINT gives the product away for nothing - and that one is silent,
        // because a successful enrollment looks exactly like a correct one. So the mint happens ONLY on a
        // confirmed entitlement: ignorance mints nothing, denies nothing, and says try again.
        //
        // Null entitlements is the SELF-HOST case - no gate at all, unchanged behaviour. That is the control.
        if (entitlements is not null)
        {
            var outcome = entitlements.LookupBySubject(validation.Subject, nowUtc ?? DateTime.UtcNow);
            if (outcome == Tenancy.EntitlementOutcome.Unknown)
            {
                FileLog.Write("[HostedEnrollment] RETRY: the entitlement read failed, so entitlement is UNKNOWN - " +
                              "not enrolling and NOT refusing (no tenant minted, no device key issued)");
                return new EnrollResult(StatusCodes.Status503ServiceUnavailable, null,
                    "the entitlement service is temporarily unavailable; please try again");
            }

            if (outcome == Tenancy.EntitlementOutcome.NotEntitled)
            {
                FileLog.Write("[HostedEnrollment] REFUSED: the entitlement read succeeded and this account has no active entitlement (no tenant minted, no device key issued)");
                return new EnrollResult(StatusCodes.Status402PaymentRequired, null,
                    "this account does not have an active hosted subscription");
            }
        }

        // The email is DISPLAY METADATA only (never the mapping key). Read it from the same verified token.
        var email = JwtIdentityReader.Read(bearer)?.Email;

        // Mint-or-lookup the tenant for this verified subject (same account -> same tenant).
        var tenant = tenants.MintOrLookupBySubject(validation.Subject, email);

        // The device id is CLIENT-supplied, so it must NEVER be the registry key on its own: two different
        // accounts presenting the same deviceId would otherwise collide on ONE registry entry, and enrolling
        // would hand one account a working key on the OTHER's record (which SetAccountBinding then rebinds to
        // the new tenant) - letting a pre-enroller take over the victim's device entry. Namespacing the registry
        // id with a ONE-WAY HASH of the resolved tenant makes cross-account collision impossible (different
        // accounts -> different tenants -> different hashes -> different device spaces) while staying
        // idempotent for one account (same tenant -> same hash). The hash - not the subject and not the raw
        // tenant id, both of which must never be logged - is what the device registry logs as the device id.
        var scopedDeviceId = NamespaceHash(tenant.Value) + "|" + req.DeviceId;
        var response = devices.RegisterForTenant(
            tenant,
            validation.Subject,
            scopedDeviceId,
            req.MachineName,
            req.Platform,
            req.DeviceType);

        // RegisterForTenant counts inside the same tenant-bound transaction, so the response never exposes
        // the hosted fleet-wide count.

        FileLog.Write($"[HostedEnrollment] enrolled deviceId={req.DeviceId}, machine={req.MachineName} " +
                      $"-> bound to its account tenant (no subject/email logged), deviceCount={response.DeviceCount}");
        return new EnrollResult(StatusCodes.Status200OK, response, "");
    }

    /// <summary>A one-way SHA-256 hex hash used to namespace the device registry id per tenant WITHOUT ever
    /// putting the subject or the raw tenant id into a value the device registry logs.</summary>
    private static string NamespaceHash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
