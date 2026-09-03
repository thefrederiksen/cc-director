using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// A request as the Gateway's own pipeline hands one to a route, for the tests that need the evidence a
/// person asked. It is here rather than copied into each test file because the promotion bound is now
/// about the REQUEST, and three copies of "what an authenticated request looks like" would be three places
/// for a test to quietly stop meaning what it says.
///
/// THE MARKERS HERE ARE THE MIDDLEWARE'S OWN CONSTANTS, NOT STRINGS THIS FILE MADE UP (fix round D). The
/// first version of this helper set an item named "DeviceKeyId", which the grant also read - and nothing
/// in the Gateway ever wrote, so every unit test passed while every real device-key request reaching the
/// promote route was refused as having no caller. A helper that describes a request the pipeline never
/// produces is a helper that lets the test and the product agree with each other and with nothing else.
/// </summary>
internal static class AnInboundRequest
{
    /// <summary>A request the device-key middleware authenticated, naming the device it matched -
    /// marked exactly as <see cref="AuthMiddleware"/> marks one.</summary>
    public static HttpContext FromDevice(string deviceId = "device-9f2c")
    {
        var http = new DefaultHttpContext();
        http.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "raw-device-key-never-read";
        http.Items[AuthMiddleware.DeviceKeyItemKey] = "raw-device-key-never-read";
        http.Items[AuthMiddleware.AuthenticatedDeviceItemKey] =
            new DeviceCredentialIdentity(deviceId, "tenant-local", "Cockpit", "Active");
        return http;
    }

    /// <summary>A request a SESSION key authenticated - an agent's own credential - marked exactly as
    /// <see cref="AuthMiddleware"/> marks one. The one caller the promotion grant must refuse on the
    /// credential itself (ruling D11).</summary>
    public static HttpContext FromSessionKey(string sessionId = "3f1a2b4c-0000-4000-8000-000000000001")
    {
        var http = new DefaultHttpContext();
        http.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "raw-session-key-never-read";
        http.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
            new SessionCredentialIdentity(Guid.Parse(sessionId), CcDirector.Core.Tenancy.TenantId.Local, "director-1");
        return http;
    }

    /// <summary>A request carrying a signed-in principal, as the account routes see one.</summary>
    public static HttpContext FromSignedInPerson(string name)
    {
        var http = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.Name, name) }, authenticationType: "TestAuthentication")),
        };
        return http;
    }

    /// <summary>A request the pipeline could not name - which is what anything running on its own has.</summary>
    public static HttpContext FromNobody() => new DefaultHttpContext();
}
