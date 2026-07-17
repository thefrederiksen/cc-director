using CcDirector.Core.Network;
using System.Net;
using System.Net.Http.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Enrolls THIS Director with its own Gateway using the DevThrottle account sign-in (epic #1069).
/// This is the one-time "Connect to Gateway" act: the Gateway must be signed in to DevThrottle, and
/// this client POSTs <c>/devices/enroll-signed-in</c> to obtain the unique per-device key the Gateway
/// issues. The key is then written to the local credential file the Director and the local cc-* tools
/// both read.
///
/// Separate from <see cref="GatewayClient"/> (the running registration/heartbeat lifecycle): a
/// brand-new device has no credential yet, so enrollment is a standalone request authorized by the
/// Gateway's signed-in account plus a proven loopback origin, not by a token. Pure of UI - the panel
/// calls this and renders the result.
/// </summary>
public static class GatewayEnrollmentClient
{
    /// <summary>
    /// Enroll THIS co-located Director with its own Gateway using the DevThrottle account sign-in instead of
    /// a pairing code (issue #1069): POST <c>/devices/enroll-signed-in</c>. The Gateway mints (or returns, if
    /// already present) this Director's own per-device key - gated on the Gateway being signed in AND the
    /// caller being a proven loopback same-machine connection. The distinct outcomes are what let the panel
    /// orchestrate the fresh-device flow: <see cref="EnrollOutcome.GatewayNotSignedIn"/> (409) means trigger
    /// the browser sign-in first; <see cref="EnrollOutcome.NotLoopback"/> (403) means this is a remote
    /// Director (epic #1069 case B). Never throws for an expected failure.
    ///
    /// The caller MUST pass a LOOPBACK <paramref name="gatewayUrl"/> (http://127.0.0.1:&lt;local gateway
    /// port&gt;) for the same-machine case - the Gateway's guardrail 1 checks the caller's remote IP with
    /// IPAddress.IsLoopback, so dialing the machine-name or tailnet address instead would 403 even on the
    /// same machine. A brand-new device has no Gateway token, so it passes <paramref name="token"/> null and
    /// the route is public (its own guards do the work).
    /// </summary>
    public static async Task<EnrollSignedInResult> EnrollSignedInAsync(
        string gatewayUrl, string? token, string deviceId, string machineName, string platform, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl))
            return new EnrollSignedInResult(EnrollOutcome.Failed, null, "No Gateway URL is configured.");
        if (string.IsNullOrWhiteSpace(deviceId))
            return new EnrollSignedInResult(EnrollOutcome.Failed, null, "This Director has no device id.");

        FileLog.Write($"[GatewayEnrollmentClient] EnrollSignedInAsync: gateway={gatewayUrl}, deviceId={deviceId}, machine={machineName}");
        var request = new EnrollSignedInRequest
        {
            DeviceId = deviceId,
            MachineName = machineName,
            Platform = platform,
            DeviceType = "workstation",
        };

        using var http = new HttpClient(GatewayHttp.Handler()) { Timeout = TimeSpan.FromSeconds(15) };
        http.BaseAddress = new Uri(gatewayUrl.TrimEnd('/') + "/");
        if (!string.IsNullOrEmpty(token))
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsJsonAsync("devices/enroll-signed-in", request, ct);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayEnrollmentClient] EnrollSignedInAsync transport FAILED: {ex.Message}");
            return new EnrollSignedInResult(EnrollOutcome.Failed, null,
                $"Could not reach the Gateway at {gatewayUrl}: {ex.Message}");
        }

        if (resp.StatusCode == HttpStatusCode.Conflict)
            return new EnrollSignedInResult(EnrollOutcome.GatewayNotSignedIn, null,
                "Sign in to DevThrottle first, then this device gets its token.");
        if (resp.StatusCode == HttpStatusCode.Forbidden)
            return new EnrollSignedInResult(EnrollOutcome.NotLoopback, null,
                "This Director must be on the Gateway's own machine to enroll this way.");
        if (!resp.IsSuccessStatusCode)
        {
            FileLog.Write($"[GatewayEnrollmentClient] EnrollSignedInAsync failed: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            return new EnrollSignedInResult(EnrollOutcome.Failed, null,
                $"The Gateway refused enrollment: HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
        }

        var body = await resp.Content.ReadFromJsonAsync<DeviceRegistrationResponse>(ct);
        if (body is null || string.IsNullOrWhiteSpace(body.DeviceKey))
        {
            FileLog.Write("[GatewayEnrollmentClient] EnrollSignedInAsync: 2xx with no device key in body");
            return new EnrollSignedInResult(EnrollOutcome.Failed, null, "The Gateway returned no device key.");
        }

        FileLog.Write($"[GatewayEnrollmentClient] EnrollSignedInAsync: per-device key issued for machine={body.MachineName}");
        return new EnrollSignedInResult(EnrollOutcome.Enrolled, body, "");
    }
}

/// <summary>
/// The distinct outcomes of <see cref="GatewayEnrollmentClient.EnrollSignedInAsync"/> (epic #1069), so the
/// connect panel can orchestrate the fresh-device flow without string-matching failure messages.
/// </summary>
public enum EnrollOutcome
{
    /// <summary>200: a per-device key was minted (or returned). <see cref="EnrollSignedInResult.Value"/> holds it.</summary>
    Enrolled,

    /// <summary>409: the Gateway is not signed in to DevThrottle yet - the panel must open the browser sign-in first.</summary>
    GatewayNotSignedIn,

    /// <summary>403: the caller is not a loopback/same-machine connection - this is a remote Director (case B).</summary>
    NotLoopback,

    /// <summary>A transport error or any other non-success - a real failure to surface.</summary>
    Failed,
}

/// <summary>The outcome of a signed-in enrollment attempt (epic #1069).</summary>
/// <param name="Outcome">Which of the distinct results occurred.</param>
/// <param name="Value">The issued device key response when <see cref="Outcome"/> is <see cref="EnrollOutcome.Enrolled"/>; otherwise null.</param>
/// <param name="Message">A human-readable reason for the non-enrolled outcomes.</param>
public sealed record EnrollSignedInResult(EnrollOutcome Outcome, DeviceRegistrationResponse? Value, string Message);
