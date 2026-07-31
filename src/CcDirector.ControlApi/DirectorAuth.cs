using CcDirector.Core.Security;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Http;

namespace CcDirector.ControlApi;

/// <summary>
/// Authentication and authorization for the Director's Control API.
///
/// Token source: %LOCALAPPDATA%\cc-director\config\director\gateway-token.txt - the same file the
/// Gateway uses, so there is ONE secret per machine. When this Director is attached to a Gateway,
/// that shared fleet token is the accepted secret instead (see <see cref="ResolveAcceptedToken"/>).
///
/// A caller presents <c>Authorization: Bearer &lt;token&gt;</c>, and the token is one of:
///   - the machine secret itself - full authority, because it IS the root;
///   - a scoped token derived from it (see <see cref="DirectorScopedToken"/>) - either full
///     authority under the admin/cli scopes, or a session-child credential bound to one session id.
///
/// Everything except <c>/healthz</c> requires a credential. <c>/healthz</c> is public because the
/// launcher polls it to decide whether this Director is alive, and its unauthenticated answer says
/// nothing but that.
///
/// There is no cookie and no login page. The cookie branch that used to live here was dead - nothing
/// in the product ever issued the cookie, and no HTML is served from this surface at all - so it was
/// a credential channel that only an attacker had a use for, and the /login redirect it fed pointed
/// at a route that has never existed.
/// </summary>
public static class DirectorAuth
{
    /// <summary>
    /// Where the middleware records what the caller turned out to be, so a handler can read the
    /// caller's authority without re-verifying the token. Nothing downstream reads this today; it is
    /// the seam a per-route rule would use rather than parsing the header a second time.
    /// </summary>
    public const string PrincipalItemKey = "cc-director-principal";

    /// <inheritdoc cref="DirectorMachineSecret.TokenFile"/>
    public static string TokenFile => DirectorMachineSecret.TokenFile;

    /// <summary>
    /// The only route reachable without a credential. Kept to exactly one entry: /login and /logout
    /// were listed here for a browser flow that does not exist, and /favicon.ico for HTML that is
    /// never served - three public routes guarding nothing, on a surface where public is the whole
    /// question.
    /// </summary>
    private static readonly HashSet<string> PublicPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/healthz",
    };

    /// <summary>
    /// The token the Control API accepts (issue #457). When this Director is attached to a
    /// Gateway, that SHARED fleet token (<c>gateway.token</c> in config.json) is the accepted
    /// secret, so the Gateway - which presents the same fleet token on every proxied call - can
    /// authenticate to this Director across machines (LAN mode). Standalone (no gateway token)
    /// falls back to this machine's own persisted token. Pure - unit-tested.
    /// </summary>
    public static string ResolveAcceptedToken(string? fleetToken) => DirectorMachineSecret.Resolve(fleetToken);

    /// <summary>Read the token from disk; generate and persist one if the file does not exist.</summary>
    public static string LoadOrCreateToken() => DirectorMachineSecret.LoadOrCreate();

    /// <summary>
    /// Middleware entry point. Verifies the presented credential, then - for a session-child
    /// credential - checks that this particular route and session id are within its grant.
    ///
    /// No credential is 401 (the caller may retry with one). A valid credential that is not allowed
    /// to do this is 403, and no retry will help; the two are deliberately distinguishable, because
    /// a client that cannot tell them apart cannot report a useful failure.
    /// </summary>
    public static Task Run(HttpContext ctx, string rootSecret, RequestDelegate next)
        => Run(ctx, rootSecret, previousRootSecret: null, next);

    /// <inheritdoc cref="Run(HttpContext, string, RequestDelegate)"/>
    /// <param name="previousRootSecret">The secret in force before the most recent runtime rotation,
    /// if there was one. Only a SESSION-CHILD credential is honoured against it - see
    /// <see cref="VerifyPresented(HttpContext, string, string?)"/>.</param>
    public static async Task Run(HttpContext ctx, string rootSecret, string? previousRootSecret, RequestDelegate next)
    {
        var path = ctx.Request.Path.Value ?? "";

        var principal = VerifyPresented(ctx, rootSecret, previousRootSecret);

        if (PublicPaths.Contains(path))
        {
            // /healthz is reachable without a credential, but a caller that presented a valid one is
            // still recognised - that is how the launcher's update check reads the version and the
            // session count off a route whose PUBLIC answer says only that the Director is alive.
            if (principal.IsValid)
                ctx.Items[PrincipalItemKey] = principal;
            await next(ctx);
            return;
        }

        if (!principal.IsValid)
        {
            FileLog.Write($"[DirectorAuth] 401 {ctx.Request.Method} {path}: no valid credential presented");
            await WriteRefusal(ctx, StatusCodes.Status401Unauthorized, "missing or invalid token");
            return;
        }

        if (principal.Scope == ControlApiScope.SessionChild)
        {
            var verdict = ControlApiGuard.CheckSessionChild(
                ctx.Request.Method, path, key => ctx.Request.Query[key].ToString(), principal.SessionId!.Value);
            if (!verdict.Allowed)
            {
                FileLog.Write($"[DirectorAuth] 403 {ctx.Request.Method} {path}: {verdict.Reason}");
                await WriteRefusal(ctx, StatusCodes.Status403Forbidden, verdict.Reason);
                return;
            }
        }

        ctx.Items[PrincipalItemKey] = principal;
        await next(ctx);
    }

    /// <summary>
    /// What the caller presented, whatever the route. Returns
    /// <see cref="ControlApiPrincipal.Invalid"/> when there is no Bearer header or it does not
    /// verify.
    ///
    /// <paramref name="previousRootSecret"/> is the one-deep grace window for runtime rotation: an
    /// ALREADY-RUNNING session's environment cannot be changed, so the session-child token stamped
    /// into it at launch is forever derived from the secret in force at launch time. When enroll,
    /// rotate, or disconnect replaces the accepted secret, that live session's hooks would otherwise
    /// 401 silently (they swallow HTTP errors) and the session's transcript pointer and preamble
    /// injection would quietly die. So a credential that fails against the current secret is retried
    /// against the previous one, and honoured ONLY if it turns out to be a session-child grant -
    /// least privilege, bound to one session. Full authority (admin, cli, and the raw secret itself)
    /// never rides the grace: whoever holds the current root can mint fresh credentials, and a
    /// rotation must actually revoke the old full-authority ones.
    /// </summary>
    public static ControlApiPrincipal VerifyPresented(HttpContext ctx, string rootSecret, string? previousRootSecret = null)
    {
        if (!ctx.Request.Headers.TryGetValue("Authorization", out var header))
            return ControlApiPrincipal.Invalid;

        var raw = header.ToString();
        if (!raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return ControlApiPrincipal.Invalid;

        var presented = raw["Bearer ".Length..].Trim();
        var principal = DirectorScopedToken.Verify(presented, rootSecret);
        if (principal.IsValid || string.IsNullOrEmpty(previousRootSecret))
            return principal;

        var underPrevious = DirectorScopedToken.Verify(presented, previousRootSecret);
        return underPrevious is { IsValid: true, Scope: ControlApiScope.SessionChild }
            ? underPrevious
            : ControlApiPrincipal.Invalid;
    }

    /// <summary>The authority recorded for this request, or null when the route is public and the
    /// caller presented nothing.</summary>
    public static ControlApiPrincipal? PrincipalOf(HttpContext ctx)
        => ctx.Items.TryGetValue(PrincipalItemKey, out var value) && value is ControlApiPrincipal p ? p : null;

    internal static async Task WriteRefusal(HttpContext ctx, int statusCode, string reason)
    {
        ctx.Response.StatusCode = statusCode;
        await ctx.Response.WriteAsJsonAsync(new { error = reason });
    }
}
