namespace CcDirector.Gateway.Contracts;

/// <summary>
/// GET /cockpit response. Tells a caller where this machine's Cockpit lives so it never
/// has to hardcode a host or port. ONE public base URL, one derivation rule (CLAUDE.md rule 7):
/// the Cockpit is a PATH under the Gateway's public base, resolved on the Gateway, so <see cref="Url"/>
/// is {base}/cockpit hosted (e.g. https://gateway.devthrottle.com/cockpit) and {frontDoor}/cockpit
/// self-hosted (the Tailscale front door, e.g. https://machine-a.tail0123.ts.net/cockpit) - never a
/// :7470 URL, never loopback (the tailnet is the trust boundary and a localhost URL would only work
/// on this one machine).
/// </summary>
public sealed class CockpitInfoDto
{
    /// <summary>
    /// The full Cockpit URL, resolved on the Gateway as {base}/cockpit (hosted) or {frontDoor}/cockpit
    /// (self-host), e.g. https://gateway.devthrottle.com/cockpit or https://machine-a.tail0123.ts.net/cockpit.
    /// Null when Tailscale is unavailable self-hosted, in which case the caller must surface the problem
    /// rather than fall back to localhost. The client OPENS this verbatim; it must NOT compose a path onto
    /// it (the Gateway owns the URL, the client just opens it - CLAUDE.md rule 7).
    /// </summary>
    public string? Url { get; set; }

    /// <summary>The loopback port the supervised Cockpit child listens on (diagnostics only).</summary>
    public int Port { get; set; }

    /// <summary>True when the Cockpit process is accepting connections on its loopback port.</summary>
    public bool Up { get; set; }
}
