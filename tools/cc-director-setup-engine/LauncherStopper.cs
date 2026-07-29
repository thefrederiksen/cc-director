using System.Diagnostics;
using System.Net.Http;
using System.Net.Sockets;

namespace CcDirector.Setup.Engine;

/// <summary>
/// One launcher process found on this machine: its id and the full command line behind it.
/// </summary>
/// <param name="CommandLine">
/// The WHOLE command line, not a parsed executable. On macOS <c>ps</c> does not quote paths, and the
/// installed launcher lives under "Library/Application Support" - a path WITH A SPACE - so splitting
/// on whitespace produced "/Users/&lt;user&gt;/Library/Application" and the scope check never matched.
/// The executable is the leading text, so a prefix comparison is both correct and space-safe.
/// </param>
public sealed record LauncherProcess(int Pid, string CommandLine);

/// <summary>
/// Stops the INSTALLED launcher, whatever started it, and proves the port is free afterwards.
///
/// This exists because a Mac was left unable to install anything. The macOS uninstall only asked
/// launchd to boot the service out, which does nothing for a launcher launchd never started - and
/// reported success. The installer's own first-install path started launchers that way, so the product
/// manufactured a process its own uninstaller could not remove, and every later install collided with
/// it on port 7900.
///
/// The order here is the fix, and every step of it comes from something observed on that machine:
///
/// 1. Find OUR launchers first, scoped to the install-owned directory. Nothing is asked or stopped
///    before this, because a launcher a developer runs from a repository checkout shares the same
///    per-user token file - so asking "whoever owns the port" to quit would shut down their launcher.
/// 2. Ask ours to quit through its own endpoint, while the token that authorizes it still exists. An
///    uninstall wipe deletes that token; a shutdown attempt on the real orphan came back 401.
/// 3. Stop what is left by process, escalating - the real orphan ignored a polite request.
/// 4. Judge by the PORT and by whether our processes are gone. Steps 1 to 3 report what was
///    attempted; only these say whether it worked, and trusting an attempt is the whole original bug.
/// </summary>
public sealed class LauncherStopper
{
    /// <summary>What the stop attempt did, and whether the machine is actually clear afterwards.</summary>
    /// <param name="Stopped">
    /// True when no installed launcher process remains AND nothing of ours holds the launcher port.
    /// A port held by something that is not ours does not make this false - we must not claim to have
    /// failed at a job that was never ours - but it IS reported in the steps.
    /// </param>
    public sealed record Result(bool Stopped, IReadOnlyList<string> Steps);

    private readonly InstallLayout _layout;

    public LauncherStopper(InstallLayout layout) => _layout = layout;

    /// <summary>Ask the launcher at this URL to quit, with this token. True when it accepted.</summary>
    public Func<string, string?, bool> RequestQuit { get; init; } = DefaultRequestQuit;

    /// <summary>Every launcher process on the machine (unfiltered - this class does the scoping).</summary>
    public Func<IReadOnlyList<LauncherProcess>> ListLauncherProcesses { get; init; } = DefaultListLauncherProcesses;

    /// <summary>Stop this process, escalating from a polite request to a kill. True when it exited.</summary>
    public Func<int, bool> StopProcess { get; init; } = DefaultStopProcess;

    /// <summary>Is anything listening on the launcher port?</summary>
    public Func<bool> PortInUse { get; init; } = DefaultPortInUse;

    /// <summary>Which process answers on the launcher port, or 0 when nothing does or it will not say.</summary>
    public Func<int> PortOwnerPid { get; init; } = DefaultPortOwnerPid;

    /// <summary>Where the launcher's bearer token lives, inside the per-user root.</summary>
    public string TokenFilePath => Path.Combine(_layout.LocalRoot, "config", "launcher", "launcher-token.txt");

    /// <summary>
    /// Stop every installed launcher. Safe when none is running. MUST be called BEFORE any wipe of the
    /// per-user root, and on macOS AFTER the launch agent is unregistered, or launchd restarts what
    /// this stops.
    /// </summary>
    public Result Stop()
    {
        var steps = new List<string>();

        // 1. Ours, and only ours. Enumerated FIRST and always - a launcher that is still starting,
        //    failed to bind, or runs on another port holds no port yet still has its binary about to
        //    be deleted underneath it.
        var ours = Ours(out var listed);
        if (!listed)
            steps.Add("could not list launcher processes on this machine - relying on the port check alone");
        steps.Add(ours.Count == 0
            ? $"no launcher process running from {_layout.LauncherDir}"
            : $"found {ours.Count} installed launcher process(es): {string.Join(", ", ours.Select(p => p.Pid))}");

        var portOwner = PortOwnerPid();
        var portBusy = PortInUse();
        if (portBusy && portOwner != 0 && ours.All(p => p.Pid != portOwner))
        {
            // Something else holds the launcher port. Not ours to stop, and not ours to claim as a
            // failure either - but the user must be told, because an install will collide with it.
            steps.Add($"port {LauncherTrayInstaller.LauncherDefaultPort} is held by process {portOwner}, "
                      + $"which is NOT running from {_layout.LauncherDir} - left alone");
        }

        if (ours.Count == 0)
        {
            // Nothing of ours is running, so there is nothing for this to fail at. Whatever may hold
            // the port has already been reported above.
            steps.Add("launcher: nothing of ours to stop");
            return new Result(true, steps);
        }

        // 2. Politely, while the token still exists, and only to a launcher that is ours.
        var token = ReadToken();
        if (token is null)
        {
            steps.Add($"launcher token not present at {TokenFilePath} - cannot ask it to quit, "
                      + "stopping it by process instead");
        }
        else if (portBusy && (portOwner == 0 || ours.Any(p => p.Pid == portOwner)))
        {
            var asked = RequestQuit($"http://127.0.0.1:{LauncherTrayInstaller.LauncherDefaultPort}/shutdown", token);
            steps.Add(asked
                ? "the launcher accepted the quit request"
                : "the launcher refused or did not answer the quit request");
            if (asked) WaitFor(() => Ours(out _).Count == 0, TimeSpan.FromSeconds(5));
        }

        // 3. Whatever is left, by process, escalating.
        foreach (var p in Ours(out _))
        {
            var exited = StopProcess(p.Pid);
            steps.Add(exited
                ? $"stopped installed launcher process {p.Pid}"
                : $"could NOT stop installed launcher process {p.Pid}");
        }

        // 4. The facts. Both of them: no process of ours left, and the port not held by us.
        // Give a killed process a moment to release the port before judging.
        WaitFor(() => Ours(out _).Count == 0, TimeSpan.FromSeconds(5));

        var remaining = Ours(out _);
        var stopped = remaining.Count == 0;

        steps.Add(stopped
            ? "no installed launcher process remains"
            : $"installed launcher process(es) STILL RUNNING: {string.Join(", ", remaining.Select(p => p.Pid))}");
        steps.Add(PortInUse()
            ? $"port {LauncherTrayInstaller.LauncherDefaultPort}: still in use"
            : $"port {LauncherTrayInstaller.LauncherDefaultPort}: free");

        return new Result(stopped, steps);
    }

    /// <summary>
    /// The launcher processes that belong to THIS install. Matched on the command line beginning with
    /// the install-owned launcher directory plus a separator - a bare prefix would also match a sibling
    /// like "&lt;LauncherDir&gt;-dev", which is somebody else's launcher.
    /// </summary>
    private List<LauncherProcess> Ours(out bool listed)
    {
        var dir = _layout.LauncherDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var prefixes = new[] { dir + Path.DirectorySeparatorChar, dir + Path.AltDirectorySeparatorChar };

        try
        {
            var all = ListLauncherProcesses();
            listed = true;
            return all
                .Where(p => !string.IsNullOrEmpty(p.CommandLine)
                            && prefixes.Any(prefix => p.CommandLine.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[LauncherStopper] listing launcher processes failed: {ex.Message}");
            listed = false;
            return [];
        }
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

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(250);
        }
        return condition();
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
    /// Every cc-launcher process, with its full command line. Windows reads the main module; macOS and
    /// Linux ask ps, whose output is NOT quoted - so the whole argument string is kept and the caller
    /// compares prefixes rather than trying to parse an executable out of it.
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

        var psi = new ProcessStartInfo("/bin/ps") { RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-axo");
        psi.ArgumentList.Add("pid=,args=");
        using var ps = Process.Start(psi)
                       ?? throw new InvalidOperationException("could not run /bin/ps");
        var output = ps.StandardOutput.ReadToEnd();
        ps.WaitForExit(5000);

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.TrimStart();
            var space = trimmed.IndexOf(' ');
            if (space <= 0) continue;
            if (!int.TryParse(trimmed[..space], out var pid)) continue;

            var commandLine = trimmed[(space + 1)..].Trim();
            if (!commandLine.Contains("cc-launcher", StringComparison.Ordinal)) continue;
            found.Add(new LauncherProcess(pid, commandLine));
        }
        return found;
    }

    /// <summary>
    /// Ask, then insist. The real orphan ignored a polite termination request, so a stop that stops at
    /// politeness stops nothing - but killing first would deny a healthy launcher a clean shutdown.
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

    /// <summary>
    /// Which process answers on the launcher port? Read from the launcher's own public health endpoint,
    /// which reports its process id. Zero when nothing answers or the answer carries no id - and zero
    /// is treated as "unknown", never as "not ours".
    /// </summary>
    private static int DefaultPortOwnerPid()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var resp = http.Send(new HttpRequestMessage(
                HttpMethod.Get, $"http://127.0.0.1:{LauncherTrayInstaller.LauncherDefaultPort}/healthz"));
            if (!resp.IsSuccessStatusCode) return 0;
            using var reader = new StreamReader(resp.Content.ReadAsStream());
            return LauncherHealthProbe.Parse(reader.ReadToEnd()).Pid;
        }
        catch (Exception ex)
        {
            EngineLog.Write($"[LauncherStopper] could not read the port owner: {ex.Message}");
            return 0;
        }
    }
}
