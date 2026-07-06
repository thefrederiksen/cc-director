using System.Net;
using CcDirector.Core.Account;

namespace CcDirector.Gateway.Account;

/// <summary>
/// Chooses WHERE a browser-navigation sign-in completes and computes the reachable front-door callback
/// (epic #1069, issue #1080). The credential-free sign-in front door (issue #1076) used to complete every
/// sign-in host-locally - it opened a browser ON THE GATEWAY HOST and waited on a loopback-only
/// listener. That is correct only when the person is sitting at the Gateway PC. When the owner
/// reaches the Gateway front door from ANOTHER machine over Tailscale, opening a browser on the host and
/// waiting on a loopback the remote browser can never reach cannot complete. This helper carries the two
/// pure decisions that make the front door remote-capable, kept out of the endpoint so they are unit-testable
/// on their own:
/// <list type="bullet">
/// <item><see cref="IsRemoteRequest"/> - is the requesting browser on a DIFFERENT machine (a routable source
///   address) or on the Gateway host itself (loopback)? The Gateway runs <c>UseForwardedHeaders</c> ahead of
///   the pipeline, so the connection's remote address already reflects the tailnet client address behind a
///   Tailscale Serve front door, and a same-machine browser shows loopback.</item>
/// <item><see cref="BuildRemoteSignInUrl"/> - the cloud sign-in URL whose <c>redirect_uri</c> is the
///   Gateway's own REACHABLE front-door callback (the scheme+host the browser actually used to reach the
///   Gateway), NOT a loopback URL, so the cloud sign-in page can redirect the user's own
///   browser back to the Gateway to hand the credential over.</item>
/// </list>
/// The front-door callback base is taken from the very request the browser made, so it is reachable by
/// construction (the browser is talking to that address right now); there is no separate configured base to
/// fall out of sync (no-fallback rule). The token hand-back SHAPE is unchanged from the loopback flow - the
/// same <c>access_token</c>/<c>refresh_token</c> query the cloud completion already uses; hardening that
/// shape is the separate follow-up (epic #1069, issue "0c").
/// </summary>
public static class RemoteSignInRouting
{
    /// <summary>
    /// The Gateway's reachable front-door callback path the cloud sign-in page redirects the user's own
    /// browser back to. Public (no Gateway token) because a signed-out browser completing sign-in has no
    /// Gateway credential yet - it is carried on the <see cref="Util.AuthMiddleware"/> allow-list.
    /// </summary>
    public const string CallbackPath = "/account/sign-in-callback";

    /// <summary>
    /// Decides whether the request came from a DIFFERENT machine (remote) rather than the Gateway host
    /// itself. Remote when the connection's remote address is present and is NOT a loopback address; a
    /// same-machine browser (or the tray auto-prompt on the host) presents loopback. When the address is
    /// unknown (null), the request is treated as same-machine so the conservative host-local path is used
    /// rather than assuming a remote we cannot address.
    /// </summary>
    /// <param name="remoteIp">The requesting connection's remote address, or null when unknown.</param>
    public static bool IsRemoteRequest(IPAddress? remoteIp)
        => remoteIp is not null && !IPAddress.IsLoopback(remoteIp);

    /// <summary>
    /// Builds the Gateway's reachable front-door callback URL - <paramref name="scheme"/> and
    /// <paramref name="host"/> are the exact scheme and host the requesting browser used to reach the
    /// Gateway (forwarded-header aware), so the URL is routable back to this Gateway from wherever that
    /// browser is. The path is <see cref="CallbackPath"/>.
    /// </summary>
    /// <param name="scheme">The request scheme (http/https). Required.</param>
    /// <param name="host">The request host (name and optional port). Required and non-empty.</param>
    /// <exception cref="ArgumentException">The scheme is missing.</exception>
    /// <exception cref="InvalidOperationException">
    /// The request carries no host, so the Gateway has no reachable front-door address to complete a remote
    /// sign-in. Surfaced (not silently degraded to loopback) per the no-fallback rule.
    /// </exception>
    public static Uri BuildFrontDoorCallback(string scheme, string host)
    {
        if (string.IsNullOrWhiteSpace(scheme))
            throw new ArgumentException("Request scheme is required to build the front-door callback", nameof(scheme));
        if (string.IsNullOrWhiteSpace(host))
            throw new InvalidOperationException(
                "The request carries no host, so this Gateway has no reachable front-door address to complete a remote sign-in.");

        return new Uri($"{scheme}://{host}{CallbackPath}");
    }

    /// <summary>
    /// Builds the cloud sign-in URL a remote browser is redirected to: the configured DevThrottle sign-in
    /// address carrying the Gateway's reachable front-door callback as its <c>redirect_uri</c>, so the cloud
    /// completion redirects the user's own browser back to this Gateway. Reuses the exact same
    /// <see cref="FirstRunLoginCoordinator.BuildSignInUrl"/> the host-local flow uses, only with a routable
    /// callback in place of the loopback one - so the cloud contract is identical.
    /// </summary>
    /// <param name="scheme">The request scheme the browser used. Required.</param>
    /// <param name="host">The request host the browser used. Required and non-empty.</param>
    public static string BuildRemoteSignInUrl(string scheme, string host)
        => FirstRunLoginCoordinator.BuildSignInUrl(BuildFrontDoorCallback(scheme, host));
}
