namespace CcDirector.Gateway.Contracts;

/// <summary>
/// GET /cockpit response. Tells a caller where this machine's Cockpit lives so it never
/// has to hardcode a host or port. ONE URL (docs/plans/one-url-cockpit.md): the Cockpit is
/// served through the Gateway's Tailscale front door via the fallback proxy, so the URL is
/// the front door itself - never a :7470 URL, never loopback (the tailnet is the trust
/// boundary and a localhost URL would only work on this one machine).
/// </summary>
public sealed class CockpitInfoDto
{
    /// <summary>
    /// The front-door URL serving the Cockpit, e.g. https://machine-a.tail0123.ts.net/.
    /// Null when Tailscale is unavailable on this machine, in which case the caller must surface
    /// the problem rather than fall back to localhost.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// The Cockpit Learning page URL, e.g. https://gateway.devthrottle.com/learn (or, self-hosted,
    /// https://machine-a.tail0123.ts.net/learn). Resolved on the Gateway from the ONE public base, and
    /// null when Tailscale is unavailable self-hosted, exactly like <see cref="Url"/>. The client OPENS
    /// this verbatim; it must NOT compose a path onto <see cref="Url"/>. <see cref="Url"/> is now
    /// {base}/cockpit, so the old client-side <c>Url + "/learn"</c> would yield the non-route
    /// {base}/cockpit/learn - the Gateway owns the URL, the client just opens it (CLAUDE.md rule 7).
    /// </summary>
    public string? LearnUrl { get; set; }

    /// <summary>The loopback port the supervised Cockpit child listens on (diagnostics only).</summary>
    public int Port { get; set; }

    /// <summary>True when the Cockpit process is accepting connections on its loopback port.</summary>
    public bool Up { get; set; }
}
