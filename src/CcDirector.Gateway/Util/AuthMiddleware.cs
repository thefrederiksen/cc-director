using CcDirector.Gateway.Api;
using CcDirector.Gateway.Cockpit;
using CcDirector.Gateway.Pairing;
using Microsoft.AspNetCore.Http;

namespace CcDirector.Gateway.Util;

/// <summary>
/// Bearer-or-cookie auth for the Gateway.
///
/// Public, no auth:    /healthz, /login, /logout, /favicon.ico,
///                     /devices/enroll-signed-in (epic #1069: carries its own loopback + signed-in
///                     guards, so a token-less fresh device can earn its first key), the credential-free
///                     cloud sign-in start front door /account/sign-in-start (issue #1076, GET + POST -
///                     it reads/returns no credential and no account data), the mobile app
///                     shell /mobile + everything under /mobile/ (which includes the enroll path
///                     POST /mobile/enroll - it carries its own account-scoped authorization) plus the
///                     legacy /m + /m/ mount it re-based from (still public so the Gateway's 301 to
///                     /mobile and the back-compat POST /m/enroll stay reachable pre-credential), the desktop
///                     Cockpit sign-in surface /signin + /device-callback and the shell's static
///                     assets under /assets/ (issue #1088 - see below), and the JSON /cockpit endpoint
///                     (a program GET, NOT a browser navigation - see below).
/// Authenticated:      every other route (Bearer header OR cc-gateway-token cookie OR, per
///                     issue #469, a per-device key issued at enrollment).
///
/// This public set is deliberately the ONLY way in without a credential once the host-wide gate is on
/// by default (issue #917): the enroll/pairing entry points carry their own authorization and the
/// sign-in surface must be reachable to obtain one, while every data endpoint stays credential-gated.
///
/// Issue #920 - the /cockpit split: /cockpit is a dual-use path. The JSON API form (a program GET with
/// no "Accept: text/html", e.g. the desktop app's Open Cockpit / Learn buttons resolving the front-door
/// URL) stays public so a same-machine caller with no credential still works. But a BROWSER navigation
/// to /cockpit ("Accept: text/html") is the Cockpit SHELL, whose data endpoints are credential-gated:
/// serving it as public would misclassify a person's navigation. It falls through to the gate and,
/// with no credential, is driven to the sign-in flow like every other page navigation.
///
/// Issue #1088 - browser device enrollment is the front door: a signed-out BROWSER navigation
/// (Accept: text/html) is redirected to /signin (the React Cockpit's shared client-core sign-in
/// screen, the exact flow the phone uses), carrying the originally-requested route in next=. The
/// raw-token /login wall is no longer the front door (it remains reachable as break-glass, #1077).
/// For that sign-in screen to render before ANY credential exists, three surfaces are public, exactly
/// as /m always has been for the phone: /signin and /device-callback (extensionless client routes the
/// SPA fallback serves the shell for; the callback receives the cloud device key in the URL FRAGMENT,
/// which never reaches this server) and the Vite-hashed static assets under /assets/ (JavaScript/CSS
/// of the shell - they carry no secret and no data; every data endpoint stays credential-gated).
///
/// Browser requests (Accept: text/html) get a 302 redirect to /signin.
/// Non-browser requests get a 401 with JSON body.
/// </summary>
internal static class AuthMiddleware
{
    public const string CookieName = "cc-gateway-token";

    /// <summary>
    /// HttpContext.Items key under which <see cref="HasValidToken"/> stashes the <c>DeviceType</c> of the
    /// per-device key that authenticated the request ("phone" / "browser" / ...), or leaves absent when the
    /// caller used the shared machine token (no device). The DevThrottle Stats surface resolution reads it
    /// to tag a remote prompt with its surface, from the SAME verified key that authenticated the call.
    /// </summary>
    public const string DeviceTypeItemKey = "cc.stats.DeviceType";

    /// <summary>
    /// HttpContext.Items key under which <see cref="HasValidToken"/> stashes the per-device key that
    /// authenticated the request, or leaves absent when the caller used the shared machine token (no device).
    /// The hosted tenant boundary (Hosted Multi-Tenancy increment 1) reads it to resolve the request's tenant
    /// from the SAME verified key that authenticated the call - the tenant is derived from the authenticated
    /// credential, never from anything the client claims. The key is already present in the request headers,
    /// so stashing it in request-scoped Items exposes nothing new.
    /// </summary>
    public const string DeviceKeyItemKey = "cc.auth.DeviceKey";

    /// <summary>
    /// HttpContext.Items key under which <see cref="HasValidToken"/> stashes THE EXACT CREDENTIAL STRING IT
    /// ACCEPTED, for every accepted credential shape - the shared machine token as well as a per-device key,
    /// arriving as a Bearer header or as any one of possibly several cookies.
    ///
    /// This exists so that anything which needs a per-caller identity - a storage partition, a per-device
    /// context key - uses the credential this gate actually validated, instead of reading the raw request a
    /// SECOND time and reaching its own conclusion. Re-deriving an identity from raw headers is re-doing the
    /// authentication decision in a second place with different rules, and two independent parsers of the
    /// same request eventually disagree: the gate accepts on a valid cookie while the second parser resolves
    /// on an attacker-chosen Bearer, and the gap between them is a partition the caller can choose. Resolve
    /// the identity once, here, and pass it forward.
    ///
    /// Absent means NO credential was authenticated on this request (the host-wide gate is off), which is
    /// not an identity and must never be quietly replaced by re-reading the headers.
    ///
    /// It is deliberately SEPARATE from <see cref="DeviceKeyItemKey"/>, whose absence means "the shared
    /// machine token, no device" and is load-bearing for hosted tenant resolution.
    /// </summary>
    public const string AuthenticatedCredentialItemKey = "cc.auth.Credential";

    /// <summary>
    /// The desktop Cockpit's sign-in route (issue #1088): the shared client-core enrollment screen a
    /// signed-out browser navigation is redirected to. Must match the route in apps/cockpit/src/main.tsx.
    /// </summary>
    public const string CockpitSignInPath = "/signin";

    /// <summary>
    /// The desktop Cockpit's device-enrollment callback route (issue #1088): where devthrottle.com hands
    /// the cloud device key back in the URL fragment. Must match apps/cockpit/src/main.tsx and the
    /// client-core COCKPIT_DEVICE_CALLBACK_PATH.
    /// </summary>
    public const string CockpitDeviceCallbackPath = "/device-callback";

    /// <summary>
    /// The React shells' Vite-hashed static assets prefix (issue #1088). Public so the sign-in screen
    /// can render before any credential exists - static JavaScript/CSS only, no secrets, no data.
    /// </summary>
    public const string CockpitAssetsPrefix = "/assets/";

    private static readonly HashSet<string> PublicPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/healthz",
        "/cockpit",
        "/login",
        "/logout",
        "/favicon.ico",
        // Epic #1069 (fresh-device unblock): the sign-in replacement for the pairing code. A brand-new
        // co-located Director has NO Gateway token yet, so its enroll call arrives token-less and would
        // 401 at this gate BEFORE reaching the endpoint's own guards - the deadlock (the one endpoint that
        // hands a fresh device its FIRST key was unreachable by a device with no key). Opening it does not
        // weaken the trust model: SignedInEnrollmentEndpoint carries its OWN transport-level authorization
        // - a proven-loopback caller (403 for any non-loopback, so a
        // tailnet/LAN attacker gains nothing), the Gateway signed in (409 otherwise), and an idempotent
        // mint (the #1136 leak guard). Loopback = same machine = already inside the trust boundary.
        "/devices/enroll-signed-in",
        // Hosted Multi-Tenancy increment 1: the hosted enrollment front door. A fresh REMOTE Director has no
        // Gateway device key yet, so it cannot pass the token gate; this route carries its OWN authorization -
        // the caller's verified Supabase ACCOUNT token, which the endpoint validates itself (aud/iss/sig/exp)
        // before minting a device key. It is exact-match public, exactly like the loopback enroll route above.
        Api.HostedEnrollmentEndpoint.Path,
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
        // Issue #1088 (epic #1069): the desktop Cockpit's device-enrollment surface - the exact
        // browser analog of the phone's public /m shell. /signin renders the shared client-core Sign
        // in screen (a signed-out browser must reach it to obtain a credential); /device-callback is
        // where devthrottle.com hands the cloud device key back IN THE URL FRAGMENT (the fragment
        // never reaches this server, so the route itself carries no secret). Both are extensionless
        // client-side routes the SPA fallback serves the React shell for; neither returns any data.
        CockpitSignInPath,
        CockpitDeviceCallbackPath,
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

        // Issue #806 / #908: the mobile app shell (/mobile and its built assets) carries no secret. It
        // must load without the global gate so the Sign in screen can render before the phone has any
        // credential; the phone then enrolls (POST /mobile/enroll, itself under /mobile/ and carrying its
        // own authorization - an account-scoped device key) and authenticates its OWN API calls (e.g.
        // /sessions) with the per-device key it receives. The master token is no longer injected into
        // the shell (issue #908), so reaching /mobile grants no access on its own - the data endpoints stay
        // Bearer/cookie-gated on that per-device key.
        //
        // The legacy /m mount stays public too: the app re-based from /m to /mobile (owner ruling
        // 2026-07-20), and the Gateway 301s /m -> /mobile and keeps POST /m/enroll as a back-compat route.
        // Both the redirect and that route must be reachable before the device holds any credential,
        // exactly as /mobile is - so an installed phone PWA still on the old path is not walled out.
        if (string.Equals(path, "/mobile", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/mobile/", StringComparison.OrdinalIgnoreCase)
            || string.Equals(path, "/m", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/m/", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        // Issue #1088: the desktop Cockpit shell's static assets (Vite's content-hashed JavaScript/CSS
        // under /assets/) are public, exactly like the /m assets above - the /signin screen cannot
        // render before any credential exists unless its script and styles load. The files carry no
        // secret and no data; every data endpoint stays credential-gated below.
        if (path.StartsWith(CockpitAssetsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        if (HasValidToken(ctx, cfg.Token, cfg.Devices))
        {
            await next();
            return;
        }

        // Unauthorized. A person's browser navigation (Accept: text/html) is driven to the shared
        // sign-in flow - the Cockpit's /signin enrollment screen - carrying the originally-requested
        // route in next= so the browser lands back on that exact route after enrollment (issue #1088).
        // Never to the raw-token /login wall (that is break-glass only, issue #1077).
        var accept = ctx.Request.Headers["Accept"].ToString();
        if (accept.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.Redirect($"{CockpitSignInPath}?next={Uri.EscapeDataString(ctx.Request.Path + ctx.Request.QueryString)}");
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
    ///
    /// Production-readiness MH-2: on a HOSTED Gateway (<see cref="GatewayHostedMode.IsHosted"/>) the shared
    /// machine token is NOT accepted here. A hosted deployment serves many tenants, and the shared token
    /// authenticates with NO device - so no tenant - which would reach every tenant-blind route with zero
    /// scoping. On hosted the per-device key issued at enrollment is therefore the ONLY accepted credential
    /// (it carries the tenant), so a shared-token Bearer or cookie is rejected. Self-host is untouched: it is
    /// single-owner, the shared token remains its credential, and this overload reads
    /// <see cref="GatewayHostedMode.IsHosted"/> (false off hosted) so behavior there is byte-identical.
    /// </summary>
    public static bool HasValidToken(HttpContext ctx, string token, DeviceRegistry? devices)
        => HasValidToken(ctx, token, devices, GatewayHostedMode.IsHosted);

    /// <summary>
    /// Testable core of <see cref="HasValidToken(HttpContext, string, DeviceRegistry?)"/> with the hosted
    /// policy passed explicitly. When <paramref name="rejectSharedToken"/> is true (hosted), the shared
    /// machine <paramref name="token"/> is not accepted on Bearer or cookie - only an active per-device key
    /// authenticates. When false (self-host), the shared token is accepted exactly as before. The seam lets a
    /// test drive both the hosted-rejection and the self-host-accept paths deterministically, without touching
    /// the process environment.
    /// </summary>
    internal static bool HasValidToken(HttpContext ctx, string token, DeviceRegistry? devices, bool rejectSharedToken)
    {
        // Bearer
        if (ctx.Request.Headers.TryGetValue("Authorization", out var header))
        {
            var raw = header.ToString();
            const string prefix = "Bearer ";
            if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var provided = raw.Substring(prefix.Length).Trim();
                // MH-2: the shared machine token authenticates with no device (no tenant), so it is refused on
                // hosted - the per-device key below is the only accepted credential there.
                if (!rejectSharedToken && string.Equals(provided, token, StringComparison.Ordinal))
                    return Accept(ctx, provided, deviceType: null);
                // Issue #469: a unique per-device key issued at enrollment is equally valid. DevThrottle
                // Stats: record the matched device's type so a remote prompt can be tagged with its surface.
                var deviceType = devices?.DeviceTypeForKey(provided);
                if (deviceType is not null)
                    return Accept(ctx, provided, deviceType);
            }
        }

        // Cookie. A browser WebSocket cannot set an Authorization header, so the live terminal stream
        // authenticates via this cookie. It accepts the shared machine token (self-host only - see MH-2 above)
        // OR, per issue #908, an active per-device key - so a phone that enrolled with its own device key (and
        // mirrors that key into the cookie) can open the stream, exactly as it can call the Bearer-authenticated
        // endpoints.
        //
        // Issue #1088: tolerate duplicate cc-gateway-token cookies. A browser can temporarily send both
        // a stale HttpOnly raw-token cookie from /login and a fresh device-key cookie from enrollment.
        // Request.Cookies exposes one value, which may be the stale one; scan the raw Cookie header and
        // accept if ANY cc-gateway-token value is valid.
        foreach (var cookieValue in CookieValues(ctx, CookieName))
        {
            // MH-2: same as the Bearer branch - the shared machine token cookie is not accepted on hosted.
            if (!rejectSharedToken && string.Equals(cookieValue, token, StringComparison.Ordinal))
                return Accept(ctx, cookieValue, deviceType: null);
            // DevThrottle Stats: same device-key surface record as the Bearer branch (the live terminal
            // stream authenticates via this cookie).
            var cookieDeviceType = devices?.DeviceTypeForKey(cookieValue);
            if (cookieDeviceType is not null)
                return Accept(ctx, cookieValue, cookieDeviceType);
        }

        return false;
    }

    /// <summary>
    /// THE ONE PLACE a request is accepted. Every accepted credential shape - the shared machine token or a
    /// per-device key, arriving as a Bearer or as any one of several cookies - returns through here, so the
    /// authenticated identity is recorded on the request exactly once, in one line, for all of them. A shape
    /// that returned true on its own would be an authenticated caller with no identity to pass forward, and
    /// whatever needed one would go and read the raw request again - which is the second authentication
    /// decision this whole mechanism exists to prevent.
    /// </summary>
    /// <param name="credential">The exact credential string that was validated.</param>
    /// <param name="deviceType">The matched device's type, or null for the shared machine token (no device).
    ///  Null deliberately leaves <see cref="DeviceKeyItemKey"/> absent, which hosted tenant resolution reads
    ///  as "shared token, no device".</param>
    private static bool Accept(HttpContext ctx, string credential, string? deviceType)
    {
        ctx.Items[AuthenticatedCredentialItemKey] = credential;
        if (deviceType is not null)
        {
            ctx.Items[DeviceTypeItemKey] = deviceType;
            ctx.Items[DeviceKeyItemKey] = credential;
        }
        return true;
    }

    private static IEnumerable<string> CookieValues(HttpContext ctx, string name)
    {
        var sawRawCookieHeader = false;
        foreach (var header in ctx.Request.Headers.Cookie)
        {
            if (header is null) continue;
            sawRawCookieHeader = true;
            foreach (var part in header.Split(';'))
            {
                var trimmed = part.Trim();
                if (trimmed.Length == 0) continue;

                var equals = trimmed.IndexOf('=');
                var cookieName = equals < 0 ? trimmed : trimmed[..equals].Trim();
                if (!string.Equals(cookieName, name, StringComparison.Ordinal))
                    continue;

                var value = equals < 0 ? "" : trimmed[(equals + 1)..].Trim();
                if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                    value = value[1..^1];
                yield return UnescapeCookieValue(value);
            }
        }

        // Normal ASP.NET cookie parsing path for callers/tests that populate Request.Cookies directly.
        if (!sawRawCookieHeader && ctx.Request.Cookies.TryGetValue(name, out var parsed))
            yield return parsed;
    }

    private static string UnescapeCookieValue(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch
        {
            return value;
        }
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
