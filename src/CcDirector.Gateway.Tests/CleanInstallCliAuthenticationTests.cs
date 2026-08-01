using System.Diagnostics;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The re-inspection's acceptance row for the credential-resolution finding, end to end and across
/// the language boundary: a CLEAN named-default layout - every file under
/// <c>&lt;shared&gt;/instances/default</c>, NOTHING at the flat root, no CC_DIRECTOR_ROOT in the
/// caller's environment - must let the real Python command line authenticate against a real,
/// auth-enabled <see cref="ControlApiHost"/>.
///
/// The host runs exactly as a 1.8 default-instance Director does: its storage root IS the instance
/// home, and its registration file - the production-written one, carrying this process's pid and
/// the bound port - sits under that home. The child process is the real CLI request path
/// (cc_shared.director._request via get_json): it sees only LOCALAPPDATA (or HOME) and
/// CC_DIRECTOR_API, the way an ordinary shell does, and must find the instance's secret by
/// matching the endpoint against the live registrations. Before the fix the resolver composed the
/// flat path, found no secret, and every out-of-process CLI call on a clean install died before
/// even sending the request.
/// </summary>
[Collection("DirectorRoot")]
public sealed class CleanInstallCliAuthenticationTests : IAsyncLifetime
{
    private readonly string _fakeMachineBase;   // stands in for LOCALAPPDATA (or HOME)
    private readonly string _sharedRoot;        // <base>/cc-director (or the posix equivalent)
    private readonly string _instanceHome;      // <sharedRoot>/instances/default
    private readonly string? _prevRoot;
    private ControlApiHost _host = null!;
    private SessionManager _sm = null!;
    private int _port;

    public CleanInstallCliAuthenticationTests()
    {
        _fakeMachineBase = Path.Combine(Path.GetTempPath(), "ccd-clean-install-" + Guid.NewGuid().ToString("N"));
        // The shared root is where the platform convention puts it under the fake base - the child
        // process derives it from LOCALAPPDATA on Windows and from HOME (~/.local/share) elsewhere.
        _sharedRoot = OperatingSystem.IsWindows()
            ? Path.Combine(_fakeMachineBase, "cc-director")
            : Path.Combine(_fakeMachineBase, ".local", "share", "cc-director");
        _instanceHome = Path.Combine(_sharedRoot, "instances", "default");

        // The HOST runs as the default instance: its whole storage tree is the instance home,
        // which is exactly what InstanceContext arranges in the product.
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _instanceHome);
    }

    public async Task InitializeAsync()
    {
        _sm = new SessionManager(new AgentOptions());
        // The registration directory is pinned to the exact path a real named-default Director
        // registers in (its own home) rather than left to the static default, which is captured
        // once per test process and may belong to an earlier fixture's root.
        _host = new ControlApiHost(_sm, "1.0.0-test", () => Task.CompletedTask,
            useEphemeralPort: true, authEnabled: true,
            instancesDirectory: Path.Combine(_instanceHome, "config", "director", "instances"));
        _port = await _host.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _sm.Dispose();
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_fakeMachineBase)) Directory.Delete(_fakeMachineBase, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void TheRealPythonCli_FromACleanShell_AuthenticatesAgainstTheDefaultInstance()
    {
        var python = FindPython();
        if (python is null)
            return; // no Python on this machine; the resolver's unit tests still cover the logic

        // Arrangement sanity: the layout is the clean named-default one. The host minted its secret
        // under the INSTANCE home, its registration (with this process's pid and the bound port)
        // sits there too, and the flat root holds no config at all.
        Assert.True(File.Exists(Path.Combine(_instanceHome, "config", "director", "gateway-token.txt")),
            "arrangement failed: the host did not mint its secret under the instance home");
        Assert.True(Directory.GetFiles(Path.Combine(_instanceHome, "config", "director", "instances"), "*.json").Length > 0,
            "arrangement failed: the host wrote no registration under the instance home");
        Assert.False(Directory.Exists(Path.Combine(_sharedRoot, "config")),
            "arrangement failed: the flat root is supposed to be clean on this layout");

        var toolsDir = FindToolsDirectory();
        var script = Path.Combine(_fakeMachineBase, "drive-cli.py");
        File.WriteAllText(script,
            "import sys\n"
            + $"sys.path.insert(0, {PyLiteral(toolsDir)})\n"
            + "from cc_shared import director\n"
            + "director.get_json(\"workspaces\")\n"   // the real CLI request path, credential and all
            + "print(\"CLI-OK\")\n");

        var psi = new ProcessStartInfo(python, script)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // The ordinary-shell environment the acceptance row names: no CC_DIRECTOR_ROOT inherited,
        // only the machine convention and the target endpoint.
        psi.EnvironmentVariables.Remove("CC_DIRECTOR_ROOT");
        psi.EnvironmentVariables.Remove("CC_DIRECTOR_INSTANCES_DIR");
        psi.EnvironmentVariables[OperatingSystem.IsWindows() ? "LOCALAPPDATA" : "HOME"] = _fakeMachineBase;
        psi.EnvironmentVariables["CC_DIRECTOR_API"] = $"http://127.0.0.1:{_port}";

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(30_000), "the CLI child process did not finish");

        Assert.True(proc.ExitCode == 0 && stdout.Contains("CLI-OK"),
            $"the real CLI could not drive the clean named-default Director. exit={proc.ExitCode}\nstdout: {stdout}\nstderr: {stderr}");
    }

    private static string PyLiteral(string path) => "r\"" + path + "\"";

    /// <summary>tools/ at the repository root, walked up from the test binaries.</summary>
    private static string FindToolsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "cc_shared", "director.py");
            if (File.Exists(candidate))
                return Path.Combine(dir.FullName, "tools");
            dir = dir.Parent;
        }
        throw new InvalidOperationException("tools/cc_shared/director.py not found above " + AppContext.BaseDirectory);
    }

    private static string? FindPython()
    {
        foreach (var name in OperatingSystem.IsWindows() ? new[] { "python" } : new[] { "python3", "python" })
        {
            try
            {
                var psi = new ProcessStartInfo(name, "--version")
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var probe = Process.Start(psi);
                if (probe is null) continue;
                probe.WaitForExit(10_000);
                if (probe.ExitCode == 0) return name;
            }
            catch
            {
                // not on PATH under this name; try the next
            }
        }
        return null;
    }
}
