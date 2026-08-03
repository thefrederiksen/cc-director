using System.Diagnostics;
using CcDirector.Core.Lifecycle;
using CcDirector.Core.Utilities;
using CcDirector.Setup.Engine;

namespace CcDirector.Launcher;

/// <summary>
/// What the launcher knows about the Director it supervises, read from outside it and with no network
/// of any kind: which Director it is, the version it is running, and how many sessions it is holding
/// (null when that could not be established).
/// </summary>
public sealed record DirectorStatus(string DirectorId, int Pid, string Version, int? Sessions);

/// <summary>
/// Supervises the installed CC Director app - start, stop, restart, and the two facts the update path
/// needs (what version is running, and whether restarting it would interrupt live work).
///
/// NOTHING HERE USES A NETWORK, AND THAT IS THE POINT. This supervisor used to work by calling the
/// Director's own web interface over loopback: POST /shutdown to stop it and GET /healthz to read its
/// version and session count. Everything else in this product goes through the Gateway, deliberately,
/// so that there is one door - but process lifecycle cannot, because it has to work exactly when the
/// Gateway does not. A Director that could not be stopped without the internet could not be updated
/// without the internet either. So the three things this needs are taken from where they already are:
/// the version and the process id from the registration the Director itself writes, the session count
/// from the roster it maintains, and "exit now" from a named signal the operating system delivers.
///
/// Resolves the installed Director via <see cref="InstallLayout"/>:
///   - Windows: %LOCALAPPDATA%/cc-director/app/cc-director.exe
///   - macOS:   ~/Applications/Director.app (an application bundle - a directory)
///
/// Start strategy: on Windows the exe is started directly with UseShellExecute = true (clean parentage,
/// no pseudo-console inheritance). On macOS the bundle is handed to /usr/bin/open, so the Director
/// becomes a child of launchd, not of this launcher.
///
/// WHICH Director is answered by <see cref="DirectorInstanceLocator"/>, and the reasoning there is
/// worth reading before changing anything here: a process name and an image path are shared by every
/// named instance of one install, so a scan for them cannot tell two Directors apart on a machine that
/// runs several - which this one does.
/// </summary>
public sealed class DirectorSupervisor
{
    private readonly InstallLayout _layout;
    private readonly DirectorInstanceLocator _locator;

    public DirectorSupervisor() : this(InstallLayout.Default(), new DirectorInstanceLocator()) { }

    public DirectorSupervisor(InstallLayout layout) : this(layout, new DirectorInstanceLocator()) { }

    public DirectorSupervisor(InstallLayout layout, DirectorInstanceLocator locator)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
    }

    /// <summary>
    /// How long a Director is given to shut down after being asked, before the launcher stops waiting.
    /// </summary>
    public static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The installed Director path (per InstallLayout): the exe on Windows, the application bundle
    /// directory on macOS. This is what the supervisor manages. To launch arbitrary slot builds, use
    /// LaunchService directly with an explicit path.
    /// </summary>
    public string DirectorExePath => _layout.PathFor(ComponentRegistry.Director);

    /// <summary>Whether the installed Director exists on disk (exe on Windows, bundle directory on macOS).</summary>
    public bool DirectorExeExists => OperatingSystem.IsWindows()
        ? File.Exists(DirectorExePath)
        : Directory.Exists(DirectorExePath) || File.Exists(DirectorExePath);

    /// <summary>Where the supervised Director's identity is resolved from.</summary>
    public DirectorInstanceLocator Locator => _locator;

    /// <summary>
    /// Whether a Director holds the supervised instance.
    ///
    /// An AMBIGUOUS result counts as running. It is not known which process it is, so nothing may be
    /// done TO it - but starting another one on top of two that already exist would make the mess
    /// worse, and that is the only decision this property is used for.
    /// </summary>
    public bool IsRunning => _locator.Resolve().Outcome != DirectorResolution.NotRunning;

    /// <summary>
    /// Start the installed Director if it is not already running.
    /// Windows: UseShellExecute = true for clean parentage (no pseudo-console inheritance).
    /// macOS: /usr/bin/open so the Director is parented by launchd, not this launcher.
    /// </summary>
    public void Start()
    {
        FileLog.Write($"[DirectorSupervisor] Start: target={DirectorExePath}");

        if (!DirectorExeExists)
            throw new FileNotFoundException($"Installed Director not found: {DirectorExePath}", DirectorExePath);

        var lookup = _locator.Resolve();
        if (lookup.Outcome != DirectorResolution.NotRunning)
        {
            FileLog.Write($"[DirectorSupervisor] Start: a Director already holds {_locator.InstanceHome} "
                          + $"({lookup.Outcome}); skipping. Claimants: {Describe(lookup)}");
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            var psi = new ProcessStartInfo
            {
                FileName = DirectorExePath,
                WorkingDirectory = Path.GetDirectoryName(DirectorExePath) ?? "",
                UseShellExecute = true,
            };

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"Process.Start returned null for: {DirectorExePath}");

            FileLog.Write($"[DirectorSupervisor] Start: launched Director pid={proc.Id}");
            return;
        }

        // macOS: hand the bundle to launchd via /usr/bin/open. open exits immediately;
        // the Director's own PID becomes visible through its instance registration file.
        var openPsi = new ProcessStartInfo
        {
            FileName = "/usr/bin/open",
            UseShellExecute = false,
        };
        openPsi.ArgumentList.Add(DirectorExePath);

        using var open = Process.Start(openPsi)
            ?? throw new InvalidOperationException($"Process.Start returned null for: /usr/bin/open {DirectorExePath}");
        open.WaitForExit();
        if (open.ExitCode != 0)
            throw new InvalidOperationException($"/usr/bin/open exited with code {open.ExitCode} for: {DirectorExePath}");

        FileLog.Write($"[DirectorSupervisor] Start: /usr/bin/open accepted launch of {DirectorExePath}");
    }

    /// <summary>
    /// Stop the running Director: raise its shutdown signal, then wait for the process to go.
    ///
    /// THE KILL IS STILL LAST AND IS STILL LOUD. A force-kill gives the Director no chance to end its
    /// sessions, say goodbye to the Gateway, or delete its crash journal - so it leaves a phantom
    /// "interrupted" entry and degrades the very signal used to tell a real crash from a deliberate
    /// stop. It stays only because a Director that cannot be stopped at all would be worse. What
    /// changed is that the graceful path no longer depends on a credential, a port, or anything
    /// answering: the signal is delivered by the operating system to one named Director.
    ///
    /// AN AMBIGUOUS DIRECTOR IS NOT STOPPED AT ALL. When two processes claim the supervised instance
    /// there is no way to tell which one the launcher is responsible for, and stopping the wrong
    /// Director is a worse outcome than stopping nothing - it takes somebody's live sessions with it.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        FileLog.Write("[DirectorSupervisor] StopAsync");

        var lookup = _locator.Resolve();
        if (lookup.Outcome == DirectorResolution.NotRunning)
        {
            FileLog.Write("[DirectorSupervisor] StopAsync: Director not running");
            return;
        }

        if (lookup.Outcome == DirectorResolution.Ambiguous || lookup.Director is not { } director)
        {
            FileLog.Write("[DirectorSupervisor] StopAsync: REFUSING to stop anything - more than one live process "
                          + $"claims {_locator.InstanceHome}, so stopping one of them would be a guess that could "
                          + $"take somebody's live sessions with it. Claimants: {Describe(lookup)}");
            return;
        }

        var signal = LifecycleSignalNames.DirectorShutdown(director.DirectorId);
        var delivered = LifecycleSignal.Raise(signal);
        FileLog.Write($"[DirectorSupervisor] StopAsync: asked {director.DirectorId} (pid={director.Pid}) to shut "
                      + $"down via {signal}; delivered={delivered}");

        if (delivered && await WaitForExitAsync(director.Pid, GracefulShutdownTimeout, ct))
        {
            FileLog.Write($"[DirectorSupervisor] StopAsync: pid={director.Pid} shut down cleanly");
            return;
        }

        FileLog.Write(delivered
            ? $"[DirectorSupervisor] StopAsync: pid={director.Pid} was asked to shut down and was still running "
              + $"{GracefulShutdownTimeout.TotalSeconds:F0}s later. Force-killing it; expect a phantom crash-journal "
              + "entry, because a killed Director never gets to clean up."
            : $"[DirectorSupervisor] StopAsync: pid={director.Pid} is running but is NOT listening for {signal}, so "
              + "it cannot be asked to stop. Force-killing it; expect a phantom crash-journal entry. A Director that "
              + "does not listen for its own shutdown signal is a build older than this launcher, or one whose "
              + "startup failed before it began listening.");

        KillProcess(director.Pid);
    }

    /// <summary>
    /// What the supervised Director is running and how busy it is, read from the files it maintains.
    ///
    /// THIS IS THE WITNESS THE UPDATE PATH NEEDS, AND IT STILL COMES FROM OUTSIDE THE DIRECTOR. The
    /// version certifies a swap: a staged update is newer than what was running, so an answer carrying
    /// the new version cannot have come from the old build - where a mere liveness check ("something is
    /// there") could. That property survives the move off the network because the version is read from
    /// the registration the RUNNING PROCESS wrote when it started, not from the executable on disk.
    /// Reading the version off disk would look equivalent and would silently destroy the whole
    /// mechanism: the file on disk is replaced BEFORE the new Director is started, so it would report
    /// the new version whether or not anything came up, the wait for a healthy build would succeed
    /// instantly every time, and the roll-back to the previous build could never fire.
    ///
    /// Returns null when nothing is running, when which process is the Director is undecidable, or when
    /// the running Director's registration cannot be read - all of which mean "no answer", and none of
    /// which may be read as "idle".
    /// </summary>
    public DirectorStatus? ReadStatus()
    {
        var lookup = _locator.Resolve();
        if (lookup.Outcome != DirectorResolution.Running || lookup.Director is not { } director)
        {
            FileLog.Write($"[DirectorSupervisor] ReadStatus: no single Director owns {_locator.InstanceHome} "
                          + $"({lookup.Outcome}), so there is nothing to report. Claimants: {Describe(lookup)}");
            return null;
        }

        var sessions = _locator.ReadSessionCount(director);
        return new DirectorStatus(director.DirectorId, director.Pid, director.Version, sessions);
    }

    /// <summary>
    /// Restart the Director: stop gracefully, wait, then start fresh.
    /// A staged update is applied by the launcher's update loop, not by the Director itself.
    /// </summary>
    public async Task RestartAsync(CancellationToken ct = default)
    {
        FileLog.Write("[DirectorSupervisor] RestartAsync");
        await StopAsync(ct);
        // Brief pause to let file locks release before relaunching.
        await Task.Delay(500, ct);
        Start();
        FileLog.Write("[DirectorSupervisor] RestartAsync: Director restarted");
    }

    private static string Describe(DirectorLookup lookup)
        => lookup.Candidates.Count == 0 ? "none" : string.Join(" | ", lookup.Candidates);

    /// <summary>
    /// Wait for a process id to leave the process table. True when it did within
    /// <paramref name="timeout"/>.
    /// </summary>
    private static async Task<bool> WaitForExitAsync(int pid, TimeSpan timeout, CancellationToken ct)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return true; // already gone
        }

        using (process)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    private static void KillProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: false);
                FileLog.Write($"[DirectorSupervisor] killed pid={pid}");
            }
        }
        catch (ArgumentException)
        {
            // It exited between the wait and the kill. Nothing to do.
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorSupervisor] kill of pid={pid} failed: {ex.Message}");
        }
    }
}
