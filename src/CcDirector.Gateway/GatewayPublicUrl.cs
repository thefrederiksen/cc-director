using CcDirector.Core.Network;

namespace CcDirector.Gateway;

/// <summary>
/// Resolves the ONE public URL a client is handed for a Gateway SURFACE - the Cockpit
/// (<see cref="CockpitPath"/>) or the mobile app (<see cref="MobilePath"/>). There is ONE public base
/// URL for this Gateway, and every surface is a PATH under it (owner ruling 2026-07-20): the Cockpit URL
/// is <c>{base}/cockpit</c> and the mobile URL is <c>{base}/mobile</c>, derived here on the Gateway,
/// exactly as localhost already works (<c>host:port/cockpit</c>). One derivation rule, hosted and local
/// alike. The client is dumb: it opens whatever URL it is handed, so the whole hosted-vs-self-host
/// verdict AND the surface path are decided HERE, once, on the Gateway (CLAUDE.md rule 7).
///
/// Two modes, one gated on the hosted signal (<see cref="GatewayHostedMode.IsHosted"/>):
///  - HOSTED (<c>CC_GATEWAY_HOSTED=1</c>): a container reached by its public URL, with no tailscale in the
///    image. The public base URL is configuration, read once from <see cref="PublicBaseUrlEnvVar"/>
///    (set to <c>https://gateway.devthrottle.com</c> on the App Service). There is NO fallback: a hosted
///    Gateway with that variable unset is a deploy misconfiguration, so this FAILS LOUD
///    (<see cref="System.InvalidOperationException"/>) rather than paper over it with a null or a guess.
///  - SELF-HOST (anything else): the base is the tailnet front-door base from
///    <see cref="TailscaleIdentity.TryGetFrontDoorBaseUrl"/>, which is null when Tailscale is down. The
///    base-resolution branch is byte-identical to before this class existed - the tailscale CLI is shelled
///    ONLY on this branch, never in hosted mode - and only the surface PATH is now appended explicitly.
///
/// The value returned is a full URL (base + surface path, e.g. <c>https://gateway.devthrottle.com/cockpit</c>)
/// with no trailing slash, or null in self-host when Tailscale is down. A caller that needs to know the
/// surface is unavailable checks for null; a caller that always has a value (hosted) never sees one.
/// </summary>
public static class GatewayPublicUrl
{
    /// <summary>
    /// The environment variable carrying the hosted Gateway's ONE public base URL, e.g.
    /// <c>https://gateway.devthrottle.com</c>. Read once per request; consulted ONLY in hosted mode.
    /// </summary>
    public const string PublicBaseUrlEnvVar = "CC_GATEWAY_PUBLIC_URL";

    /// <summary>The Cockpit surface path under the base (the React shell fallback serves it).</summary>
    public const string CockpitPath = "/cockpit";

    /// <summary>The Cockpit Learning page path - a ROOT SPA route (issue #472), a sibling of
    /// <see cref="CockpitPath"/>, NOT a child of it. It is <c>{base}/learn</c>, never
    /// <c>{base}/cockpit/learn</c> (which is not a route). The desktop Learn button opens this URL
    /// verbatim - it must NOT compose a path onto the Cockpit URL (CLAUDE.md rule 7).</summary>
    public const string LearnPath = "/learn";

    /// <summary>The mobile app surface path under the base.</summary>
    public const string MobilePath = "/mobile";

    /// <summary>
    /// Resolve the full public URL for the Cockpit surface (<c>{base}/cockpit</c>) against the live
    /// environment: the configured public base in hosted mode, the tailnet front door otherwise.
    /// </summary>
    /// <returns>The full Cockpit URL, or null in self-host mode when Tailscale is down.</returns>
    public static string? ResolveCockpit() => Resolve(CockpitPath);

    /// <summary>
    /// Resolve the full public URL for the Cockpit Learning page (<c>{base}/learn</c>) against the live
    /// environment. Same base rule as <see cref="ResolveCockpit"/>; only the surface path differs. This is
    /// what the Gateway hands the dumb desktop Learn button so it never composes <c>Url + "/learn"</c>.
    /// </summary>
    /// <returns>The full Learn URL, or null in self-host mode when Tailscale is down.</returns>
    public static string? ResolveLearn() => Resolve(LearnPath);

    /// <summary>
    /// Resolve the full public URL for the mobile-app surface (<c>{base}/mobile</c>) against the live
    /// environment. Same base rule as <see cref="ResolveCockpit"/>; the surface path is the only difference.
    ///
    /// DEFERRED to P3: NOTHING wires this yet. The mobile app still serves at <c>/m</c> (unchanged in P1).
    /// The <c>/m</c>-&gt;<c>/mobile</c> app re-base (Vite base, React Router basename, service-worker scope,
    /// and the <c>/m/signin</c>+<c>/m/enroll</c> sign-in seam), the 301, and the first consumer of this
    /// method all land together in P3, where a phone proves it. It exists here now, with its own pure
    /// both-mode unit tests, so the resolver is one complete thing rather than being reopened in P3.
    /// </summary>
    /// <returns>The full mobile URL, or null in self-host mode when Tailscale is down.</returns>
    public static string? ResolveMobile() => Resolve(MobilePath);

    /// <summary>
    /// Resolve the full public URL for a surface PATH against the live environment: the configured public
    /// base in hosted mode, the tailnet front door otherwise. Fails loud when hosted and the base is unset.
    /// Shells the tailscale CLI only on the self-host branch (never in hosted mode).
    /// </summary>
    /// <param name="surfacePath">The surface path, e.g. <see cref="CockpitPath"/> or <see cref="MobilePath"/>.</param>
    /// <returns>The full URL (base + path), or null in self-host mode when Tailscale is down.</returns>
    public static string? Resolve(string surfacePath)
        => GatewayHostedMode.IsHosted
            ? Resolve(true, Environment.GetEnvironmentVariable(PublicBaseUrlEnvVar), selfHostFrontDoor: null, surfacePath)
            : Resolve(false, hostedConfiguredBase: null, TailscaleIdentity.TryGetFrontDoorBaseUrl(), surfacePath);

    /// <summary>
    /// Pure resolver - fully unit-testable without touching the real environment or shelling tailscale.
    /// </summary>
    /// <param name="isHosted"><see cref="GatewayHostedMode.IsHosted"/>.</param>
    /// <param name="hostedConfiguredBase">The <see cref="PublicBaseUrlEnvVar"/> value; may be null or blank.</param>
    /// <param name="selfHostFrontDoor">The self-host front door from
    /// <see cref="TailscaleIdentity.TryGetFrontDoorBaseUrl"/>; null when Tailscale is down.</param>
    /// <param name="surfacePath">The surface path to append to the base (leading slash optional).</param>
    /// <returns>The full URL (base + path) with no trailing slash, or null in self-host mode when the
    /// front door is null.</returns>
    /// <exception cref="InvalidOperationException">Hosted mode with <paramref name="hostedConfiguredBase"/>
    /// missing or blank - a deploy misconfiguration that must not be papered over.</exception>
    public static string? Resolve(bool isHosted, string? hostedConfiguredBase, string? selfHostFrontDoor, string surfacePath)
    {
        var path = NormalizeSurfacePath(surfacePath);

        if (isHosted)
        {
            if (string.IsNullOrWhiteSpace(hostedConfiguredBase))
                throw new InvalidOperationException(
                    $"{PublicBaseUrlEnvVar} is not set. A hosted Gateway (CC_GATEWAY_HOSTED=1) must be " +
                    "configured with its public base URL (for example https://gateway.devthrottle.com). " +
                    "This is a deploy misconfiguration - refusing to serve a missing or guessed public URL.");

            return NormalizeBase(hostedConfiguredBase) + path;
        }

        // Self-host: the tailnet front door base (null when down), with the surface path appended. The
        // base-resolution branch is unchanged; only the explicit surface path is new.
        return string.IsNullOrWhiteSpace(selfHostFrontDoor) ? null : NormalizeBase(selfHostFrontDoor) + path;
    }

    /// <summary>Trim whitespace and any trailing slash so the appended surface path never doubles it.</summary>
    private static string NormalizeBase(string baseUrl) => baseUrl.Trim().TrimEnd('/');

    /// <summary>Ensure the surface path has a single leading slash and no trailing slash.</summary>
    private static string NormalizeSurfacePath(string surfacePath)
    {
        var trimmed = (surfacePath ?? "").Trim().Trim('/');
        return "/" + trimmed;
    }
}
