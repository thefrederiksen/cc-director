namespace CcDirector.Gateway.Tray;

/// <summary>
/// One line of the flyout's Fleet section: a Director's machine name and a short description
/// ("v0.9.32, seen 2s ago"). A plain record (no UI types) so the Gateway library stays UI-free.
/// </summary>
public sealed record FleetLine(string Label, string Value);

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
    private IReadOnlyList<FleetLine>? _fleetLines; // null until the first heartbeat resolves it
    private int? _deviceCount;         // null until the first heartbeat resolves it
    private int? _machineCount;        // null until the first heartbeat resolves it

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

    /// <summary>Store the latest per-Director fleet lines computed by the background heartbeat.</summary>
    public void SetFleet(IReadOnlyList<FleetLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        lock (_gate)
            _fleetLines = lines;
    }

    /// <summary>
    /// The Fleet section's lines (one per Director), or an empty list until the first heartbeat
    /// resolves them. Reading this never touches the registry.
    /// </summary>
    public IReadOnlyList<FleetLine> FleetLines
    {
        get
        {
            lock (_gate)
                return _fleetLines ?? Array.Empty<FleetLine>();
        }
    }

    /// <summary>Store the latest paired-device count read by the background heartbeat.</summary>
    public void SetDeviceCount(int count)
    {
        lock (_gate)
            _deviceCount = count;
    }

    /// <summary>
    /// The "Devices" row value: "2 paired", "none paired", or <see cref="Placeholder"/> until the
    /// first heartbeat resolves it.
    /// </summary>
    public string DevicesDisplay
    {
        get
        {
            lock (_gate)
                return _deviceCount switch
                {
                    null => Placeholder,
                    0 => "none paired",
                    1 => "1 paired",
                    var n => $"{n} paired",
                };
        }
    }

    /// <summary>Store the latest online launcher (machine) count read by the background heartbeat.</summary>
    public void SetMachineCount(int count)
    {
        lock (_gate)
            _machineCount = count;
    }

    /// <summary>
    /// The "Machines" row value: how many cc-launcher machines are online right now (the launcher
    /// registry sweeps stale entries), or <see cref="Placeholder"/> until the first heartbeat.
    /// </summary>
    public string MachinesDisplay
    {
        get
        {
            lock (_gate)
                return _machineCount switch
                {
                    null => Placeholder,
                    1 => "1 online",
                    var n => $"{n} online",
                };
        }
    }

    /// <summary>
    /// Describe one Director for its Fleet line: trimmed version plus how recently the Gateway saw
    /// it ("v0.9.32, seen 2s ago"), with an explicit warning suffix when its advertised endpoint
    /// stopped answering. Pure (caller passes now) so it is unit-testable.
    /// </summary>
    public static string DescribeDirector(string version, DateTime? lastSeenUtc, string? advertisedEndpointState, DateTime nowUtc)
    {
        var ver = string.IsNullOrWhiteSpace(version) ? "unknown version" : "v" + version.Split('+')[0];
        var seen = lastSeenUtc is { } t ? $", seen {AgeText(nowUtc - t)}" : "";
        var warn = advertisedEndpointState == CcDirector.Gateway.Contracts.DirectorDto.EndpointStateUnreachableByName
            ? ", endpoint unreachable"
            : "";
        return ver + seen + warn;
    }

    /// <summary>"just now", "42s ago", "5m ago", "3h ago" - short enough for one flyout line.</summary>
    public static string AgeText(TimeSpan age)
    {
        if (age < TimeSpan.FromSeconds(5)) return "just now";
        if (age < TimeSpan.FromMinutes(1)) return $"{(int)age.TotalSeconds}s ago";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes}m ago";
        return $"{(int)age.TotalHours}h ago";
    }
}
