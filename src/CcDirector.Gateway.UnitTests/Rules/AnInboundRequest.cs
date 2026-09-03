using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// A request as the Gateway's own pipeline hands one to a route, for the tests that need the evidence a
/// person asked. It is here rather than copied into each test file because the promotion bound is now
/// about the REQUEST, and three copies of "what an authenticated request looks like" would be three places
/// for a test to quietly stop meaning what it says.
/// </summary>
internal static class AnInboundRequest
{
    /// <summary>A request the device-key middleware authenticated, naming the device it matched.</summary>
    public static HttpContext FromDevice(string deviceId = "device-9f2c")
    {
        var http = new DefaultHttpContext();
        http.Items["DeviceKeyId"] = deviceId;
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
