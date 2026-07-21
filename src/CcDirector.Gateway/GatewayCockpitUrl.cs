using CcDirector.Core.Network;

namespace CcDirector.Gateway;

/// <summary>
/// Resolves the ONE public base URL the Cockpit is reached at, which the Gateway hands back from
/// <c>GET /cockpit</c>, the <c>CockpitUrl</c> on <c>GET /gateway/about</c>, and (through <c>/cockpit</c>)
/// the desktop Learn button. The client is dumb: it opens whatever URL it is handed, so the whole
/// hosted-vs-self-host verdict is made HERE, once, on the Gateway (CLAUDE.md rule 7).
///
/// Two modes, one gated on the hosted signal (<see cref="GatewayHostedMode.IsHosted"/>):
///  - HOSTED (<c>CC_GATEWAY_HOSTED=1</c>): a container reached by its public URL, with no tailscale in the
///    image. The public cockpit URL is configuration, read once from <see cref="PublicCockpitUrlEnvVar"/>
///    (set to <c>https://cockpit.devthrottle.com</c> on the App Service). There is NO fallback: a hosted
///    Gateway with that variable unset is a deploy misconfiguration, so this FAILS LOUD
///    (<see cref="System.InvalidOperationException"/>) rather than paper over it with a null or a guess.
///  - SELF-HOST (anything else): byte-identical to before this class existed - the tailnet front-door base
///    from <see cref="TailscaleIdentity.TryGetFrontDoorBaseUrl"/>, which is null when Tailscale is down.
///    The tailscale CLI is shelled ONLY on this branch, never in hosted mode.
///
/// The value returned is a BASE URL with no trailing slash (or null in self-host with Tailscale down);
/// each call site appends its own <c>"/"</c>, exactly as the two endpoints did before, so the emitted
/// string is unchanged in self-host mode.
/// </summary>
public static class GatewayCockpitUrl
{
    /// <summary>
    /// The environment variable carrying the hosted Gateway's public cockpit URL, e.g.
    /// <c>https://cockpit.devthrottle.com</c>. Read once per request; consulted ONLY in hosted mode.
    /// </summary>
    public const string PublicCockpitUrlEnvVar = "CC_GATEWAY_PUBLIC_COCKPIT_URL";

    /// <summary>
    /// Resolve the cockpit base URL against the live environment: the configured public URL in hosted
    /// mode, the tailnet front door otherwise. Fails loud when hosted and the public URL is unset.
    /// Shells the tailscale CLI only on the self-host branch (never in hosted mode).
    /// </summary>
    /// <returns>The base URL with no trailing slash, or null in self-host mode when Tailscale is down.</returns>
    public static string? ResolveBase()
        => GatewayHostedMode.IsHosted
            ? ResolveBase(true, Environment.GetEnvironmentVariable(PublicCockpitUrlEnvVar), selfHostFrontDoor: null)
            : ResolveBase(false, hostedConfiguredUrl: null, TailscaleIdentity.TryGetFrontDoorBaseUrl());

    /// <summary>
    /// Pure resolver - fully unit-testable without touching the real environment or shelling tailscale.
    /// </summary>
    /// <param name="isHosted"><see cref="GatewayHostedMode.IsHosted"/>.</param>
    /// <param name="hostedConfiguredUrl">The <see cref="PublicCockpitUrlEnvVar"/> value; may be null or blank.</param>
    /// <param name="selfHostFrontDoor">The self-host front door from
    /// <see cref="TailscaleIdentity.TryGetFrontDoorBaseUrl"/>; null when Tailscale is down.</param>
    /// <returns>The base URL with no trailing slash, or null in self-host mode when the front door is null.</returns>
    /// <exception cref="InvalidOperationException">Hosted mode with <paramref name="hostedConfiguredUrl"/>
    /// missing or blank - a deploy misconfiguration that must not be papered over.</exception>
    public static string? ResolveBase(bool isHosted, string? hostedConfiguredUrl, string? selfHostFrontDoor)
    {
        if (isHosted)
        {
            if (string.IsNullOrWhiteSpace(hostedConfiguredUrl))
                throw new InvalidOperationException(
                    $"{PublicCockpitUrlEnvVar} is not set. A hosted Gateway (CC_GATEWAY_HOSTED=1) must be " +
                    "configured with its public cockpit URL (for example https://cockpit.devthrottle.com). " +
                    "This is a deploy misconfiguration - refusing to serve a missing or guessed cockpit URL.");

            // Normalize to a base with no trailing slash so the call sites' own "/" never doubles it.
            return hostedConfiguredUrl.Trim().TrimEnd('/');
        }

        // Self-host: exactly what the endpoints returned before - the tailnet front door base, null when down.
        return selfHostFrontDoor;
    }
}
