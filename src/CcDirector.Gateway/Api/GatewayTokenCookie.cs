using System;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The ONE place the Gateway writes the browser session-credential cookie (<c>cc-gateway-token</c>). Every
/// surface that hands a browser its per-device key sets the cookie THROUGH here - the mobile/Cockpit device
/// enrollment (<see cref="MobileEnrollmentEndpoint"/>) and both hosted human-account sign-in entry points (the
/// account sign-in callback and the hosted <c>/m/enroll</c> branch) - so the credential is set exactly one
/// way: <c>HttpOnly</c>, <c>SameSite=Lax</c>, a thirty-day lifetime, and marked essential.
///
/// A credential set two ways is a divergence risk in miniature: if one copy forgot <c>HttpOnly</c> or drifted
/// on <c>SameSite</c>, that one surface would be silently weaker than the others while every test still passed.
/// Folding the write into a single helper makes the cookie's security options impossible to set inconsistently.
/// </summary>
internal static class GatewayTokenCookie
{
    /// <summary>
    /// Writes the issued local per-device key as the <c>cc-gateway-token</c> cookie on the response, with the
    /// single agreed set of security options. The key itself is never logged (security rule DT-05); the caller
    /// logs only the outcome.
    /// </summary>
    /// <param name="ctx">The HTTP context whose response the cookie is written to. Required.</param>
    /// <param name="deviceKey">The issued local per-device key. Required and non-empty.</param>
    public static void Set(HttpContext ctx, string deviceKey)
    {
        if (ctx is null) throw new ArgumentNullException(nameof(ctx));
        if (string.IsNullOrEmpty(deviceKey)) throw new ArgumentException("A device key is required to set the gateway cookie", nameof(deviceKey));

        ctx.Response.Cookies.Append(Util.AuthMiddleware.CookieName, deviceKey, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddDays(30),
            IsEssential = true,
        });
    }
}
