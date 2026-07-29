using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;

namespace CcDirector.Setup.Engine;

/// <summary>One launcher process found on this machine: its id and the executable behind it.</summary>
public sealed record LauncherProcess(int Pid, string ExecutablePath);

/// <summary>
/// Stops the INSTALLED launcher, whatever started it, and proves the port is free afterwards.
///
/// This exists because a Mac was left unable to install anything. The macOS uninstall only asked
/// launchd to boot the service out, which does nothing for a launcher launchd never started - and
/// reported success. The installer's own first-install path starts launchers that way, so the product
/// manufactured a process its own uninstaller could not remove, and every later install collided with
/// it. Windows already stopped by process; macOS never did.
///
/// The ORDER here is the fix, and each step exists because of something observed on that machine:
///
/// 1. Ask the launcher to quit through its own endpoint, FIRST. The token that authorizes that call
///    lives inside the tree an uninstall deletes, so after a wipe there is no way left to ask
///    politely - a shutdown attempt on the real orphan returned 401 because its token file was gone.
/// 2. Stop by process, scoped strictly to the install-owned launcher directory, so a launcher a
///    developer is running from a repository checkout is never touched.
/// 3. Escalate. The real orphan IGNORED a polite termination request and needed to be killed.
/// 4. Verify the port is free. Steps 1 to 3 report what they attempted; only the port says whether
///    it worked, and the whole failure was an installer trusting an attempt instead of a fact.
/// </summary>
public sealed class LauncherStopper
{
    /// <summary>What the stop attempt did, and whether the port ended up free.</summary>
    /// <param name="PortFree">The only success signal that matters: nothing holds the launcher port.</param>
    public sealed record Result(bool PortFree, IReadOnlyList<string> Steps);

    private readonly InstallLayout _layout;

    public LauncherStopper(InstallLayout layout) => _layout = layout;

    /// <summary>Ask the launcher at this URL to quit, with this token. Returns true when it accepted.</summary>
    public Func<string, string?, bool> RequestQuit { get; init; } = DefaultRequestQuit;

    /// <summary>Every launcher process on the machine (unfiltered - this class does the scoping).</summary>
    public Func<IReadOnlyList<LauncherProcess>> ListLauncherProcesses { get; init; } = DefaultListLauncherProcesses;

    /// <summary>Stop this process, escalating from a polite request to a kill. True when it exited.</summary>
    public Func<int, bool> StopProcess { get; init; } = DefaultStopProcess;

    /// <summary>Is anything listening on the launcher port?</summary>
    public Func<bool> PortInUse { get; init; } = DefaultPortInUse;

    /// <summary>Where the launcher's bearer token lives, inside the per-user root.</summary>
    public string TokenFilePath => Path.Combine(_layout.LocalRoot, "config", "launcher", "launcher-token.txt");

    /// <summary>
    /// Stop the installed launcher. Safe to call when none is running: it reports that and succeeds.
    /// MUST be called BEFORE any wipe of the per-user root - see step 1 above.
    /// </summary>
    public Result Stop()
    {
        var steps = new List<string>();

        if (!PortInUse())
        {
            steps.Add("launcher: not running (port free)");
            return new Result(true, steps);
        }

        // 1. Politely, while the token still exists.
        var token = ReadToken();
        steps.Add(token is null
            ? $"launcher token not found at {TokenFilePath} - cannot ask it to quit, will stop it by process"
            : "asking the launcher to quit through its own endpoint");
        if (token is not null)
        {
            var asked = RequestQuit($"http://127.0.0.1:{LauncherTrayInstaller.LauncherDefaultPort}/shutdown", token);
            steps.Add(asked ? "launcher accepted the quit request" : "launcher refused or did not answer the quit request");
            if (asked && WaitForPortFree(TimeSpan.FromSeconds(5)))
            {
                steps.Add("launcher: stopped (quit request, port free)");
                return new Result(true, steps);
            }
        }

        // 2 and 3. By process, scoped to the install-owned directory, escalating.
        var ours = ListLauncherProcesses()
            .Where(p => !string.IsNullOrEmpty(p.ExecutablePath)
                        && p.ExecutablePath.StartsWith(_layout.LauncherDir, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (ours.Count == 0)
        {
            // Something holds the port and it is not ours. Say so plainly rather than reporting
            // success or killing a stranger.
            steps.Add($"port {LauncherTrayInstaller.LauncherDefaultPort} is held by a process that is NOT under {_layout.LauncherDir} - left alone");
            return new Result(false, steps);
        }

        foreach (var p in ours)
        {
            var exited = StopProcess(p.Pid);
            steps.Add(exited
                ? $"stopped installed launcher process {p.Pid}"
                : $"could NOT stop installed launcher process {p.Pid}");
        }

        // 4. The fact, not the attempt.
        var free = WaitForPortFree(TimeSpan.FromSeconds(5));
        steps.Add(free
            ? $"port {LauncherTrayInstaller.LauncherDefaultPort}: free"
            : $"port {LauncherTrayInstaller.LauncherDefaultPort}: STILL IN USE after stopping {ours.Count} process(es)");
        return new Result(free, steps);
    }

    private string? ReadToken()
    {
        try
        {
            if (!File.Exists(TokenFilePath)) return null;
            var token = File.ReadAllText(TokenFilePath).Trim();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[LauncherStopper] could not read the launcher token: {ex.Message}");
            return null;
        }
    }

    private bool WaitForPortFree(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!PortInUse()) return true;
            Thread.Sleep(250);
        }
        return !PortInUse();
    }

    // ---- production implementations ----

    private static bool DefaultRequestQuit(string url, string? token)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (token is not null) req.Headers.Add("Authorization", $"Bearer {token}");
            using var resp = http.Send(req);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[LauncherStopper] quit request failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Every cc-launcher process, with the executable behind it. Windows reads the main module;
    /// macOS and Linux ask ps for the full argument list, because MainModule is not reliable there -
    /// and the executable path is what scopes the stop to our own install.
    /// </summary>
    private static IReadOnlyList<LauncherProcess> DefaultListLauncherProcesses()
    {
        var found = new List<LauncherProcess>();

        if (OperatingSystem.IsWindows())
        {
            foreach (var p in Process.GetProcessesByName("cc-launcher"))
            {
                try { found.Add(new LauncherProcess(p.Id, p.MainModule?.FileName ?? "")); }
                catch (Exception ex) { EngineLog.Write($"[LauncherStopper] pid={p.Id}: {ex.Message}"); }
                finally { p.Dispose(); }
            }
            return found;
        }

        try
        {
            var psi = new ProcessStartInfo("/bin/ps") { RedirectStandardOutput = true, UseShellExecute = false };
            psi.ArgumentList.Add("-axo");
            psi.ArgumentList.Add("pid=,args=");
            using var ps = Process.Start(psi);
            if (ps is null) return found;
            var output = ps.StandardOutput.ReadToEnd();
            ps.WaitForExit(5000);

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.TrimStart();
                var space = trimmed.IndexOf(' ');
                if (space <= 0) continue;
                if (!int.TryParse(trimmed[..space], out var pid)) continue;

                var args = trimmed[(space + 1)..].Trim();
                if (!args.Contains("cc-launcher", StringComparison.Ordinal)) continue;

                // The executable is the first token of the argument list.
                var exe = args.Split(' ', 2)[0];
                found.Add(new LauncherProcess(pid, exe));
            }
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[LauncherStopper] listing launcher processes failed: {ex.Message}");
        }
        return found;
    }

    /// <summary>
    /// Ask, then insist. The real orphan ignored a polite termination request, so a stop that stops at
    /// politeness does not stop anything - but killing first would deny a healthy launcher the chance
    /// to shut down cleanly.
    /// </summary>
    private static bool DefaultStopProcess(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            try
            {
                p.CloseMainWindow();
                if (p.WaitForExit(3000)) return true;
            }
            catch (Exception ex)
            {
                EngineLog.Write($"[LauncherStopper] polite stop of pid={pid} failed: {ex.Message}");
            }

            p.Kill(entireProcessTree: true);
            return p.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            return true;   // already gone
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[LauncherStopper] kill pid={pid} failed: {ex.Message}");
            return false;
        }
    }

    private static bool DefaultPortInUse()
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync("127.0.0.1", LauncherTrayInstaller.LauncherDefaultPort);
            return connect.Wait(TimeSpan.FromMilliseconds(600)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}
