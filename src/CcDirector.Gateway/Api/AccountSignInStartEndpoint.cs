using CcDirector.Core.Utilities;
using CcDirector.Gateway.Account;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The credential-free sign-in START front door (epic #1069, issue #1076). This is the one public entry
/// point a SIGNED-OUT browser can reach to BEGIN cloud sign-in, breaking the outer ring of the epic's
/// deadlock: cloud sign-in used to sit BEHIND the raw Gateway token wall (<c>login.html</c>), so a person
/// with no Gateway token was bounced to a screen asking them to paste <c>gateway-token.txt</c> - the very
/// thing cloud sign-in is meant to replace. This endpoint is deliberately on the public-paths allow-list
/// (<see cref="Util.AuthMiddleware"/>) so a request carrying no <c>cc-gateway-token</c> cookie and no
/// Bearer is served rather than answered 401 / redirected to <c>/login</c>.
///
/// It is SAFE to expose because it reads, echoes, and returns NO credential and NO account data:
/// <list type="bullet">
/// <item><c>GET /account/sign-in-start</c> - a browser navigation target (Accept: text/html). Returns a
///   self-contained HTML front door with a single "Sign in with DevThrottle" action. Side-effect-free: it
///   never opens a browser, never reads a credential, and its response is identical whether or not the
///   caller supplies any token, so a stray credential cannot influence it.</item>
/// <item><c>POST /account/sign-in-start</c> - the START action the front door submits. It BRANCHES on the
///   caller (issue #1080). A REMOTE caller is answered <c>302</c> to devthrottle.com carrying a
///   <c>redirect_uri</c> back to this Gateway's own routable <c>/account/sign-in-callback</c>, so the
///   person completes sign-in in their OWN browser. Only a LOOPBACK caller falls to the host-local
///   browser sign-in (<see cref="GatewaySignInService"/>, issue #637), where a browser on this host's
///   desktop is actually reachable. The captured token never leaves the Gateway.</item>
/// </list>
///
/// This is the ONLY way to start a Gateway sign-in. The authenticated <c>POST /account/sign-in</c> that
/// used to sit beside it was removed with the Gateway's user interface: it ran the loopback flow
/// unconditionally, so the Cockpit's Sign in button hung forever on any machine but this one. Every
/// <c>/account</c> DATA endpoint (<c>/account/status</c>, <c>/account/logout</c>, <c>/account/devices</c>,
/// <c>/account/credits</c>) stays gated exactly as before - the public allow-list is an exact-path set, so
/// adding this path weakens none of them.
///
/// Security (carries DT-05): no access/refresh token is ever read from the request, written to the
/// response, or written to the log on any path. The log records only that the credential-free start was
/// reached and the outcome (started / already signed in / not available).
/// </summary>
internal static class AccountSignInStartEndpoint
{
    /// <summary>The exact public path both the front door (GET) and the start action (POST) live on.</summary>
    public const string Path = "/account/sign-in-start";

    /// <summary>
    /// The credential-free front-door page. Matches the Gateway <c>login.html</c> dark theme so the two
    /// Gateway web surfaces read as one. It carries NO token and NO input field for one; its only action
    /// posts back to this same path to begin sign-in.
    /// </summary>
    private const string FrontDoorHtml = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>DevThrottle - Sign in</title>
<style>
  body {
    margin: 0;
    background: #1e1e1e;
    color: #ddd;
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
    min-height: 100vh;
    display: flex;
    align-items: center;
    justify-content: center;
  }
  .panel {
    width: 100%;
    max-width: 420px;
    background: #252526;
    border: 1px solid #3c3c3c;
    border-radius: 6px;
    padding: 24px;
    margin: 16px;
    text-align: center;
  }
  h1 { margin: 0 0 4px; font-size: 18px; letter-spacing: 0.5px; }
  p { color: #888; font-size: 13px; margin: 0 0 16px; line-height: 1.4; }
  button {
    margin-top: 8px;
    background: #0e639c;
    color: white;
    border: 0;
    padding: 10px 18px;
    border-radius: 4px;
    font-weight: 600;
    cursor: pointer;
    font-size: 14px;
    width: 100%;
  }
  button:hover { background: #1177bb; }
</style>
</head>
<body>
  <form class="panel" method="post" action="/account/sign-in-start">
    <h1>DevThrottle</h1>
    <p>Sign in with your DevThrottle account to connect this Gateway. No access key needed.</p>
    <button type="submit">Sign in with DevThrottle</button>
  </form>
</body>
</html>
""";

    /// <summary>The status page shown after the start action, with the one line of status text injected.</summary>
    private const string StatusHtmlTemplate = """
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>DevThrottle - Sign in</title>
<style>
  body {
    margin: 0;
    background: #1e1e1e;
    color: #ddd;
    font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", Roboto, Helvetica, Arial, sans-serif;
    min-height: 100vh;
    display: flex;
    align-items: center;
    justify-content: center;
  }
  .panel {
    width: 100%;
    max-width: 420px;
    background: #252526;
    border: 1px solid #3c3c3c;
    border-radius: 6px;
    padding: 24px;
    margin: 16px;
    text-align: center;
  }
  h1 { margin: 0 0 4px; font-size: 18px; letter-spacing: 0.5px; }
  p { color: #aaa; font-size: 13px; margin: 0; line-height: 1.4; }
</style>
</head>
<body>
  <div class="panel">
    <h1>DevThrottle</h1>
    <p>__STATUS__</p>
  </div>
</body>
</html>
""";

    /// <summary>
    /// Maps the public sign-in START front door and its start action. On a host with no sign-in flow
    /// (<paramref name="signIn"/> null - a non-Windows host with no credential service) the front door
    /// still renders and the start action reports an explicit, user-safe "not available" result rather
    /// than pretending to start a sign-in.
    /// </summary>
    /// <param name="app">The route builder.</param>
    /// <param name="signIn">The Gateway-hosted DevThrottle sign-in flow (issue #637), or null on a host with no credential service.</param>
    public static void Map(IEndpointRouteBuilder app, GatewaySignInService? signIn)
    {
        // GET: the credential-free front door. Side-effect-free - it never reads a credential and its
        // response does not depend on any token the caller may have supplied, so a stray/expired token
        // cannot influence what is served (security rule DT-05).
        app.MapGet(Path, () =>
        {
            FileLog.Write("[AccountSignInStartEndpoint] GET /account/sign-in-start: sign-in start front door reached (no credential required)");
            return Results.Content(FrontDoorHtml, "text/html; charset=utf-8");
        });

        // POST: begin the sign-in. The callback target is chosen by whether the request is same-machine or
        // remote (epic #1069, issue #1080), so a person reaching the front door from ANOTHER machine over
        // Tailscale can complete sign-in in their OWN browser instead of on the host loopback:
        //   - REMOTE  (a routable requester): redirect THIS browser to the cloud sign-in page carrying the
        //     Gateway's reachable front-door callback as the redirect_uri. No browser opens on the host.
        //   - SAME-MACHINE (a loopback requester, e.g. a person at the Gateway PC): keep the existing
        //     host-local browser loopback flow (issue #637), detached exactly like the authenticated POST
        //     /account/sign-in (#853), where the loopback still earns its place.
        // The single-flight guard in the service makes a duplicate same-machine start a harmless no-op.
        app.MapPost(Path, (HttpContext ctx) =>
        {
            // No sign-in flow on this host: report it explicitly instead of fabricating a started state.
            if (signIn is null)
            {
                FileLog.Write("[AccountSignInStartEndpoint] POST /account/sign-in-start: no sign-in flow on this host -> not available");
                return StatusResult("Sign-in is not available on this Gateway host.");
            }

            // Already signed in: nothing to start.
            if (signIn.IsSignedIn())
            {
                FileLog.Write("[AccountSignInStartEndpoint] POST /account/sign-in-start: already signed in -> no browser hand-off started");
                return StatusResult("This Gateway is already signed in to DevThrottle.");
            }

            // Remote requester: complete in the user's own browser via a redirect through the front door.
            // The remote address already reflects X-Forwarded-For (the Gateway runs UseForwardedHeaders), so
            // a tailnet client behind the Tailscale Serve front door presents its routable address here.
            if (RemoteSignInRouting.IsRemoteRequest(ctx.Connection.RemoteIpAddress))
            {
                // The reachable front-door base is the scheme+host the browser actually used to reach the
                // Gateway (forwarded-header aware), so the redirect_uri is routable back here - never a
                // loopback URL. When the request carries no host the Gateway has no reachable
                // address to complete a remote sign-in, so we surface that clearly rather than fall back to
                // the loopback the remote browser cannot reach (no-fallback rule).
                var host = ctx.Request.Host.Value;
                if (string.IsNullOrWhiteSpace(host))
                {
                    FileLog.Write("[AccountSignInStartEndpoint] POST /account/sign-in-start: remote request carries no host -> no reachable front-door address for remote sign-in");
                    return StatusResult(
                        "This Gateway does not have a reachable address to complete sign-in from another device. Sign in from the Gateway host, or reach it through its Tailscale address.");
                }

                var signInUrl = RemoteSignInRouting.BuildRemoteSignInUrl(ctx.Request.Scheme, host);
                // The sign-in URL carries only the redirect_uri (a callback address), never a credential, so
                // logging it in full is safe and gives the token-free outbound-URL evidence (DT-05 upheld).
                FileLog.Write($"[AccountSignInStartEndpoint] POST /account/sign-in-start: remote sign-in -> redirecting the browser to the cloud sign-in page (front-door callback, no host browser opened): {signInUrl}");
                return Results.Redirect(signInUrl);
            }

            // Same-machine requester: keep the host-local browser loopback flow.
            FileLog.Write("[AccountSignInStartEndpoint] POST /account/sign-in-start: same-machine sign-in -> starting the host-local browser loopback flow in the background");
            _ = Task.Run(async () =>
            {
                var result = await signIn.RunSignInAsync().ConfigureAwait(false);
                FileLog.Write(result.Succeeded
                    ? "[AccountSignInStartEndpoint] background sign-in: signed in"
                    : $"[AccountSignInStartEndpoint] background sign-in: not signed in - {result.FailureReason}");
            });

            return StatusResult("Sign-in started. Finish it in the browser window that opened on the Gateway host, then return here.");
        });
    }

    /// <summary>Builds a 200 text/html status page with the given single line of user-safe status text.</summary>
    private static IResult StatusResult(string status)
    {
        var html = StatusHtmlTemplate.Replace("__STATUS__", System.Web.HttpUtility.HtmlEncode(status));
        return Results.Content(html, "text/html; charset=utf-8");
    }
}
