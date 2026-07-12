using System.Net;
using CcDirector.Core.Account;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Account;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Pairing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The gate decision for <c>POST /devices/enroll-signed-in</c>, extracted as a pure function so the
/// security guardrails are unit-tested without a web host (issue #1069).
/// </summary>
public enum EnrollGateDecision
{
    /// <summary>All guardrails satisfied - mint (or reuse) the device key.</summary>
    Allow,

    /// <summary>The caller is not a proven loopback (same-machine) connection. 403.</summary>
    NotSameMachine,

    /// <summary>The Gateway is not signed in to DevThrottle, so there is no account to bind the key to. 409.</summary>
    NotSignedIn,
}

/// <summary>
/// Device enrollment via the DevThrottle account sign-in, for a co-located Director (issue #1069).
/// This is the sign-in replacement for the pairing code: a device gets onto the Gateway by signing in
/// to DevThrottle, not by reading a code off the Gateway host.
///
///   POST /devices/enroll-signed-in -> mint (or, if this device already has one, RETURN) this
///       same-machine Director's own per-device key. Gated by three guardrails, each enforced here:
///
///   1. Same-machine PROVEN at the transport layer: the caller's remote address must be loopback.
///      A null or non-loopback address is rejected (403). This is never a self-asserted header/flag,
///      so the endpoint cannot be used from the tailnet or the LAN.
///   2. Bound to the Gateway's current signed-in account identity (GatewaySignInService.GetIdentity).
///      Not signed in -> 409 with a "sign in first" the client maps to opening the browser sign-in.
///   3. Idempotent mint (DeviceRegistry.RegisterIfAbsent): a device that already holds an active key
///      gets the SAME key back - never a fresh key per call. This is the guardrail against the #1136
///      auto-mint key leak; combined with the client only calling enroll once (never from its polling
///      loop), a key is minted at most once per device identity.
///
/// Every mint is logged with the account identity, device id, and timestamp (guardrail 5) so any leak
/// is traceable. Remote/headless Directors (not on the Gateway's machine) enroll via the tailnet
/// callback path and are a follow-up; this endpoint is deliberately same-machine only.
/// </summary>
internal static class SignedInEnrollmentEndpoint
{
    /// <summary>
    /// The pure guardrail decision (guardrails 1 and 2). <paramref name="remoteIp"/> is the caller's
    /// connection address; <paramref name="isSignedIn"/> and <paramref name="identity"/> come from the
    /// Gateway's sign-in service. Returns <see cref="EnrollGateDecision.Allow"/> only when the caller is a
    /// proven loopback connection AND the Gateway is signed in with a resolved identity.
    /// </summary>
    public static EnrollGateDecision Evaluate(IPAddress? remoteIp, bool isSignedIn, AccountIdentity? identity)
    {
        // Guardrail 1: same-machine must be PROVEN. Unknown (null) or non-loopback is rejected - we never
        // treat an unknown caller as same-machine here (unlike sign-in routing), because this mints a key.
        if (remoteIp is null || !IPAddress.IsLoopback(remoteIp))
            return EnrollGateDecision.NotSameMachine;

        // Guardrail 2: there must be a signed-in account to bind the key to.
        if (!isSignedIn || identity is null)
            return EnrollGateDecision.NotSignedIn;

        return EnrollGateDecision.Allow;
    }

    public static void Map(IEndpointRouteBuilder app, DeviceRegistry devices, GatewaySignInService? signIn, ChildDeviceMirrorService? mirror = null)
    {
        if (devices is null) throw new ArgumentNullException(nameof(devices));

        app.MapPost("/devices/enroll-signed-in", (EnrollSignedInRequest req, HttpContext ctx) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.DeviceId))
                return Results.BadRequest(new { error = "deviceId is required" });

            var remoteIp = ctx.Connection.RemoteIpAddress;
            var identity = signIn?.GetIdentity();
            var decision = Evaluate(remoteIp, signIn?.IsSignedIn() ?? false, identity);

            switch (decision)
            {
                case EnrollGateDecision.NotSameMachine:
                    FileLog.Write($"[SignedInEnrollment] REJECTED not-same-machine: deviceId={req.DeviceId}, remoteIp={remoteIp?.ToString() ?? "<null>"}");
                    return Results.Json(
                        new { error = "This endpoint enrolls only a Director on the Gateway's own machine." },
                        statusCode: StatusCodes.Status403Forbidden);

                case EnrollGateDecision.NotSignedIn:
                    FileLog.Write($"[SignedInEnrollment] REJECTED not-signed-in: deviceId={req.DeviceId}");
                    return Results.Json(
                        new { error = "Sign in to DevThrottle on the Gateway first, then enroll." },
                        statusCode: StatusCodes.Status409Conflict);
            }

            // Guardrail 3: idempotent - reuse the existing active key if present, mint at most once.
            var response = devices.RegisterIfAbsent(req.DeviceId, req.MachineName, req.Platform, req.DeviceType);

            // Guardrail 5: log the mint with the bound account identity, device id, and (via FileLog) time.
            FileLog.Write($"[SignedInEnrollment] enrolled deviceId={req.DeviceId}, machine={req.MachineName}, account={identity!.Email} (via {identity.Provider}), deviceCount={response.DeviceCount}");

            // Mirror this device up to the account roster, best-effort and fire-and-forget - it never
            // blocks or fails enrollment (the Director already holds its local key).
            if (mirror is not null)
                _ = mirror.MirrorChildUpAsync(req.DeviceId);

            return Results.Json(response, statusCode: StatusCodes.Status200OK);
        });
    }
}
