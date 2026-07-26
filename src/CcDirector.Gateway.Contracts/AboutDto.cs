namespace CcDirector.Gateway.Contracts;

/// <summary>
/// GET /gateway/about response: what the three SERVER-SIDE products are running - this Gateway, the
/// Cockpit bundle it serves, and the mobile app bundle it serves - plus how it is reached.
///
/// It is deliberately NOT a report on the box. The Director is absent: it has its own About box (see
/// CcDirector.Core AboutInfo.SharedRows) and its own screen in the Cockpit, and its facts do not belong
/// on a page about server versions. The install root, the operating-system machine name, the run-mode
/// label and the installer's component manifest are all gone with it - on the hosted service they were
/// internal detail about somebody else's infrastructure, and on a self-hosted Gateway the install root
/// leaked the operating-system user name into a page any enrolled device could read.
/// </summary>
public sealed class AboutDto
{
    /// <summary>Full informational version of the running Gateway, e.g. "0.6.15+sha".</summary>
    public string Version { get; set; } = "";

    /// <summary>Build date of the running Gateway executable ("yyyy-MM-dd HH:mm:ss"), or null.</summary>
    public string? BuildDate { get; set; }

    /// <summary>
    /// The build stamp of the Cockpit bundle this Gateway serves at /c, or null when no built bundle is
    /// staged (a routine Debug build does not build the web apps). Read from wwwroot/c/build.json - the
    /// Gateway cannot read the commit compiled into the bundle's own JavaScript.
    /// </summary>
    public BundleStampDto? Cockpit { get; set; }

    /// <summary>
    /// The build stamp of the mobile app bundle this Gateway serves at /mobile, or null when no built
    /// bundle is staged. Read from wwwroot/mobile/build.json.
    /// </summary>
    public BundleStampDto? Mobile { get; set; }

    /// <summary>
    /// The folded deployment label the client renders verbatim - "Hosted service" or "Self-hosted"
    /// (CLAUDE.md rule 7: the Gateway owns the verdict, the client never re-derives it from a flag).
    /// </summary>
    public string Deployment { get; set; } = "";

    /// <summary>
    /// The auto-resolved public base address this Gateway is reached at (no surface path), or null in
    /// self-host when Tailscale is down. Manual network addressing was retired in issue #2022 - the
    /// address is resolved automatically and shown read-only, never chosen on a settings page.
    /// </summary>
    public string? Address { get; set; }

    /// <summary>The one front-door URL the Cockpit is reached at, or null when Tailscale is down.</summary>
    public string? CockpitUrl { get; set; }

    /// <summary>
    /// The Gateway's own listen port, or NULL on the hosted service. Hosted clients reach the Gateway
    /// only through <see cref="Address"/> on 443 (the platform terminates TLS there and forwards to the
    /// container's internal port), so the internal number composes with nothing a caller can use: shown
    /// beside an https address it reads as a reachable port and is not one. Self-hosted it IS the port
    /// the Gateway is listening on and is worth showing, so the Gateway decides here and the client just
    /// renders what it is given.
    /// </summary>
    public int? Port { get; set; }

    /// <summary>Seconds since this Gateway process started.</summary>
    public long UptimeSeconds { get; set; }

    /// <summary>The Gateway's current time (UTC).</summary>
    public DateTime ServerTime { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// The build identity of one served web bundle, read from the <c>build.json</c> its Vite build emits.
/// The bundles carry no meaningful semantic version of their own (they ship with the Gateway), so the
/// commit plus the build time IS their version.
/// </summary>
public sealed class BundleStampDto
{
    /// <summary>The short commit the bundle was built from.</summary>
    public string Commit { get; set; } = "";

    /// <summary>When the bundle was built (UTC), or null when the stamp carries no time.</summary>
    public DateTime? BuildTime { get; set; }
}
