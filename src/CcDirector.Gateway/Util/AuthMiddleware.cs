using CcDirector.Gateway.Pairing;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Util;

/// <summary>
/// Bearer-or-cookie auth for the Gateway.
///
/// Public, no auth:    /healthz, /cockpit, /login, /logout, /favicon.ico, /devices/register, and the
///                     mobile app shell /m + everything under /m/ (which includes the mobile enroll
///                     path POST /m/enroll - it carries its own account-scoped authorization).
/// Authenticated:      every other route (Bearer header OR cc-gateway-token cookie OR, per
///                     issue #469, a per-device key issued at enrollment).
///
/// This public set is deliberately the ONLY way in without a credential once the host-wide gate is on
/// by default (issue #917): the enroll/pairing entry points carry their own authorization and the login
/// surface must be reachable to obtain one, while every data endpoint stays credential-gated.
///
/// Browser requests (Accept: text/html) get a 302 redirect to /login.
/// Non-browser requests get a 401 with JSON body.
/// </summary>
internal static class AuthMiddleware
{
    public const string CookieName = "cc-gateway-token";

    private static readonly HashSet<string> PublicPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/healthz",
        "/cockpit",
        "/login",
        "/logout",
        "/favicon.ico",
        // Issue #469: enrollment carries its own authorization (the pairing code), so a brand-new
        // device with no credential yet can reach it. The endpoint itself rejects a wrong/expired/
        // used code, so opening the route does not weaken the trust model.
        "/devices/register",
    };

    public static async Task Run(HttpContext ctx, RequireToken cfg, Func<Task> next)
    {
        var path = ctx.Request.Path.Value ?? "";

        if (PublicPaths.Contains(path)) { await next(); return; }

        // Issue #806 / #908: the mobile app shell (/m and its built assets) carries no secret. It must
        // load without the global gate so the Sign in screen can render before the phone has any
        // credential; the phone then enrolls (POST /m/enroll, itself under /m/ and carrying its own
        // authorization - an account-scoped device key) and authenticates its OWN API calls (e.g.
        // /sessions) with the per-device key it receives. The master token is no longer injected into
        // the shell (issue #908), so reaching /m grants no access on its own - the data endpoints stay
        // Bearer/cookie-gated on that per-device key.
        if (string.Equals(path, "/m", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/m/", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        if (HasValidToken(ctx, cfg.Token, cfg.Devices))
        {
            await next();
            return;
        }

        // Unauthorized
        var accept = ctx.Request.Headers["Accept"].ToString();
        if (accept.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.Redirect($"/login?next={Uri.EscapeDataString(ctx.Request.Path + ctx.Request.QueryString)}");
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await ctx.Response.WriteAsync("{\"error\":\"missing or invalid token\"}");
    }

    /// <summary>
    /// The Gateway's one token check (Bearer header OR the <see cref="CookieName"/> cookie,
    /// ordinal compare against the per-machine gateway token). Used by the global middleware
    /// above and by endpoints that must stay token-gated even when the global middleware is
    /// off (issue #369: the voice-turn submit/poll surface in production mode).
    /// </summary>
    public static bool HasValidToken(HttpContext ctx, string token) => HasValidToken(ctx, token, null);

    /// <summary>
    /// As <see cref="HasValidToken(HttpContext, string)"/> but, per issue #469, ALSO accepts a
    /// Bearer that matches an active per-device key in the <paramref name="devices"/> registry, so
    /// an enrolled Director authenticates with its own unique key rather than the shared token.
    /// </summary>
    public static bool HasValidToken(HttpContext ctx, string token, DeviceRegistry? devices)
    {
        // Bearer
        if (ctx.Request.Headers.TryGetValue("Authorization", out var header))
        {
            var raw = header.ToString();
            const string prefix = "Bearer ";
            if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var provided = raw.Substring(prefix.Length).Trim();
                if (string.Equals(provided, token, StringComparison.Ordinal))
                    return true;
                // Issue #469: a unique per-device key issued at enrollment is equally valid.
                if (devices is not null && devices.IsValidDeviceKey(provided))
                    return true;
            }
        }

        // Cookie. A browser WebSocket cannot set an Authorization header, so the live terminal stream
        // authenticates via this cookie. It accepts the shared machine token OR, per issue #908, an
        // active per-device key - so a phone that enrolled with its own device key (and mirrors that key
        // into the cookie) can open the stream, exactly as it can call the Bearer-authenticated endpoints.
        if (ctx.Request.Cookies.TryGetValue(CookieName, out var cookieValue))
        {
            if (string.Equals(cookieValue, token, StringComparison.Ordinal))
                return true;
            if (devices is not null && devices.IsValidDeviceKey(cookieValue))
                return true;
        }

        return false;
    }

    public sealed class RequireToken
    {
        public string Token { get; init; } = "";

        /// <summary>
        /// Issue #469: the per-device-key registry, so an enrolled Director's own key is accepted
        /// as a valid Bearer alongside the shared machine token. Null disables per-device-key auth.
        /// </summary>
        public DeviceRegistry? Devices { get; init; }
    }
}
