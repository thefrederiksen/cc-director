using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using CcDirector.Core.Utilities;
using CcDirector.Gateway;
using CcDirector.HostedAgent;
using CcDirector.Setup.Engine;

namespace CcDirector.GatewayApp;

/// <summary>
/// Hosts the in-process <see cref="GatewayHost"/> and puts a Start/Stop control in the notification area.
///
/// The Gateway is being reshaped to behave like a WINDOWS SERVICE, and this class is deliberately the only
/// thing standing between it and that: it is a thin SHIM around the host, not the application. A service
/// has exactly two verbs a person can use on it - start and stop - so that is all this tray offers. There
/// is no flyout, no status panel, no settings, no sign-in, and no "open the Cockpit": every screen the
/// Gateway used to own now lives in the Cockpit, which is reached by browsing to the Gateway's own URL.
///
/// The acceptance test for the shape: DELETE THIS CLASS AND NOTHING BREAKS. Anything that would break if
/// this file vanished belongs in CcDirector.Gateway (the headless library), not here. When the Gateway
/// becomes a real service, this shim is deleted and nothing else changes.
///
/// Consequently the Gateway NEVER opens a browser and never draws a window - a service has no desktop to
/// draw on. Signing the Gateway in to its DevThrottle account is done from the Cockpit's Account page,
/// which navigates to the public /account/sign-in-start front door (epic #1069).
/// </summary>
public sealed class GatewayTrayController : IDisposable
{
    private enum HostState { Starting, Running, Stopped, Failed }
    private enum PortProbe { Nothing, OurGateway, OtherListener }

    // Issue #880: how long a /shutdown-initiated graceful quit gets before the watchdog hard-exits
    // the process. Must stay well under the self-update helper's 20-second wait for the exe to
    // unlock, so a wedged quit can never strand an update on the old build.
    private static readonly TimeSpan ShutdownWatchdogGrace = TimeSpan.FromSeconds(10);

    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly int _port;
    private readonly CancellationTokenSource _lifetime = new();

    private TrayIcon? _trayIcon;
    private NativeMenuItem? _startItem;
    private NativeMenuItem? _stopItem;
    private string _statusText = "Gateway stopped";
    private GatewayHost? _host;
    private HostState _state = HostState.Stopped;
    private bool _busy;
    private bool _disposed;

    public GatewayTrayController(IClassicDesktopStyleApplicationLifetime desktop, int port)
    {
        _desktop = desktop;
        _port = port;
    }

    /// <summary>The gateway's listen port.</summary>
    public int Port => _port;

    /// <summary>When the tray app started.</summary>
    public DateTime StartedAtUtc { get; } = DateTime.UtcNow;

    /// <summary>The running host, or null while stopped/starting.</summary>
    public GatewayHost? Host => _host;

    /// <summary>
    /// The Gateway's in-process brain (issue #184): a warm claude.exe this process hosts
    /// itself - no Director dependency. Owned by the <see cref="GatewayHost"/> (the brief
    /// agent drives it, issue #185); null while the host is stopped/starting.
    /// </summary>
    public BrainSupervisor? Brain => _host?.Brain;

    /// <summary>Human-readable host state ("Running", "Failed", ...).</summary>
    public string StateText => _state.ToString();

    /// <summary>Build the tray control, register autostart, and start the gateway.</summary>
    public void Start()
    {
        FileLog.Write($"[GatewayTrayController] Start (managed={GatewayAppOptions.Managed})");

        BuildTrayIcon();
        RegisterAutostartSafe();

        SetState(HostState.Starting);
        _ = StartHostAsync();

        if (GatewayAppOptions.Managed)
            _ = RunUpdateLoopAsync(_lifetime.Token);
    }

    private void BuildTrayIcon()
    {
        // Service semantics: the menu is Start and Stop, and nothing else. The Gateway's status, settings,
        // account, devices and everything else are the Cockpit's, reached by browsing to this Gateway's
        // URL - not by a panel hanging off this icon. The tooltip carries the one thing a person needs
        // from the icon itself: whether it is running.
        var menu = new NativeMenu();

        _startItem = new NativeMenuItem("Start");
        _startItem.Click += (_, _) => _ = StartClickedAsync();
        menu.Add(_startItem);

        _stopItem = new NativeMenuItem("Stop");
        _stopItem.Click += (_, _) => _ = StopClickedAsync();
        menu.Add(_stopItem);

        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://devthrottle-gateway/Assets/tray.ico"))),
            ToolTipText = "DevThrottle Gateway",
            Menu = menu,
            IsVisible = true,
        };

        var icons = new TrayIcons { _trayIcon };
        TrayIcon.SetIcons(Application.Current!, icons);
        ApplyMenuEnablement();
        FileLog.Write("[GatewayTrayController] Tray icon created (Start/Stop only)");
    }

    /// <summary>Start the gateway host (the service's Start verb). A no-op while it is already up.</summary>
    private async Task StartClickedAsync()
    {
        if (_busy) return;
        if (_host is not null)
        {
            FileLog.Write("[GatewayTrayController] Start clicked: already running - ignoring");
            return;
        }
        _busy = true;
        try
        {
            FileLog.Write("[GatewayTrayController] Start clicked");
            SetState(HostState.Starting);
            // Issue #880: the start chain runs on the THREAD POOL, never inline from the click. Run
            // inline, the awaits inside the host resume on the UI thread, where a synchronous dispose
            // can block - freezing the tray and wedging the operation. SetState/ApplyStatus marshal to
            // the UI thread themselves, so the tray stays responsive throughout.
            await Task.Run(StartHostAsync);
        }
        finally
        {
            _busy = false;
            ApplyMenuEnablement();
        }
    }

    /// <summary>Stop the gateway host (the service's Stop verb). The tray stays, so it can be started again.</summary>
    private async Task StopClickedAsync()
    {
        if (_busy) return;
        if (_host is null)
        {
            FileLog.Write("[GatewayTrayController] Stop clicked: already stopped - ignoring");
            return;
        }
        _busy = true;
        try
        {
            FileLog.Write("[GatewayTrayController] Stop clicked");
            // Issue #880: off the UI thread - the host stop also gracefully stops the host-owned brain,
            // whose synchronous dispose would otherwise block the UI thread and wedge the stop.
            await Task.Run(StopHostAsync);
            SetState(HostState.Stopped);
        }
        finally
        {
            _busy = false;
            ApplyMenuEnablement();
        }
    }

    /// <summary>Grey out the verb that does not apply, the way a service control does.</summary>
    private void ApplyMenuEnablement()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_startItem is not null) _startItem.IsEnabled = _host is null && !_busy;
            if (_stopItem is not null) _stopItem.IsEnabled = _host is not null && !_busy;
        });
    }

    private async Task StartHostAsync()
    {
        try
        {
            // A fresh host each start: StopAsync disposes the registry and Tailscale
            // provisioner, so a restart needs a new instance rather than reusing a torn-down one.
            var host = new GatewayHost(_port);
            // The self-update helper POSTs /shutdown to make this process exit so the exe unlocks.
            host.OnShutdownRequested = () =>
            {
                FileLog.Write("[GatewayTrayController] shutdown requested via /shutdown (self-update)");
                // Issue #880: /shutdown must ALWAYS end this process - the self-update swap waits
                // (bounded) for the exe to unlock, and a graceful quit wedged behind a stuck host
                // stop or a frozen UI thread would strand it on the old build. The watchdog
                // hard-exits after the grace period; every store the Gateway owns is written
                // through on mutation, so a hard exit at shutdown time loses nothing.
                var watchdog = new Thread(() =>
                {
                    Thread.Sleep(ShutdownWatchdogGrace);
                    FileLog.Write($"[GatewayTrayController] graceful quit did not finish within {ShutdownWatchdogGrace.TotalSeconds:0}s of /shutdown -> hard process exit (issue #880 watchdog)");
                    Environment.Exit(0);
                })
                { IsBackground = true, Name = "shutdown-watchdog" };
                watchdog.Start();
                _ = QuitAsync();
            };
            // Back the Cockpit Settings page with the bits only THIS process knows (run mode + the
            // per-user autostart Run-key, which needs this exe's path and the managed-launch arguments).
            // These hooks are optional by design - GatewaySettingsHooks degrades to
            // "unknown"/"unsupported" when null - so a headless host without this shim still serves
            // settings correctly. That is what keeps the delete-the-shim test honest.
            host.SettingsHooks = new CcDirector.Gateway.Api.GatewaySettingsHooks
            {
                Mode = () => GatewayAppOptions.Managed ? "managed" : "dev",
                AutostartEnabled = () =>
                    OperatingSystem.IsWindows() ? GatewayAutostart.IsRegistered() : (bool?)null,
                SetAutostart = enable =>
                {
                    if (!OperatingSystem.IsWindows()) return false;
                    if (enable)
                    {
                        var exe = Environment.ProcessPath
                                  ?? throw new InvalidOperationException("Could not resolve own exe path");
                        GatewayAutostart.EnsureRegistered(exe, GatewayAppOptions.AutostartArguments());
                        return true;
                    }
                    GatewayAutostart.Unregister();
                    return false;
                },
            };
            await host.StartAsync();
            _host = host;
            SetState(HostState.Running);
            ApplyMenuEnablement();
            FileLog.Write($"[GatewayTrayController] Gateway running on :{_port}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayTrayController] StartHostAsync FAILED: {ex.Message}");
            await DiagnoseStartFailureAsync();
        }
    }

    /// <summary>
    /// Periodic machine-tier auto-update (managed mode only): check for a newer Gateway and, if
    /// found, launch the detached self-update helper (it POSTs /shutdown -> swap -> relaunch ->
    /// health -> auto-rollback). The Cockpit picks up its own update on the relaunch. Failures only log.
    /// </summary>
    private static async Task RunUpdateLoopAsync(CancellationToken ct)
    {
        var layout = InstallLayout.Default();
        // Let the gateway settle before the first check; never compete with startup.
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
                    var version = await new GatewayUpdater(layout).CheckStageAndLaunchAsync(release, source, ct);
                    if (version is not null)
                    {
                        FileLog.Write($"[GatewayTrayController] launched Gateway self-update to {version}; this process will be asked to exit");
                        return; // the detached helper POSTs /shutdown, swaps, and relaunches us
                    }
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[GatewayTrayController] update check failed: {ex.Message}");
                }
            }
            try { await Task.Delay(cfg.Enabled ? cfg.Interval : TimeSpan.FromHours(1), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    // A bare "FAILED" on a tray icon Windows hides by default is a silent dead-end.
    // The overwhelmingly common cause is the port already being taken, so probe it and
    // say what is actually there. The app stays alive either way, so Start can retry.
    private async Task DiagnoseStartFailureAsync()
    {
        var probe = await ProbePortAsync();
        var (status, tip) = probe switch
        {
            PortProbe.OurGateway => ($"Another gateway already on :{_port}",
                                     $"DevThrottle Gateway - another instance is already serving :{_port}"),
            PortProbe.OtherListener => ($"Port {_port} in use by another app",
                                        $"DevThrottle Gateway - port {_port} is occupied by another app"),
            _ => ("Gateway FAILED - see logs", "DevThrottle Gateway - failed to start"),
        };
        FileLog.Write($"[GatewayTrayController] DiagnoseStartFailure: probe={probe}, status=\"{status}\"");
        _state = HostState.Failed;
        ApplyStatus(status, tip);
        ApplyMenuEnablement();
    }

    // Distinguish "our own gateway is already there" (a benign double-start) from
    // "some other app holds the port" (a real conflict) from "nothing listening"
    // (the bind failed for another reason entirely).
    private async Task<PortProbe> ProbePortAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var resp = await http.GetAsync($"http://127.0.0.1:{_port}/healthz");
            var body = await resp.Content.ReadAsStringAsync();
            if (body.Contains("\"status\":\"ok\"") || body.Contains("\"directors\""))
                return PortProbe.OurGateway;
            return PortProbe.OtherListener; // answered HTTP, but not our gateway shape
        }
        catch
        {
            // Not HTTP (or refused). A raw TCP connect tells us whether anything is
            // listening at all.
            return await CanConnectAsync() ? PortProbe.OtherListener : PortProbe.Nothing;
        }
    }

    private async Task<bool> CanConnectAsync()
    {
        try
        {
            using var tcp = new TcpClient();
            var connect = tcp.ConnectAsync("127.0.0.1", _port);
            var done = await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(1)));
            return done == connect && tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    private async Task StopHostAsync()
    {
        if (_host is null) return;
        try
        {
            await _host.StopAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayTrayController] StopHostAsync error: {ex.Message}");
        }
        finally
        {
            _host = null;
        }
    }

    /// <summary>
    /// End the process. NOT a menu item - a service has no "quit", only stop. This is reached solely by
    /// the self-update helper's POST /shutdown (via <see cref="GatewayHost.OnShutdownRequested"/>), which
    /// needs this process to exit so the exe unlocks and can be swapped.
    /// </summary>
    private async Task QuitAsync()
    {
        FileLog.Write("[GatewayTrayController] QuitAsync");
        _lifetime.Cancel();
        // Issue #880: the host stop (which also gracefully stops the host-owned brain) must never run
        // with the UI thread's context captured, or a slow brain stop freezes the tray and wedges the quit.
        await Task.Run(StopHostAsync);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_trayIcon is not null) _trayIcon.IsVisible = false;
            _desktop.Shutdown();
        });
    }

    private void RegisterAutostartSafe()
    {
        if (!GatewayAppOptions.RegisterAutostart)
        {
            FileLog.Write("[GatewayTrayController] Autostart registration skipped (--no-autostart)");
            return;
        }

        try
        {
            var exePath = Environment.ProcessPath
                          ?? Process.GetCurrentProcess().MainModule?.FileName
                          ?? throw new InvalidOperationException("Could not resolve own exe path for autostart");
            GatewayAutostart.EnsureRegistered(exePath, GatewayAppOptions.AutostartArguments());
        }
        catch (Exception ex)
        {
            // Autostart is a convenience, not a hard dependency of running right now.
            // Log truthfully and keep running rather than failing the whole app.
            FileLog.Write($"[GatewayTrayController] Autostart registration FAILED: {ex.Message}");
        }
    }

    private void SetState(HostState state)
    {
        _state = state;
        var (status, tip) = state switch
        {
            HostState.Starting => ("Gateway starting...", "DevThrottle Gateway - starting"),
            HostState.Running => ($"Gateway running on :{_port}", $"DevThrottle Gateway - running on :{_port}"),
            HostState.Stopped => ("Gateway stopped", "DevThrottle Gateway - stopped"),
            HostState.Failed => ("Gateway FAILED - see logs", "DevThrottle Gateway - failed to start"),
            _ => ("Gateway", "DevThrottle Gateway"),
        };
        ApplyStatus(status, tip);
    }

    private void ApplyStatus(string status, string tip)
    {
        _statusText = status;
        Dispatcher.UIThread.Post(() =>
        {
            if (_trayIcon is not null) _trayIcon.ToolTipText = tip;
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        // Synchronous best-effort stop on shutdown.
        try { _host?.StopAsync().GetAwaiter().GetResult(); } // also gracefully stops the host-owned brain
        catch (Exception ex) { FileLog.Write($"[GatewayTrayController] Dispose stop error: {ex.Message}"); }
        _host = null;
        if (_trayIcon is not null) _trayIcon.IsVisible = false;
        _lifetime.Dispose();
    }
}
