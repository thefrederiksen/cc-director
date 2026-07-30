using System.Diagnostics;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Setup.Engine;

namespace CcDirector.Launcher;

/// <summary>
/// The launcher's non-visual core: the loopback web host, the Gateway registration
/// client, and the persistent Gateway command stream, built around one LaunchService
/// and one DirectorSupervisor.
///
/// Extracted from LauncherTrayController so the launcher can run in two modes:
///   - Tray mode (normal): LauncherTrayController owns a core and adds the menu-bar
///     icon and flyout on top.
///   - Headless mode (degraded): on an unattended machine whose screen is locked, the
///     operating system refuses to start a user-interface session (Avalonia cannot
///     create its render timer). A supervisor that dies because an icon cannot be
///     drawn is useless, so Program falls back to running JUST this core - every
///     remote capability (registration, stream commands, self-update) stays alive;
///     only the menu-bar icon is missing.
/// </summary>
public sealed class LauncherCore : IAsyncDisposable
{
    private readonly int _port;
    private readonly string _version;

    private LauncherHost? _host;
    private GatewayRegistrationClient? _gatewayClient;
    private LauncherStreamClient? _launcherStreamClient;
    private bool _stopped;

    public LauncherCore(int port, string version)
    {
        _port = port;
        _version = version ?? throw new ArgumentNullException(nameof(version));
    }

    public int Port => _port;
    public string Version => _version;

    /// <summary>
    /// Start the web host, register with the Gateway, and join the persistent command
    /// stream. <paramref name="requestShutdownAsync"/> is invoked when a /shutdown
    /// request arrives on the web host. <paramref name="userInterfaceState"/> is
    /// "tray" (normal) or "degraded" (headless fallback) and is surfaced on /healthz.
    /// </summary>
    public async Task StartAsync(Func<Task> requestShutdownAsync, string userInterfaceState = "tray")
    {
        ArgumentNullException.ThrowIfNull(requestShutdownAsync);
        FileLog.Write($"[LauncherCore] StartAsync: port={_port}, version={_version}, userInterface={userInterfaceState}");

        var launchService = new LaunchService();
        var directorSupervisor = new DirectorSupervisor();

        // One instance of each query service, shared by the loopback host and the Gateway command stream, for
        // the same reason they share the supervisor and the launch service: two instances would be two places
        // for behaviour to drift apart.
        var appCatalog = new AppCatalog();
        var fileSearch = new FileSearchService();

        _host = new LauncherHost(_port, launchService, directorSupervisor, requestShutdownAsync, _version,
            userInterfaceState, appCatalog, fileSearch);
        await _host.StartAsync();
        FileLog.Write($"[LauncherCore] Host running on :{_port}");

        // Issue #331: register with the Gateway (no-op when gateway not configured).
        // The token is read after host start because LauncherHost writes it on start.
        var gwConfig = GatewayConfig.Load();
        var launcherToken = LauncherAuth.LoadOrCreateToken();
        _gatewayClient = new GatewayRegistrationClient(gwConfig, _port, launcherToken, _version);
        _gatewayClient.Start();

        // launcher-persistent-join: when gateway.streamMode is on, also JOIN the Gateway over a
        // persistent SignalR stream so the Gateway can push lifecycle commands DOWN the open
        // connection instead of dialing this launcher's REST API. Runs alongside the registration
        // client (which stays for metadata). Start() is a no-op unless a Gateway is configured AND
        // stream mode is on.
        _launcherStreamClient = new LauncherStreamClient(gwConfig, _port, _version, directorSupervisor, launchService,
            appCatalog, fileSearch);
        _launcherStreamClient.Start();
    }

    /// <summary>Stop the stream, unregister from the Gateway, and stop the web host. Safe to call twice.</summary>
    public async Task StopAsync()
    {
        if (_stopped) return;
        _stopped = true;
        FileLog.Write("[LauncherCore] StopAsync");

        if (_launcherStreamClient is not null)
        {
            await _launcherStreamClient.StopAsync();
            _launcherStreamClient = null;
        }

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
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>
    /// Why this launcher is not registered to start at login, or null when it is (or was never
    /// asked to be). READ BY /healthz AND /status: a launcher whose autostart registration failed
    /// must not look identical to one that is properly managed.
    ///
    /// This exists because of a Mac that could not install anything. The registration failed, the
    /// failure was caught, one line went to a log, and the launcher carried on reporting perfect
    /// health. Nothing else on the machine or in the fleet could tell that launcher from a managed
    /// one - so the installer certified it, the uninstaller could not stop it, and every later
    /// install collided with it. The state was invisible, which is what made it survive.
    /// </summary>
    public static string? AutostartFailure { get; private set; }

    /// <summary>
    /// Has the autostart state been decided yet? Until it has, "no failure" is not the same as
    /// "healthy" - headless mode starts its web host BEFORE registering, so /healthz answered
    /// autostartOk=true during a window when nothing had been checked at all.
    /// </summary>
    public static bool AutostartChecked { get; private set; }

    /// <summary>
    /// Is this launcher registered to start at login? Distinct from <see cref="AutostartFailure"/>:
    /// autostart turned OFF on purpose is not a failure, but it is also NOT registered - and reporting
    /// "ok" for it left the fleet unable to tell a managed launcher from an unmanaged one, which is the
    /// whole point of publishing this.
    /// </summary>
    public static bool AutostartRegistered { get; private set; }

    /// <summary>
    /// Record the autostart state from somewhere other than startup - the tray's own enable/disable
    /// toggle. Without this the API kept reporting a startup-era failure after the user had fixed it,
    /// or healthy after a later toggle failed.
    /// </summary>
    public static void RecordAutostartState(string? failure, bool registered)
    {
        // Written in this order so a reader that sees Checked=true cannot then read a stale failure.
        AutostartFailure = failure;
        AutostartRegistered = registered;
        AutostartChecked = true;
        FileLog.Write($"[LauncherCore] autostart state recorded: registered={registered}, "
                      + $"failure={failure ?? "none"}");
    }

    /// <summary>
    /// Register the start-at-login autostart for the current executable (the Run key on
    /// Windows, the launchd launch agent on macOS), honoring --no-autostart.
    ///
    /// A failure does NOT take the launcher down - it is still useful without autostart - but it is
    /// recorded in <see cref="AutostartFailure"/> and reported over the launcher's own API. Silence
    /// was the defect.
    /// </summary>
    public static void RegisterAutostartSafe()
    {
        if (!LauncherAppOptions.RegisterAutostart)
        {
            FileLog.Write("[LauncherCore] Autostart registration skipped (--no-autostart)");
            // Not a failure - it was not asked for - but NOT registered either.
            RecordAutostartState(failure: null, registered: false);
            return;
        }

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            RecordAutostartState(failure: null, registered: false);   // no mechanism on this platform
            return;
        }

        try
        {
            var exePath = Environment.ProcessPath
                          ?? Process.GetCurrentProcess().MainModule?.FileName
                          ?? throw new InvalidOperationException("Could not resolve own exe path for autostart");
            // EnsureRegistered returns FALSE for "already correct, nothing written" on both platforms, so
            // its return value cannot be read as success or failure. Ask the machine what the state IS.
            // Getting this wrong would have reported every normally-registered launcher as broken from
            // its second login onward - and on macOS from the FIRST run, because the installer now
            // registers the agent before the launcher starts.
            if (OperatingSystem.IsWindows())
                LauncherAutostart.EnsureRegistered(exePath, LauncherAppOptions.AutostartArguments());
            else
                LauncherLaunchdAutostart.EnsureRegistered(exePath, LauncherAppOptions.AutostartArguments());

            var registered = OperatingSystem.IsWindows()
                ? LauncherAutostart.IsRegistered()
                : LauncherLaunchdAutostart.IsRegistered();

            RecordAutostartState(
                registered
                    ? null
                    : (OperatingSystem.IsWindows()
                        ? "the autostart Run key is not present after registering"
                        : "the launch agent property list is not present after registering"),
                registered);
        }
        catch (Exception ex)
        {
            RecordAutostartState(ex.Message, registered: false);
            FileLog.Write($"[LauncherCore] Autostart registration FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Periodic machine-tier auto-update (managed mode only). Two separate jobs, in this order:
    ///
    ///   1. The LAUNCHER's own update: check for a newer Launcher and, if found, launch the detached
    ///      self-update helper (it POSTs /shutdown -> swap -> relaunch -> health -> auto-rollback).
    ///      Windows only, as it has always been.
    ///   2. The DIRECTOR's update, which the launcher now owns (issue #1033): if one is staged and the
    ///      Director has no sessions running, stop it, swap the build, start it, and confirm the new
    ///      version answers - rolling back if it does not. On BOTH platforms, because the reason the
    ///      Director cannot do this for itself has nothing to do with which operating system it is on.
    ///
    /// The Director's job is deliberately outside the Windows-only branch above it. Putting it inside
    /// would have quietly left every Mac exactly where it was: staging updates that nothing ever
    /// installed. Both jobs are governed by the same auto-update switch the Director used to read, so a
    /// machine with auto-update turned off is still left alone. Failures only log.
    /// </summary>
    public static async Task RunUpdateLoopAsync(CancellationToken ct)
    {
        var layout = InstallLayout.Default();
        var directorUpdates = new DirectorUpdateOwner(new DirectorSupervisor());

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
                        FileLog.Write($"[LauncherCore] launched Launcher self-update to {version}; this process will be asked to exit");
                        return; // the detached helper POSTs /shutdown, swaps, and relaunches us
                    }
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[LauncherCore] update check failed: {ex.Message}");
                }
            }

            if (cfg.Enabled)
            {
                // RunOnceAsync never throws; the catch is here only so a future change to it cannot take
                // the whole loop down and leave the machine with no update path at all.
                try
                {
                    var decision = await directorUpdates.RunOnceAsync(ct);
                    if (decision != DirectorUpdateDecision.NothingStaged)
                        FileLog.Write($"[LauncherCore] Director update pass: {decision}");
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[LauncherCore] Director update pass FAILED: {ex.Message}");
                }
            }

            try { await Task.Delay(cfg.Enabled ? cfg.Interval : TimeSpan.FromHours(1), ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>The launcher's informational version from the executing assembly.</summary>
    public static string ReadVersion()
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
}
