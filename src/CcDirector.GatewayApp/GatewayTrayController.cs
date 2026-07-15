using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using CcDirector.Core.Utilities;
using CcDirector.Gateway;
using CcDirector.HostedAgent;

namespace CcDirector.GatewayApp;

/// <summary>
/// The tray SHIM: it puts a Start/Stop control in the notification area and renders the Gateway's state
/// as a tooltip. That is the whole of it.
///
/// Everything that makes the Gateway work - the host lifecycle, the managed self-update loop, autostart,
/// the Cockpit's settings hooks, port diagnostics, the issue #880 shutdown watchdog - lives in
/// <see cref="GatewayService"/>, in the headless library. This class only presents it.
///
/// The rule that keeps it honest: DELETE THIS FILE AND NOTHING BREAKS. The dev console host in
/// CcDirector.Gateway drives the very same GatewayService with no tray at all, which is what proves it.
/// When the Gateway becomes a real Windows service, that is simply a third host, and nothing in the
/// library changes.
///
/// A service has exactly two verbs a person can use, so the menu is Start and Stop and nothing else - no
/// flyout, no status panel, no settings, no sign-in, no "open the Cockpit". Every screen the Gateway used
/// to own lives in the Cockpit, reached by browsing to the Gateway's own URL. The Gateway never opens a
/// browser and never draws a window, because a service has no desktop to draw on.
/// </summary>
public sealed class GatewayTrayController : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;
    private readonly GatewayService _service;

    private TrayIcon? _trayIcon;
    private NativeMenuItem? _startItem;
    private NativeMenuItem? _stopItem;
    private bool _busy;
    private bool _disposed;

    public GatewayTrayController(IClassicDesktopStyleApplicationLifetime desktop, int port)
    {
        _desktop = desktop;
        _service = new GatewayService(new GatewayServiceOptions
        {
            Port = port,
            Managed = GatewayAppOptions.Managed,
            RegisterAutostart = GatewayAppOptions.RegisterAutostart,
            AutostartArguments = GatewayAppOptions.AutostartArguments(),
            ModeLabel = GatewayAppOptions.Managed ? "managed" : "dev",
        });
        _service.StateChanged += Render;
        // /shutdown (the self-update helper) has already stopped the Gateway by the time this fires; all
        // that is left is to end THIS process, which only a host knows how to do. The service's #880
        // watchdog hard-exits if this does not complete in time, so a wedged tray cannot strand an update.
        _service.ShutdownRequested += () => Dispatcher.UIThread.Post(() =>
        {
            if (_trayIcon is not null) _trayIcon.IsVisible = false;
            _desktop.Shutdown();
        });
    }

    /// <summary>The gateway's listen port.</summary>
    public int Port => _service.Port;

    /// <summary>When the tray app started.</summary>
    public DateTime StartedAtUtc => _service.StartedAtUtc;

    /// <summary>The running host, or null while stopped/starting.</summary>
    public GatewayHost? Host => _service.Host;

    /// <summary>The Gateway's in-process brain (issue #184); null while the host is stopped/starting.</summary>
    public BrainSupervisor? Brain => _service.Brain;

    /// <summary>Human-readable host state ("Running", "Failed", ...).</summary>
    public string StateText => _service.State.ToString();

    /// <summary>Build the tray control and start the gateway.</summary>
    public void Start()
    {
        FileLog.Write($"[GatewayTrayController] Start (managed={GatewayAppOptions.Managed})");
        BuildTrayIcon();
        _ = _service.StartAsync();
        _service.StartBackgroundWork();
    }

    private void BuildTrayIcon()
    {
        var menu = new NativeMenu();

        _startItem = new NativeMenuItem("Start");
        _startItem.Click += (_, _) => _ = RunVerbAsync(_service.StartAsync);
        menu.Add(_startItem);

        _stopItem = new NativeMenuItem("Stop");
        _stopItem.Click += (_, _) => _ = RunVerbAsync(_service.StopAsync);
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
        Render();
        FileLog.Write("[GatewayTrayController] Tray icon created (Start/Stop only)");
    }

    /// <summary>
    /// Run a service verb off the click. Issue #880: never inline - run inline and the awaits inside the
    /// host resume on the UI thread, where a synchronous brain dispose blocks, freezing the tray and
    /// wedging the operation. Task.Run gives the chain a context-free thread.
    /// </summary>
    private async Task RunVerbAsync(Func<Task> verb)
    {
        if (_busy) return;
        _busy = true;
        Render();
        try
        {
            await Task.Run(verb);
        }
        catch (Exception ex)
        {
            // Boundary catch (this is the click handler body): a verb that throws must never crash the
            // tray. The service has already resolved the state; just log and re-render.
            FileLog.Write($"[GatewayTrayController] verb FAILED: {ex.Message}");
        }
        finally
        {
            _busy = false;
            Render();
        }
    }

    /// <summary>Show the service's state: the tooltip, and which verb currently applies.</summary>
    private void Render()
    {
        var running = _service.Host is not null;
        var tip = $"DevThrottle Gateway - {_service.StatusText}";
        Dispatcher.UIThread.Post(() =>
        {
            if (_trayIcon is not null) _trayIcon.ToolTipText = tip;
            if (_startItem is not null) _startItem.IsEnabled = !running && !_busy;
            if (_stopItem is not null) _stopItem.IsEnabled = running && !_busy;
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _service.StateChanged -= Render;
        _service.Dispose();
        if (_trayIcon is not null) _trayIcon.IsVisible = false;
    }
}
