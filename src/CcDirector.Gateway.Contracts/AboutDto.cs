namespace CcDirector.Gateway.Contracts;

/// <summary>
/// GET /about response: the "what is this Gateway running and what's installed" diagnostics the
/// Cockpit About page renders. Built on the Gateway box (it owns installed.json + its own version).
/// </summary>
public sealed class AboutDto
{
    public string Product { get; set; } = "Director";

    /// <summary>Full informational version, e.g. "0.6.15+sha".</summary>
    public string Version { get; set; } = "";

    /// <summary>Build date of the running Gateway exe ("yyyy-MM-dd HH:mm:ss"), or null.</summary>
    public string? BuildDate { get; set; }

    public string MachineName { get; set; } = "";

    /// <summary>The per-user install root on the Gateway box (%LOCALAPPDATA%\cc-director).</summary>
    public string InstallRoot { get; set; } = "";

    /// <summary>The one front-door URL the Cockpit is reached at, or null when Tailscale is down.</summary>
    public string? CockpitUrl { get; set; }

    /// <summary>Installed component id -> version (from installed.json on the Gateway box).</summary>
    public Dictionary<string, string> InstalledComponents { get; set; } = new();

    /// <summary>
    /// The live process diagnostics the "This machine" Settings tab used to show, relocated here read-only
    /// on BOTH surfaces (issue #2022): the machine settings left the Cockpit Settings page, so the facts a
    /// user still needs to see about a Gateway host live on the About page, which works everywhere. These
    /// have no per-tenant dimension - they describe the host process, not an account - so they are shown as
    /// they are, not partitioned.
    /// </summary>
    public string State { get; set; } = "Running";

    /// <summary>The Gateway's listen port on its own box.</summary>
    public int Port { get; set; }

    /// <summary>Seconds since this Gateway process started.</summary>
    public long UptimeSeconds { get; set; }

    /// <summary>The number of Directors this Gateway currently sees.</summary>
    public int Directors { get; set; }

    /// <summary>The run mode label ("managed" | "dev" | "unknown"), from the host process.</summary>
    public string Mode { get; set; } = "unknown";

    /// <summary>
    /// The auto-resolved public base address this Gateway is reached at (no surface path), or null in
    /// self-host when Tailscale is down. Manual network addressing (Tailscale vs LAN) was retired in issue
    /// #2022 - the address is resolved automatically and shown here read-only, never chosen on a settings page.
    /// </summary>
    public string? Address { get; set; }

    /// <summary>
    /// Whether this is the shared HOSTED Gateway (CC_GATEWAY_HOSTED=1) rather than a self-hosted one on the
    /// owner's own machine (issue #2017). The Settings page reads this ALWAYS-AVAILABLE, public flag to choose
    /// which tabs to render - Gateway-owned tab selection (CLAUDE.md rule 7), never guessed by the client from
    /// a failed fetch: on hosted the machine-scoped "This machine" tab is absent and every kept setting is
    /// scoped to the caller's account; self-host shows the machine tab and one-Gateway scope.
    /// </summary>
    public bool Hosted { get; set; }

    public DateTime ServerTime { get; set; } = DateTime.UtcNow;
}
