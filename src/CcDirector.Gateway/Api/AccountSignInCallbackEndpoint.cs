using CcDirector.Core.Utilities;
using CcDirector.Gateway.Account;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The Gateway's reachable front-door sign-in CALLBACK (epic #1069, issue #1080): <c>GET
/// /account/sign-in-callback</c>. This is the routable address the cloud sign-in page redirects the user's
/// OWN browser back to after they sign in - the remote-capable counterpart to the host-local loopback
/// listener (<see cref="Core.Account.LoopbackLoginListener"/>) the first-run flow used. A person reaching the
/// Gateway front door from ANOTHER machine over Tailscale can now finish sign-in in their own browser: the
/// sign-in START (<see cref="AccountSignInStartEndpoint"/>) redirects them to the cloud sign-in page carrying
/// THIS callback as the <c>redirect_uri</c>, and the cloud completion redirects the browser back here with
/// the captured token pair, which the Gateway stores and confirms with a signed-in landing page.
///
/// Public (on the <see cref="Util.AuthMiddleware"/> allow-list): the browser completing sign-in has no
/// Gateway credential yet, so it must be reachable without one - exactly like the sign-in START front door.
/// The hand-back SHAPE (the <c>access_token</c>/<c>refresh_token</c> query) is unchanged from the loopback
/// flow the cloud completion already targets; hardening that shape is the separate follow-up (issue "0c").
///
/// Security (carries DT-05): the captured tokens are stored through the Gateway credential service and are
/// NEVER written to the response or the log on any path. The response is a fixed status page that echoes no
/// token, and the access log redacts this path's query string (see <c>GatewayHost</c>) so the handed-back
/// credential never reaches the gateway log. The token never leaves the Gateway.
/// </summary>
internal static class AccountSignInCallbackEndpoint
{
    /// <summary>The exact public path the cloud sign-in completion redirects the browser back to.</summary>
    public const string Path = RemoteSignInRouting.CallbackPath;

    /// <summary>
    /// The status page shown after the callback, sharing the sign-in front door's dark theme so the Gateway
    /// web surfaces read as one. It carries a single line of user-safe status text and NO token.
    /// </summary>
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
    /// Maps <c>GET /account/sign-in-callback</c>. On a host with no sign-in flow (<paramref name="signIn"/>
    /// null - a non-Windows host with no credential service) the callback reports an explicit, user-safe
    /// "not available" result rather than pretending to have captured a credential.
    /// </summary>
    /// <param name="app">The route builder.</param>
    /// <param name="signIn">The Gateway-hosted DevThrottle sign-in flow (issue #637), or null on a host with no credential service.</param>
    public static void Map(IEndpointRouteBuilder app, GatewaySignInService? signIn)
    {
        app.MapGet(Path, (HttpContext ctx) =>
        {
            // No sign-in flow on this host: there is nothing to store a credential into, so report it
            // explicitly instead of silently discarding the hand-back.
            if (signIn is null)
            {
                FileLog.Write("[AccountSignInCallbackEndpoint] GET /account/sign-in-callback: no sign-in flow on this host -> not available");
                return StatusResult("Sign-in is not available on this Gateway host.");
            }

            // The tokens are read straight from the request but are NEVER logged (DT-05); only the outcome is.
            var accessToken = ctx.Request.Query["access_token"].ToString();
            var refreshToken = ctx.Request.Query["refresh_token"].ToString();

            var result = signIn.CompleteBrowserSignIn(accessToken, refreshToken);
            if (!result.Succeeded)
            {
                FileLog.Write($"[AccountSignInCallbackEndpoint] GET /account/sign-in-callback: sign-in not completed - {result.FailureReason}");
                return StatusResult(result.FailureReason ?? "Sign-in did not complete. Please try again.");
            }

            FileLog.Write("[AccountSignInCallbackEndpoint] GET /account/sign-in-callback: sign-in completed - credential stored on the Gateway");
            return StatusResult("You are signed in to DevThrottle. This Gateway is now connected to your account. You can close this tab.");
        });
    }

    /// <summary>Builds a 200 text/html status page with the given single line of user-safe status text (no token).</summary>
    private static IResult StatusResult(string status)
    {
        var html = StatusHtmlTemplate.Replace("__STATUS__", System.Web.HttpUtility.HtmlEncode(status));
        return Results.Content(html, "text/html; charset=utf-8");
    }
}
