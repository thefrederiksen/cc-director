namespace CcDirector.Gateway.Contracts;

/// <summary>
/// GET /diag/echo response. Backs the mobile Diagnostics page's connection-path readout
/// (auto-network-switching mission). Reports what the Gateway sees about the caller's connection so the
/// phone can tell a direct-LAN hit (a private 192.168.x address) apart from a Tailscale relay (a
/// 100.64.0.0/10 CGNAT address), plus the Gateway's own reachable addresses so the page can show where a
/// direct path would point.
/// </summary>
public sealed class NetDiagEchoDto
{
    /// <summary>
    /// The client IP as the Gateway sees it AFTER X-Forwarded-For processing: the phone's tailnet 100.x
    /// address through the Tailscale front door, or its 192.168.x LAN address on a direct hit.
    /// </summary>
    public string? ClientIp { get; set; }

    /// <summary>Classification of <see cref="ClientIp"/>: "tailscale", "lan", "local", or "other".</summary>
    public string ClientPath { get; set; } = "other";

    /// <summary>The raw X-Forwarded-For header, or empty. Present only when a proxy (Tailscale serve) forwarded the request.</summary>
    public string ForwardedFor { get; set; } = "";

    /// <summary>The Host header the request arrived on (the ts.net front door, a LAN IP, or the machine name).</summary>
    public string Host { get; set; } = "";

    /// <summary>This Gateway's OS host name.</summary>
    public string MachineName { get; set; } = "";

    /// <summary>This Gateway's best LAN IPv4 (e.g. 192.168.1.42), or null when none is available.</summary>
    public string? GatewayLanIp { get; set; }

    /// <summary>This Gateway's Tailscale MagicDNS name (e.g. soren-north.tailnet.ts.net), or null.</summary>
    public string? GatewayTailnetName { get; set; }

    /// <summary>Server UTC time, for a rough clock read.</summary>
    public DateTime ServerTime { get; set; } = DateTime.UtcNow;
}
