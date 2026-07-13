namespace CcDirector.Gateway.Contracts;

/// <summary>
/// One completed speed-test result, submitted by a client (phone or Cockpit) to POST /diag/result and
/// returned by GET /diag/results (Network Diagnostics mission). The client fills the measured fields; the
/// Gateway stamps <see cref="ClientIp"/>, <see cref="ClientPath"/>, and <see cref="ReceivedAt"/> on
/// receipt so an agent reading the history sees both what the user measured AND how the Gateway saw that
/// connection - without needing the phone.
/// </summary>
public sealed class NetDiagResultDto
{
    // ----- filled by the client (what the page measured and showed) -----

    /// <summary>The route the page reported: "tailscale", "lan", "local", or "other".</summary>
    public string Route { get; set; } = "";
    public double? LatencyMedianMs { get; set; }
    public double? LatencyBestMs { get; set; }
    public int LatencySamples { get; set; }
    public double? DownloadMbps { get; set; }
    public double? UploadMbps { get; set; }

    /// <summary>The page's rating: "fast", "ok", or "slow".</summary>
    public string Rating { get; set; } = "";

    /// <summary>The plain-English verdict headline the user saw.</summary>
    public string Verdict { get; set; } = "";

    /// <summary>The host the page was loaded from (window.location.host).</summary>
    public string LoadedFrom { get; set; } = "";

    /// <summary>Which surface ran the test: "mobile" or "cockpit".</summary>
    public string Surface { get; set; } = "";

    /// <summary>
    /// The ACTUAL-path tags for this client, from the Gateway's authoritative self-peer view at test time
    /// (the mobile page already computes this for its verdict). These - NOT the front-door Route/ClientPath -
    /// decide which home (LAN-direct) vs away (relay) sub-sum a result folds into, so quality-by-location is
    /// judged by the measured path and never conflated with the front door.
    /// </summary>
    public bool? Direct { get; set; }
    public bool IsLanPath { get; set; }

    // ----- stamped by the Gateway on receipt -----

    /// <summary>The client IP the Gateway saw for the submission (after X-Forwarded-For).</summary>
    public string? ClientIp { get; set; }

    /// <summary>The Gateway's own classification of <see cref="ClientIp"/>: "tailscale", "lan", "local", "other".</summary>
    public string? ClientPath { get; set; }

    /// <summary>When the Gateway received this result (UTC).</summary>
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}
