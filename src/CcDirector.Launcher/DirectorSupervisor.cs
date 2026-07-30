using CcDirector.Core.Network;
using System.Diagnostics;
using System.Net.Http;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Setup.Engine;

namespace CcDirector.Launcher;

/// <summary>
/// One answer from a running Director's own control interface: the version it reports being, the number
/// of sessions it holds (null when it declined to say), and where the answer came from.
/// </summary>
public sealed record DirectorHealth(string? Version, int? Sessions, int Pid, int Port);

/// <summary>
/// Supervises the installed CC Director app.
///
/// Resolves the installed Director via <see cref="InstallLayout"/>:
///   - Windows: %LOCALAPPDATA%/cc-director/app/cc-director.exe
///   - macOS:   ~/Applications/Director.app (an application bundle - a directory)
///
/// Provides start / stop / restart operations with a FileLog audit trail.
///
/// Start strategy: on Windows the exe is started directly with UseShellExecute = true
/// (clean parentage, no pseudo-console inheritance). On macOS the bundle is handed to
/// /usr/bin/open, so the Director becomes a child of launchd, not of this launcher.
///
/// Stop strategy: POST /shutdown to the Director's Control API (graceful, portable).
/// The running Director and its Control API port are discovered through the instance
/// registration files every Director writes to config/director/instances/{id}.json.
/// A registration counts only when its process is alive AND that process's executable
/// is the installed Director - a stale file or a development-slot Director never matches.
///
/// THOSE FILES LIVE IN MORE THAN ONE PLACE, which is easy to get wrong and was.
/// CcStorage.DirectorInstances resolves relative to the CALLER'S home, and the launcher
/// runs at the storage root - but a Director started for a named instance keeps its whole
/// storage under &lt;root&gt;/instances/&lt;slug&gt;/ and registers there. From 1.8 the
/// installed Director boots as instance "default", so on a normal machine the root's own
/// directory is empty and every live registration sits one level in. Both layouts are
/// scanned; see InstanceRegistrationDirectories.
/// </summary>
public sealed class DirectorSupervisor
{
    private readonly InstallLayout _layout;
    private readonly HttpClient _http;

    public DirectorSupervisor() : this(InstallLayout.Default()) { }

    public DirectorSupervisor(InstallLayout layout)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _http = new HttpClient(GatewayHttp.Handler()) { Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <summary>
    /// The installed Director path (per InstallLayout): the exe on Windows, the
    /// application bundle directory on macOS. This is what the supervisor manages.
    /// To launch arbitrary slot builds, use LaunchService directly with an explicit path.
    /// </summary>
    public string DirectorExePath => _layout.PathFor(ComponentRegistry.Director);

    /// <summary>Whether the installed Director exists on disk (exe on Windows, bundle directory on macOS).</summary>
    public bool DirectorExeExists => OperatingSystem.IsWindows()
        ? File.Exists(DirectorExePath)
        : Directory.Exists(DirectorExePath) || File.Exists(DirectorExePath);

    /// <summary>
    /// Whether the installed Director appears to be running (a live instance registration
    /// whose process belongs to the installed Director, or on Windows a process whose
    /// image path matches). Best-effort: does not prove the Control API is healthy.
    /// </summary>
    public bool IsRunning => FindDirectorProcess() is not null;

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

        if (FindDirectorProcess() is { } running)
        {
            FileLog.Write($"[DirectorSupervisor] Start: Director already running (pid={running.Id}); skipping");
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
    /// Stop the running Director gracefully via POST /shutdown to its Control API.
    /// Falls back to process kill only when the Control API is unreachable.
    /// </summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        FileLog.Write("[DirectorSupervisor] StopAsync");

        var proc = FindDirectorProcess();
        if (proc is null)
        {
            FileLog.Write("[DirectorSupervisor] StopAsync: Director not running");
            return;
        }

        // Try graceful shutdown via Control API.
        //
        // A port of 0 means no registration was found for this pid, and that is NOT a normal condition - the
        // Director is demonstrably running, so it registered somewhere. It is logged loudly because the silent
        // version of this cost real damage: when the launcher looked in only one of the registration
        // directories, every 1.8 Director came back with port 0, the graceful shutdown below was skipped
        // without a word, and every remote stop and restart force-killed instead. A force-kill gives the
        // Director no chance to clean up and leaves a phantom interrupted entry in its crash journal, so the
        // failure degraded the very signal used to tell a real crash from a clean stop - while looking like it
        // worked every time. The kill remains the fallback, because a Director that cannot be stopped at all
        // would be worse; what changes is that it can no longer happen quietly.
        var port = FindDirectorPort(proc.Id);
        if (port <= 0)
            FileLog.Write($"[DirectorSupervisor] StopAsync: NO registration found for pid={proc.Id} - cannot reach "
                          + "the Control API, so this stop will FORCE-KILL instead of shutting down gracefully. "
                          + "The Director is running but its instance registration was not found in any known "
                          + "directory; expect a phantom crash-journal entry.");

        if (port > 0)
        {
            try
            {
                FileLog.Write($"[DirectorSupervisor] StopAsync: POST http://127.0.0.1:{port}/shutdown");
                using var resp = await _http.PostAsync($"http://127.0.0.1:{port}/shutdown", content: null, ct);
                FileLog.Write($"[DirectorSupervisor] StopAsync: /shutdown -> {(int)resp.StatusCode}");
                // Wait for the process to exit after the graceful shutdown.
                await WaitForExitAsync(proc, TimeSpan.FromSeconds(10), ct);
                return;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorSupervisor] StopAsync: /shutdown failed ({ex.Message}); falling back to process stop");
            }
        }

        // Fallback: stop the process directly.
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: false);
                FileLog.Write($"[DirectorSupervisor] StopAsync: killed pid={proc.Id}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorSupervisor] StopAsync: kill failed: {ex.Message}");
        }
    }

    /// <summary>
    /// What the running Director says about itself when asked from outside: the version it IS and how
    /// many sessions it currently holds, straight off its own control interface.
    ///
    /// This is the witness the update path needs, and it has to come from outside the Director for the
    /// answer to mean anything (issue #1033). The version is what certifies a swap: a staged update is
    /// newer than what was running, so an answer carrying the new version cannot have come from the old
    /// build - where a liveness check ("something answered") could. The session count is what decides
    /// whether updating now would interrupt live work, and reading it live is stronger than reading a
    /// file the Director wrote at some earlier moment.
    /// </summary>
    public async Task<DirectorHealth?> ReadHealthAsync(CancellationToken ct = default)
    {
        var process = FindDirectorProcess();
        if (process is null)
            return null;

        var port = FindDirectorPort(process.Id);
        if (port <= 0)
        {
            FileLog.Write($"[DirectorSupervisor] ReadHealthAsync: the Director is running (pid={process.Id}) but no "
                          + "instance registration names its control port, so it cannot be asked anything.");
            return null;
        }

        try
        {
            using var response = await _http.GetAsync($"http://127.0.0.1:{port}/healthz", ct);
            if (!response.IsSuccessStatusCode)
            {
                FileLog.Write($"[DirectorSupervisor] ReadHealthAsync: /healthz on {port} answered {(int)response.StatusCode}");
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            var health = System.Text.Json.JsonSerializer.Deserialize<HealthDto>(body, HealthJsonOptions);
            if (health is null)
            {
                FileLog.Write($"[DirectorSupervisor] ReadHealthAsync: /healthz on {port} returned a body that is not a health answer.");
                return null;
            }

            return new DirectorHealth(health.Version, health.Sessions, process.Id, port);
        }
        catch (Exception ex)
        {
            // A Director that is starting, stopping, or already gone refuses the connection. That is a
            // normal answer during an update rather than a fault, so it is recorded and returned as
            // "nothing answered" instead of thrown.
            FileLog.Write($"[DirectorSupervisor] ReadHealthAsync: /healthz on {port} could not be read: {ex.Message}");
            return null;
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions HealthJsonOptions =
        new(System.Text.Json.JsonSerializerDefaults.Web);

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

    /// <summary>
    /// Find a running process that IS the installed Director.
    /// Windows: match the image path of any cc-director process (MainModule works there).
    /// macOS: walk the instance registration files - the registered process must be alive
    /// and its executable must live inside the installed bundle. MainModule is not used
    /// on macOS (unreliable); the executable path comes from /bin/ps.
    /// </summary>
    private Process? FindDirectorProcess()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (var proc in Process.GetProcessesByName("cc-director"))
                {
                    try
                    {
                        var exePath = proc.MainModule?.FileName ?? "";
                        if (string.Equals(exePath, DirectorExePath, StringComparison.OrdinalIgnoreCase))
                            return proc;
                    }
                    catch
                    {
                        // MainModule access may fail for elevated processes; skip.
                    }
                }
                return null;
            }

            foreach (var instance in ReadInstanceRegistrations())
            {
                var proc = TryGetLiveProcess(instance.Pid);
                if (proc is null) continue;
                if (BelongsToInstalledDirector(ExecutablePathForPid(instance.Pid)))
                    return proc;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorSupervisor] FindDirectorProcess error: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Find the installed Director's Control API port: the instance registration whose
    /// Pid is the process we identified as the installed Director. Returns 0 if not found.
    /// </summary>
    private int FindDirectorPort(int directorPid)
    {
        try
        {
            foreach (var instance in ReadInstanceRegistrations())
            {
                if (instance.Pid == directorPid && instance.Port > 0)
                    return instance.Port;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorSupervisor] FindDirectorPort error: {ex.Message}");
        }
        return 0;
    }

    /// <summary>
    /// The folder under the storage root that holds per-instance homes. A Director started for a named
    /// instance keeps its whole storage under &lt;root&gt;/instances/&lt;slug&gt;/ and registers there rather
    /// than in the root's own config directory - which from 1.8 is where the installed Director lives, as
    /// instance "default".
    /// </summary>
    private const string InstancesFolderName = "instances";

    /// <summary>One parsed instance registration file: config/director/instances/{id}.json.</summary>
    internal readonly record struct InstanceRegistration(int Pid, int Port);

    /// <summary>
    /// Read every Director instance registration file (CcStorage.DirectorInstances).
    /// Each running Director writes {DirectorId, Pid, ControlEndpoint, ...}; files of
    /// dead Directors may linger, so callers must verify the Pid is alive.
    /// </summary>
    internal static IEnumerable<string> InstanceRegistrationDirectories()
    {
        // The launcher's own home. Also the path CC_DIRECTOR_INSTANCES_DIR pins, which is how a test aims this
        // whole scan at a throwaway directory.
        yield return CcStorage.DirectorInstances();

        var instancesRoot = Path.Combine(CcStorage.Root(), InstancesFolderName);
        string[] instanceHomes;
        try
        {
            if (!Directory.Exists(instancesRoot)) yield break;
            instanceHomes = Directory.GetDirectories(instancesRoot);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorSupervisor] InstanceRegistrationDirectories: cannot list {instancesRoot}: {ex.Message}");
            yield break;
        }

        foreach (var home in instanceHomes)
            yield return Path.Combine(home, "config", "director", "instances");
    }

    /// <summary>
    /// Read every Director instance registration file, across the launcher's own home AND every per-instance
    /// home (see <see cref="InstanceRegistrationDirectories"/>).
    ///
    /// Each running Director writes {DirectorId, Pid, ControlEndpoint, ...}; files of dead Directors may
    /// linger, so callers must verify the Pid is alive.
    /// </summary>
    internal static List<InstanceRegistration> ReadInstanceRegistrations()
    {
        var result = new List<InstanceRegistration>();

        foreach (var dir in InstanceRegistrationDirectories())
        {
            if (!Directory.Exists(dir)) continue;

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.json");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorSupervisor] ReadInstanceRegistrations: cannot list {dir}: {ex.Message}");
                continue;
            }

            foreach (var file in files)
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("Pid", out var pidEl) || !pidEl.TryGetInt32(out var pid))
                        continue;
                    var port = 0;
                    if (root.TryGetProperty("ControlEndpoint", out var epEl)
                        && Uri.TryCreate(epEl.GetString(), UriKind.Absolute, out var ep))
                        port = ep.Port;
                    result.Add(new InstanceRegistration(pid, port));
                }
                catch
                {
                    // Skip malformed files.
                }
            }
        }

        return result;
    }

    /// <summary>The live process for a pid, or null when it no longer exists.</summary>
    private static Process? TryGetLiveProcess(int pid)
    {
        try
        {
            var proc = Process.GetProcessById(pid);
            return proc.HasExited ? null : proc;
        }
        catch (ArgumentException)
        {
            return null; // no such process
        }
    }

    /// <summary>True when the executable path is the installed Director (inside the bundle on macOS).</summary>
    private bool BelongsToInstalledDirector(string executablePath)
    {
        if (executablePath.Length == 0) return false;
        return OperatingSystem.IsWindows()
            ? string.Equals(executablePath, DirectorExePath, StringComparison.OrdinalIgnoreCase)
            : executablePath.StartsWith(DirectorExePath + "/", StringComparison.Ordinal);
    }

    /// <summary>
    /// The full executable path of a pid on macOS, via /bin/ps (comm holds the absolute
    /// path). Returns "" when the process is gone or ps fails. Not used on Windows.
    /// </summary>
    private static string ExecutablePathForPid(int pid)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/ps",
                UseShellExecute = false,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add("comm=");
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(pid.ToString());

            using var ps = Process.Start(psi);
            if (ps is null) return "";
            var output = ps.StandardOutput.ReadToEnd().Trim();
            ps.WaitForExit();
            return ps.ExitCode == 0 ? output : "";
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DirectorSupervisor] ExecutablePathForPid({pid}) error: {ex.Message}");
            return "";
        }
    }

    private static async Task WaitForExitAsync(Process proc, TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            FileLog.Write("[DirectorSupervisor] WaitForExitAsync: timed out waiting for Director to exit");
        }
    }
}
