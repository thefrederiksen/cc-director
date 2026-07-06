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
/// <item><c>POST /account/sign-in-start</c> - the START action the front door submits. It kicks the
///   EXISTING Gateway browser loopback sign-in (<see cref="GatewaySignInService"/>, issue #637) off as a
///   detached background task, exactly like the authenticated <see cref="AccountSignInEndpoint"/> (#853),
///   and returns a small status page. The captured token never leaves the Gateway.</item>
/// </list>
///
/// Scope boundary (issue #1076): this endpoint makes the sign-in start REACHABLE without a credential. The
/// remote-vs-loopback redirect mechanics of the flow itself are a separate follow-up (epic #1069, issue
/// "0b"); here the start reuses the existing host-local loopback mechanism unchanged. Every <c>/account</c>
/// DATA endpoint (<c>/account/status</c>, <c>/account/logout</c>, <c>/account/devices</c>,
/// <c>/account/credits</c>) and the authenticated <c>POST /account/sign-in</c> stay gated exactly as before
/// - the public allow-list is an exact-path set, so adding this path weakens none of them.
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

        // POST: begin the sign-in. Reuses the EXISTING host-local browser loopback flow (issue #637),
        // detached exactly like the authenticated POST /account/sign-in (#853) so the request never blocks
        // on the person finishing in the browser. The single-flight guard in the service makes a duplicate
        // start a harmless no-op.
        app.MapPost(Path, () =>
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

            FileLog.Write("[AccountSignInStartEndpoint] POST /account/sign-in-start: starting the browser loopback sign-in in the background (no credential required)");
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
