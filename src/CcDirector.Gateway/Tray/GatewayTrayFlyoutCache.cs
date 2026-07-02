namespace CcDirector.Gateway.Tray;

/// <summary>
/// Thread-safe cache of the values the Gateway tray flyout needs, refreshed by the tray controller's
/// background heartbeat so the flyout open path (CcDirector.GatewayApp.GatewayTrayController) never
/// does a synchronous registry read or a <c>tailscale</c> CLI probe (issue #855). Kept here in the
/// library - not buried in the Avalonia flyout-building method - so the "what does the flyout show
/// before the first heartbeat resolves" placeholder logic is unit-testable without an Avalonia UI
/// thread (mirroring <see cref="CcDirector.Gateway.Account.GatewaySignInTraySurface"/>).
///
/// Cached values:
///   - The Director count, shown in the flyout's "Directors" row. Null until the first heartbeat
///     resolves it, when the row shows <see cref="Placeholder"/> rather than blocking the open.
///   - The Tailscale front-door base URL, used by the Open Cockpit action. Null both before the
///     first heartbeat resolves it AND when Tailscale is genuinely unavailable; the caller treats
///     null as "no tailnet URL" and refuses rather than opening a wrong-everywhere loopback URL.
///   - The Cockpit reachability line ("reachable on :7470"), refreshed by an HTTP probe of the
///     local Cockpit that only ever runs on the heartbeat, never on the flyout open.
///   - The one-line Brain summary ("not started (spawns on first use)"), refreshed by the brain
///     health read (it touches transcript files on disk) on the heartbeat only.
/// </summary>
public sealed class GatewayTrayFlyoutCache
{
    /// <summary>The row value shown until the first heartbeat resolves the real one.</summary>
    public const string Placeholder = "...";

    private readonly object _gate = new();
    private int? _directorCount;      // null until the first heartbeat resolves it
    private string? _frontDoorBaseUrl; // null until resolved OR when Tailscale is unavailable
    private string? _cockpitStatus;    // null until the first probe resolves it
    private string? _brainSummary;     // null until the first health read resolves it

    /// <summary>
    /// Store the latest Director count read by the background heartbeat (off the UI thread).
    /// </summary>
    public void SetDirectorCount(int count)
    {
        lock (_gate)
            _directorCount = count;
    }

    /// <summary>
    /// Store the latest Tailscale front-door base URL resolved by the background heartbeat (off the
    /// UI thread). A null url means Tailscale is unavailable - cached as null so the Open Cockpit
    /// action refuses rather than probing the CLI on the click.
    /// </summary>
    public void SetFrontDoorBaseUrl(string? url)
    {
        lock (_gate)
            _frontDoorBaseUrl = url;
    }

    /// <summary>
    /// The "Directors" row value for the flyout: the cached count, or <see cref="Placeholder"/> until
    /// the first heartbeat resolves it. Reading this never touches the registry.
    /// </summary>
    public string DirectorCountDisplay
    {
        get
        {
            lock (_gate)
                return _directorCount?.ToString() ?? Placeholder;
        }
    }

    /// <summary>
    /// The cached front-door base URL for the Open Cockpit action (e.g.
    /// <c>https://machine-a.tail0123.ts.net</c>), or null when not yet resolved or when Tailscale is
    /// unavailable. Reading this never shells the <c>tailscale</c> CLI.
    /// </summary>
    public string? FrontDoorBaseUrl
    {
        get
        {
            lock (_gate)
                return _frontDoorBaseUrl;
        }
    }

    /// <summary>
    /// Store the latest Cockpit reachability read by the background heartbeat's local HTTP probe.
    /// </summary>
    public void SetCockpitStatus(bool reachable, int port)
    {
        lock (_gate)
            _cockpitStatus = reachable ? $"reachable on :{port}" : $"not reachable on :{port}";
    }

    /// <summary>
    /// The "Cockpit" row value for the flyout: the cached probe result, or <see cref="Placeholder"/>
    /// until the first heartbeat resolves it. Reading this never sends an HTTP request.
    /// </summary>
    public string CockpitStatusDisplay
    {
        get
        {
            lock (_gate)
                return _cockpitStatus ?? Placeholder;
        }
    }

    /// <summary>
    /// Store the latest one-line Brain summary read by the background heartbeat's health check.
    /// </summary>
    public void SetBrainSummary(string summary)
    {
        lock (_gate)
            _brainSummary = summary;
    }

    /// <summary>
    /// The "Brain" row value for the flyout: the cached health summary, or <see cref="Placeholder"/>
    /// until the first heartbeat resolves it. Reading this never touches transcript files.
    /// </summary>
    public string BrainSummaryDisplay
    {
        get
        {
            lock (_gate)
                return _brainSummary ?? Placeholder;
        }
    }
}
