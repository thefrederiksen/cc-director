using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using CcDirector.Core.Configuration;
using CcDirector.Core.Diagnostics;
using CcDirector.Core.Utilities;
using CcDirector.Setup.Engine;
using CcDirector.TrayUi;

namespace CcDirector.Launcher;

/// <summary>
/// Owns the tray icon, the loopback REST host, the launch service, and the Director
/// supervisor. This is the whole launcher app: no main window, just a notification-area
/// icon whose menu drives everything. Only the Quit menu item (or a /shutdown REST call)
/// shuts it down.
/// </summary>
public sealed class LauncherTrayController : IDisposable
{
    private enum HostState { Starting, Running, Failed }

    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly int _port;
    private readonly CancellationTokenSource _lifetime = new();

    private TrayIcon? _trayIcon;
    private TrayFlyoutController? _flyout;
    private Bitmap? _icon;
    private LauncherHost? _host;
    private GatewayRegistrationClient? _gatewayClient;
    private HostState _state = HostState.Starting;
    private bool _disposed;

    // The flyout's Details values plus the configured Gateway base URL: resolved once, off the UI
    // thread, right after Start (small local file reads that do not change within one run - the
    // Gateway registration also only reads its config at start), so the flyout open stays I/O-free
    // (the same discipline as the Gateway tray, issue #855).
    private const string Placeholder = "...";
    private volatile string _buildDateText = Placeholder;
    private volatile string _installRootText = Placeholder;
    private volatile string _installedText = Placeholder;
    private volatile string? _gatewayBaseUrl;

    public LauncherTrayController(IClassicDesktopStyleApplicationLifetime desktop, int port)
    {
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        _port = port;
    }

    /// <summary>Build the tray icon, register autostart, and start the REST host.</summary>
    public void Start()
    {
        FileLog.Write($"[LauncherTrayController] Start (managed={LauncherAppOptions.Managed})");

        BuildTrayIcon();
        RegisterAutostartSafe();

        SetState(HostState.Starting);
        _ = StartHostAsync();

        // The flyout's Details values: read once, off the UI thread - they cannot change within
        // one run, and the flyout open path must never touch the disk.
        _ = Task.Run(ResolveAboutInfo);

        if (LauncherAppOptions.Managed)
            _ = RunUpdateLoopAsync(_lifetime.Token);
    }

    /// <summary>
    /// Resolve the flyout's Details values (build date, install root, installed component
    /// versions) and the configured Gateway base URL: small local file reads that never change
    /// within one run of this process.
    /// </summary>
    private void ResolveAboutInfo()
    {
        try
        {
            _buildDateText = AboutInfo.BuildDate()?.ToString("yyyy-MM-dd HH:mm") ?? "(unknown)";
            _installRootText = AboutInfo.InstallRoot;
            var installed = AboutInfo.InstalledComponents();
            _installedText = installed.Count == 0
                ? "(no installed.json - dev build?)"
                : string.Join(", ", installed.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(kv => $"{kv.Key} {kv.Value}"));

            var gateway = GatewayConfig.Load();
            _gatewayBaseUrl = gateway.IsEnabled ? gateway.Url.TrimEnd('/') : null;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherTrayController] ResolveAboutInfo FAILED: {ex.Message}");
        }
    }

    private void BuildTrayIcon()
    {
        // Modern interaction: LEFT-CLICK the icon opens the OneDrive-style flyout (live status +
        // action buttons + the Start-with-Windows toggle). The right-click menu is reduced to a
        // single Quit escape hatch - everything else lives in the flyout, not a legacy text menu.
        _icon = new Bitmap(AssetLoader.Open(new Uri("avares://cc-launcher/Assets/icon.png")));
        _flyout = new TrayFlyoutController(BuildFlyoutModel);
        // Create + first-layout the panel window off-screen NOW, so the first left-click is
        // instant instead of paying native window creation.
        _flyout.WarmUp();

        var menu = new NativeMenu();
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => _ = QuitAsync();
        menu.Add(quit);

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://cc-launcher/Assets/tray.ico"))),
            ToolTipText = "CC Launcher",
            Menu = menu,
            IsVisible = true,
        };
        _trayIcon.Clicked += (_, _) => _flyout?.Toggle();

        var icons = new TrayIcons { _trayIcon };
        TrayIcon.SetIcons(Application.Current!, icons);
        FileLog.Write("[LauncherTrayController] Tray icon created");
    }

    /// <summary>Build the flyout's content from current state (called fresh on each open).</summary>
    private TrayFlyoutModel BuildFlyoutModel()
    {
        var supervisor = new DirectorSupervisor();
        var directorState = supervisor.IsRunning ? "running"
            : supervisor.DirectorExeExists ? "stopped"
            : "not installed";

        var rows = new List<StatusRow>
        {
            new("Version", ReadVersion().Split('+')[0]),
            new("Port", _port.ToString()),
            new("Director", directorState),
        };

        var actions = new List<FlyoutAction>
        {
            new() { Text = "Restart Director", Primary = true, OnClick = () => _ = RestartDirectorAsync() },
        };
        // The Cockpit (and the one Settings page, which lives there) is served through the
        // configured Gateway, so these jumps only exist when this machine has a Gateway
        // configured - a dead button would be worse than no button.
        if (_gatewayBaseUrl is { } gw)
        {
            actions.Add(new() { Text = "Open Cockpit", OnClick = () => OpenUrl(gw + "/", "Cockpit") });
            actions.Add(new() { Text = "Settings", OnClick = () => OpenUrl(gw + "/settings", "CockpitSettings") });
        }

        // The quieter diagnostics (matching the Gateway flyout).
        var details = new List<StatusRow>
        {
            new("Gateway", _gatewayBaseUrl ?? "not configured"),
            new("Build date", _buildDateText),
            new("Install root", _installRootText),
            new("Installed", _installedText),
        };

        var footerLinks = new List<FlyoutAction>
        {
            new() { Text = "Logs", OnClick = OpenLogsFolder },
            new() { Text = "Config", OnClick = OpenConfigFolder },
        };

        ToggleSpec? toggle = OperatingSystem.IsWindows()
            ? new ToggleSpec { Label = "Start with Windows", IsOn = LauncherAutostart.IsRegistered(), OnChanged = SetAutostart }
            : null;

        return new TrayFlyoutModel
        {
            AppName = "CC Launcher",
            Icon = _icon,
            StatusTitle = _state switch
            {
                HostState.Running => $"Running on :{_port}",
                HostState.Starting => "Starting...",
                _ => "Failed - see logs",
            },
            StatusPillText = _state.ToString(),
            Status = _state switch
            {
                HostState.Running => StatusLevel.Ok,
                HostState.Starting => StatusLevel.Warn,
                _ => StatusLevel.Error,
            },
            Accent = Color.Parse("#F2600C"), // launcher orange
            Rows = rows,
            DetailRows = details,
            Actions = actions,
            FooterLinks = footerLinks,
            Toggle = toggle,
            OnQuit = () => _ = QuitAsync(),
        };
    }

    private async Task StartHostAsync()
    {
        try
        {
            var launchService = new LaunchService();
            var directorSupervisor = new DirectorSupervisor();
            var version = ReadVersion();

            _host = new LauncherHost(
                _port,
                launchService,
                directorSupervisor,
                QuitAsync,
                version);

            await _host.StartAsync();
            SetState(HostState.Running);
            FileLog.Write($"[LauncherTrayController] Host running on :{_port}");

            // Issue #331: register with the Gateway (no-op when gateway not configured).
            // The token is read after host start because LauncherHost writes it on start.
            var gwConfig = GatewayConfig.Load();
            var launcherToken = LauncherAuth.LoadOrCreateToken();
            _gatewayClient = new GatewayRegistrationClient(gwConfig, _port, launcherToken, version);
            _gatewayClient.Start();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherTrayController] StartHostAsync FAILED: {ex.Message}");
            SetState(HostState.Failed);
        }
    }

    private async Task RestartDirectorAsync()
    {
        FileLog.Write("[LauncherTrayController] RestartDirectorAsync: user requested");
        try
        {
            if (_host is null)
            {
                FileLog.Write("[LauncherTrayController] RestartDirectorAsync: host not ready");
                return;
            }
            // Access the supervisor through the host's internal state by creating a fresh one.
            // The host owns the supervisor internally; for the tray menu we create a standalone one.
            var supervisor = new DirectorSupervisor();
            await supervisor.RestartAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherTrayController] RestartDirectorAsync FAILED: {ex.Message}");
        }
    }

    private async Task QuitAsync()
    {
        FileLog.Write("[LauncherTrayController] QuitAsync");
        _lifetime.Cancel();

        // Issue #331: unregister from the Gateway before shutting down the REST host.
        if (_gatewayClient is not null)
        {
            await _gatewayClient.StopAsync();
            _gatewayClient = null;
        }

        if (_host is not null)
        {
            await _host.StopAsync();
            _host = null;
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _flyout?.Close();
            if (_trayIcon is not null) _trayIcon.IsVisible = false;
            _desktop.Shutdown();
        });
    }

    private void OpenLogsFolder()
        => OpenFolder(Path.GetDirectoryName(FileLog.CurrentLogPath), "OpenLogsFolder");

    private void OpenConfigFolder()
        => OpenFolder(Path.GetDirectoryName(InstallLayout.Default().ConfigPath), "OpenConfigFolder");

    private static void OpenFolder(string? dir, string label)
    {
        if (string.IsNullOrEmpty(dir))
        {
            FileLog.Write($"[LauncherTrayController] {label} REFUSED: no folder path resolved");
            return;
        }
        // Off the UI thread so the click returns immediately (shell launch latency).
        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(dir);
                FileLog.Write($"[LauncherTrayController] {label}: {dir}");
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[LauncherTrayController] {label} FAILED: {ex.Message}");
            }
        });
    }

    /// <summary>Open a URL in the default browser, off the UI thread so the click returns immediately.</summary>
    private static void OpenUrl(string url, string label)
    {
        _ = Task.Run(() =>
        {
            try
            {
                FileLog.Write($"[LauncherTrayController] Open{label}: {url}");
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[LauncherTrayController] Open{label} FAILED: {ex.Message}");
            }
        });
    }

    /// <summary>Enable/disable the Start-with-Windows autostart from the flyout toggle.</summary>
    private void SetAutostart(bool enable)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            if (enable)
            {
                var exePath = Environment.ProcessPath
                    ?? throw new InvalidOperationException("Could not resolve own exe path");
                LauncherAutostart.EnsureRegistered(exePath, LauncherAppOptions.AutostartArguments());
                FileLog.Write("[LauncherTrayController] Autostart enabled by user");
            }
            else
            {
                LauncherAutostart.Unregister();
                FileLog.Write("[LauncherTrayController] Autostart disabled by user");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherTrayController] SetAutostart FAILED: {ex.Message}");
        }
    }

    private void RegisterAutostartSafe()
    {
        if (!LauncherAppOptions.RegisterAutostart)
        {
            FileLog.Write("[LauncherTrayController] Autostart registration skipped (--no-autostart)");
            return;
        }

        if (!OperatingSystem.IsWindows()) return;

        try
        {
            var exePath = Environment.ProcessPath
                          ?? Process.GetCurrentProcess().MainModule?.FileName
                          ?? throw new InvalidOperationException("Could not resolve own exe path for autostart");
            LauncherAutostart.EnsureRegistered(exePath, LauncherAppOptions.AutostartArguments());
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherTrayController] Autostart registration FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Periodic machine-tier auto-update (managed mode only): check for a newer Launcher and, if
    /// found, launch the detached self-update helper (it POSTs /shutdown -> swap -> relaunch ->
    /// health -> auto-rollback). Mirrors the Gateway's update loop. Failures only log.
    /// </summary>
    private static async Task RunUpdateLoopAsync(CancellationToken ct)
    {
        var layout = InstallLayout.Default();
        // Let the launcher settle before the first check; never compete with startup.
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            var cfg = AutoUpdateConfig.Load(layout);
            if (cfg.Enabled && OperatingSystem.IsWindows())
            {
                try
                {
                    var source = new ReleaseSource();
                    var release = await source.FetchLatestAsync(ct);
                    var version = await new LauncherUpdater(layout).CheckStageAndLaunchAsync(release, source, ct);
                    if (version is not null)
                    {
                        FileLog.Write($"[LauncherTrayController] launched Launcher self-update to {version}; this process will be asked to exit");
                        return; // the detached helper POSTs /shutdown, swaps, and relaunches us
                    }
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[LauncherTrayController] update check failed: {ex.Message}");
                }
            }
            try { await Task.Delay(cfg.Enabled ? cfg.Interval : TimeSpan.FromHours(1), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private void SetState(HostState state)
    {
        _state = state;
        var tip = state switch
        {
            HostState.Starting => "CC Launcher - starting",
            HostState.Running => $"CC Launcher - running on :{_port}",
            HostState.Failed => "CC Launcher - failed to start",
            _ => "CC Launcher",
        };
        // The flyout reads state live on open; here we only keep the tray tooltip current.
        Dispatcher.UIThread.Post(() =>
        {
            if (_trayIcon is not null) _trayIcon.ToolTipText = tip;
        });
    }

    private static string ReadVersion()
    {
        try
        {
            var asm = System.Reflection.Assembly.GetExecutingAssembly();
            var attr = System.Reflection.CustomAttributeExtensions
                .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(asm);
            return attr?.InformationalVersion ?? "0.0.0";
        }
        catch
        {
            return "0.0.0";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        try { _gatewayClient?.StopAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { FileLog.Write($"[LauncherTrayController] Dispose gateway client stop error: {ex.Message}"); }
        _gatewayClient = null;
        try { _host?.StopAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { FileLog.Write($"[LauncherTrayController] Dispose stop error: {ex.Message}"); }
        _host = null;
        if (_trayIcon is not null) _trayIcon.IsVisible = false;
        _lifetime.Dispose();
    }
}
