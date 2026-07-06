using CcDirector.Gateway.Api;
using CcDirector.Gateway.Cockpit;
using CcDirector.Gateway.Pairing;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Util;

/// <summary>
/// Bearer-or-cookie auth for the Gateway.
///
/// Public, no auth:    /healthz, /login, /logout, /favicon.ico, /devices/register, the credential-free
///                     cloud sign-in start front door /account/sign-in-start (issue #1076, GET + POST -
///                     it reads/returns no credential and no account data), the mobile app
///                     shell /m + everything under /m/ (which includes the mobile enroll path
///                     POST /m/enroll - it carries its own account-scoped authorization), and the
///                     JSON /cockpit endpoint (a program GET, NOT a browser navigation - see below).
/// Authenticated:      every other route (Bearer header OR cc-gateway-token cookie OR, per
///                     issue #469, a per-device key issued at enrollment).
///
/// This public set is deliberately the ONLY way in without a credential once the host-wide gate is on
/// by default (issue #917): the enroll/pairing entry points carry their own authorization and the login
/// surface must be reachable to obtain one, while every data endpoint stays credential-gated.
///
/// Issue #920 - the /cockpit split: /cockpit is a dual-use path. The JSON API form (a program GET with
/// no "Accept: text/html", e.g. the desktop app's Open Cockpit / Learn buttons resolving the front-door
/// URL) stays public so a same-machine caller with no credential still works. But a BROWSER navigation
/// to /cockpit ("Accept: text/html") is the Blazor SHELL, whose /_blazor circuit and /_framework assets
/// are credential-gated: serving that shell to an unauthenticated browser produced a dead Cockpit whose
/// circuit 401s. So a browser navigation to /cockpit is NOT public - it falls through to the gate and,
/// having no cookie, is redirected to /login first; after sign-in the shell loads WITH the
/// cc-gateway-token cookie and its assets authenticate (200). The gate on /_blazor and session data is
/// unchanged (never weakened).
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
        // Issue #1076 (epic #1069): the credential-free cloud sign-in START front door. A signed-out
        // browser must reach this to BEGIN cloud sign-in, so it cannot sit behind the raw-token wall
        // (that is the deadlock the epic breaks). It is exact-match, so ONLY /account/sign-in-start is
        // public; every other /account/* DATA endpoint (status, logout, devices, credits) and the
        // authenticated POST /account/sign-in stay gated. The entry point reads/echoes no credential and
        // returns no account data (AccountSignInStartEndpoint).
        AccountSignInStartEndpoint.Path,
        // Issue #1080 (epic #1069): the reachable front-door sign-in CALLBACK the cloud sign-in page
        // redirects the user's OWN browser back to, so a person on ANOTHER machine completes sign-in in
        // their own browser instead of on the host loopback. The browser completing sign-in has no Gateway
        // credential yet (it carries the cloud-issued token it hands back, not a Gateway token), so - like
        // the START front door and device enrollment - it must be reachable without a Gateway token. Still
        // exact-match: every other /account/* DATA endpoint and the authenticated POST /account/sign-in
        // stay gated (AccountSignInCallbackEndpoint).
        AccountSignInCallbackEndpoint.Path,
    };

    public static async Task Run(HttpContext ctx, RequireToken cfg, Func<Task> next)
    {
        var path = ctx.Request.Path.Value ?? "";

        // Issue #920: a BROWSER navigation to /cockpit (the Cockpit shell) is never public - only the
        // JSON /cockpit API form is. The shell's own data endpoints are gated, so an unauthenticated
        // browser must be driven to /login (and get the cookie) BEFORE it loads the shell, rather than
        // being handed a shell whose API calls 401. IsBrowserPageRequest is the one definition of
        // "person navigating to a dual-use page" (GET + Accept: text/html), reused here so the
        // classification cannot drift from the CockpitReactApp that serves these navigations.
        var isCockpitBrowserShell =
            string.Equals(path, "/cockpit", StringComparison.OrdinalIgnoreCase)
            && CockpitReactApp.IsBrowserPageRequest(ctx.Request.Method, ctx.Request.Path, ctx.Request.Headers.Accept);

        if (!isCockpitBrowserShell && PublicPaths.Contains(path)) { await next(); return; }

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
    ///
    /// Per issue #469/#908, this ALSO accepts a Bearer or cookie that matches an active per-device
    /// key in the <paramref name="devices"/> registry, so an enrolled device (phone or Director)
    /// authenticates with its own unique key rather than the shared token. Every caller must pass
    /// the registry: issue #1045 deleted the device-key-blind two-argument overload because a route
    /// that omitted the registry silently 401'd every per-device key (it bit voice-turn). Pass
    /// <c>null</c> for <paramref name="devices"/> ONLY when per-device-key auth is genuinely
    /// inapplicable (there is no registry on this host).
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
