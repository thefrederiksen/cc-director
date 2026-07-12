using System.Diagnostics;
using System.Net.Http;
using System.Runtime.Versioning;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Performs the post-placement launcher step on macOS - the macOS twin of
/// <see cref="LauncherTrayInstaller"/>. The generic <see cref="UpdateRunner"/> places the
/// cc-launcher binary but never starts it, so on a fresh install the launcher would sit dormant
/// and its launch agent would never be registered. This:
///   1. restarts the launcher under launchd ("launchctl kickstart -k") when its launch agent is
///      already registered (a reinstall or repair), so the newly placed binary takes over,
///   2. otherwise starts the launcher directly with <c>--managed</c> - on startup the launcher
///      writes and bootstraps its own launch agent property list, exactly as the Windows launcher
///      registers its own Run key, so install-time registration and app self-registration can
///      never disagree,
///   3. waits for the launcher's loopback health endpoint,
///   4. confirms the launch agent property list exists.
///
/// Everything is per-user (the launch agent lives in the user's LaunchAgents folder and the
/// binary under the per-user install root): no elevation, no system daemon. macOS-only.
/// </summary>
public sealed class LauncherMacInstaller
{
    /// <summary>The launch agent label, agreed across the mission sessions on 2026-07-11. The
    /// launcher's own autostart class (LauncherLaunchdAutostart, owned by the launcher port work)
    /// is the canonical definition; consolidate this constant with that class once it exists.</summary>
    public const string LaunchAgentLabel = "com.devthrottle.cc-launcher";

    /// <summary>Runs a short command and returns its exit code and combined output. Injectable so
    /// tests can fake launchctl without a real launchd.</summary>
    public delegate (int Exit, string Output) CommandRunner(string executable, string arguments);

    /// <summary>Starts the launcher process directly and returns its process id. Injectable so
    /// tests can fake the first start without a real binary.</summary>
    public delegate int ProcessStarter(string executablePath, string arguments, string workingDirectory);

    private readonly InstallLayout _layout;
    private readonly HttpClient _http;
    private readonly CommandRunner _runCommand;
    private readonly ProcessStarter _startProcess;
    private readonly string _launchAgentPlistPath;
    private readonly TimeSpan _healthTimeout;

    public LauncherMacInstaller(
        InstallLayout layout,
        HttpClient? http = null,
        CommandRunner? runCommand = null,
        ProcessStarter? startProcess = null,
        string? launchAgentPlistPath = null,
        TimeSpan? healthTimeout = null)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _runCommand = runCommand ?? ProcessRunner.Run;
        _startProcess = startProcess ?? StartDetachedProcess;
        _launchAgentPlistPath = launchAgentPlistPath ?? DefaultLaunchAgentPlistPath();
        _healthTimeout = healthTimeout ?? TimeSpan.FromSeconds(20);
    }

    /// <summary>The user's launch agent property list for the launcher:
    /// ~/Library/LaunchAgents/com.devthrottle.cc-launcher.plist.</summary>
    public static string DefaultLaunchAgentPlistPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "LaunchAgents", LaunchAgentLabel + ".plist");

    /// <summary>
    /// Start the already-placed launcher and verify it is healthy and registered as a launch
    /// agent. The launcher binary must already be placed (by the <see cref="UpdateRunner"/>) at
    /// <see cref="InstallLayout.PathFor"/> for the Launcher component.
    /// </summary>
    [SupportedOSPlatform("macos")]
    public async Task<LauncherInstallResult> InstallAsync(CancellationToken ct = default)
    {
        var steps = new List<string>();
        EngineLog.Write("[LauncherMacInstaller] InstallAsync begin");

        var launcherBinary = _layout.PathFor(ComponentRegistry.Launcher);
        if (!File.Exists(launcherBinary))
            return Fail(steps, $"Launcher binary not present at {launcherBinary}; the file placement must run first.");

        if (File.Exists(_launchAgentPlistPath))
        {
            // Reinstall or repair: the agent is registered, so launchd owns the process. A
            // kickstart restart makes launchd stop the old instance and start the newly placed
            // binary - never an in-place overwrite of a running file (the placement was
            // rename-based), and the restart hands over cleanly.
            RestartUnderLaunchd(steps);
        }
        else
        {
            // First install: start the launcher directly. On startup it writes and bootstraps its
            // own launch agent property list (RunAtLoad + KeepAlive + --managed), which we verify
            // below - the same self-registration contract as the Windows Run key.
            try
            {
                var pid = _startProcess(launcherBinary, LauncherTrayInstaller.InstalledArguments, _layout.LauncherDir);
                steps.Add($"started launcher process id {pid} ({LauncherTrayInstaller.InstalledArguments})");
            }
            catch (Exception ex)
            {
                return Fail(steps, $"Failed to start the launcher: {ex.Message}");
            }
        }

        var healthUrl = $"http://127.0.0.1:{LauncherTrayInstaller.LauncherDefaultPort}/healthz";
        var up = await WaitForHttpAsync(healthUrl, _healthTimeout, ct);
        steps.Add($"launcher health endpoint on port {LauncherTrayInstaller.LauncherDefaultPort}: {(up ? "OK" : "no response")}");
        if (!up)
            return Fail(steps, $"Launcher started but did not answer on port {LauncherTrayInstaller.LauncherDefaultPort}. Check {_layout.LogsDir}.");

        var registered = File.Exists(_launchAgentPlistPath);
        steps.Add($"launch agent property list: {(registered ? "registered" : "NOT registered")} at {_launchAgentPlistPath}");
        if (!registered)
            return Fail(steps, "Launcher is healthy but did not register its launch agent property list; check the launcher log.");

        EngineLog.Write("[LauncherMacInstaller] InstallAsync success");
        return new LauncherInstallResult(true,
            $"Launcher installed, running on port {LauncherTrayInstaller.LauncherDefaultPort}, and registered as a launch agent.", steps);
    }

    /// <summary>
    /// Restart the launcher under launchd so the newly placed binary takes over. When the agent
    /// is not actually loaded (a property list left behind without a bootstrap - for example a
    /// crash between writing the file and loading it), kickstart fails; the launcher is then
    /// started directly, and on startup it re-bootstraps its own agent. That direct start is the
    /// documented first-install path, not a fallback that hides a defect.
    /// </summary>
    private void RestartUnderLaunchd(List<string> steps)
    {
        var (uidExit, uidOutput) = _runCommand("/usr/bin/id", "-u");
        if (uidExit != 0 || !int.TryParse(uidOutput.Trim(), out var uid))
        {
            steps.Add($"could not resolve the user id (exit {uidExit}); starting the launcher directly");
            StartDirectly(steps);
            return;
        }

        var serviceTarget = $"gui/{uid}/{LaunchAgentLabel}";
        var (kickExit, kickOutput) = _runCommand("/bin/launchctl", $"kickstart -k {serviceTarget}");
        if (kickExit == 0)
        {
            steps.Add($"restarted the launch agent with launchctl kickstart ({serviceTarget})");
            return;
        }

        EngineLog.Write($"[LauncherMacInstaller] launchctl kickstart failed (exit {kickExit}): {kickOutput.Trim()}");
        steps.Add($"launchctl kickstart failed (exit {kickExit}); the agent is not loaded - starting the launcher directly so it re-registers itself");
        StartDirectly(steps);
    }

    private void StartDirectly(List<string> steps)
    {
        var launcherBinary = _layout.PathFor(ComponentRegistry.Launcher);
        var pid = _startProcess(launcherBinary, LauncherTrayInstaller.InstalledArguments, _layout.LauncherDir);
        steps.Add($"started launcher process id {pid} ({LauncherTrayInstaller.InstalledArguments})");
    }

    /// <summary>Starts the launcher as a plain detached child: no shell, no redirected pipes (a
    /// redirected pipe would tie the launcher's lifetime to the wizard's stdio), fresh
    /// environment inherited from the wizard.</summary>
    private static int StartDetachedProcess(string executablePath, string arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Process.Start returned null");
        return process.Id;
    }

    private async Task<bool> WaitForHttpAsync(string url, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            try
            {
                using var response = await _http.GetAsync(url, ct);
                if (response.IsSuccessStatusCode) return true;
            }
            catch
            {
                // not up yet
            }
            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    private static LauncherInstallResult Fail(List<string> steps, string message)
    {
        EngineLog.Write($"[LauncherMacInstaller] FAILED: {message}");
        return new LauncherInstallResult(false, message, steps);
    }
}
