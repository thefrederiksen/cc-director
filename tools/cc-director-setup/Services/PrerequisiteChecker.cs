using System.Diagnostics;
using CcDirectorSetup.Models;

namespace CcDirectorSetup.Services;

public static class PrerequisiteChecker
{
    public static List<PrerequisiteInfo> CreateChecklist(CcDirector.Setup.Engine.InstallRole role)
    {
        // EXACTLY ONE prerequisite is genuinely required: the .NET runtime, because the Windows
        // Director publishes framework-dependent (--self-contained false) and will not start
        // without it. It is also the one item Setup can install itself, so the required gate is
        // satisfiable without the user ever leaving the wizard.
        //
        // The other three were marked required and gated the Next button, which stopped a new user
        // on this screen until they went away and installed three programs by hand. None of them
        // is needed to install DevThrottle or to start it:
        //   - Python: the cc-* tools ship their OWN relocatable CPython (cc-python-win-x64.zip,
        //     staged by PythonToolsInstaller). They never use the system interpreter.
        //   - Node.js: MCP servers and browser tools only.
        //   - Claude Code: only the Claude agent. The Director runs eight agent command line
        //     tools (see src/CcDirector.Core/Agents/), so requiring this one stopped a user who
        //     came for Codex or Gemini for not having a different vendor's tool.
        // They are still checked, still shown, and now installable with one click - but they no
        // longer gate. What is missing is reported on the Complete screen instead (CapabilityNotice).
        var checklist = new List<PrerequisiteInfo>
        {
            new PrerequisiteInfo
            {
                Name = ".NET 10 Runtime",
                Description = "ASP.NET Core Runtime 10 - required: DevThrottle will not start without it",
                IsRequired = true,
                CanAutoInstall = true,
                WingetId = "Microsoft.DotNet.AspNetCore.10",
                InstallUrl = "https://dotnet.microsoft.com/download/dotnet/10.0"
            },
            new PrerequisiteInfo
            {
                Name = "Claude Code",
                Description = "Recommended: the default coding agent. DevThrottle runs other agents too, "
                    + "so you can install this later or use a different one.",
                IsRequired = false,
                CanAutoInstall = true,
                WingetId = "Anthropic.ClaudeCode",
                InstallUrl = "https://docs.anthropic.com/en/docs/claude-code/overview"
            },
            new PrerequisiteInfo
            {
                Name = "Python",
                Description = "Recommended: Python 3.11 or higher, for your own scripts. The cc-* tools "
                    + "bring their own Python and do not need this.",
                IsRequired = false,
                CanAutoInstall = true,
                WingetId = "Python.Python.3.12",
                InstallUrl = "https://www.python.org/downloads/"
            },
            new PrerequisiteInfo
            {
                Name = "Node.js",
                Description = "Recommended: Node.js 20+, needed only for MCP servers and browser tools",
                IsRequired = false,
                CanAutoInstall = true,
                WingetId = "OpenJS.NodeJS.LTS",
                InstallUrl = "https://nodejs.org/"
            },
        };

        // Tailscale is NOT a product requirement on a workstation - the tunnel-only
        // architecture means every Director and launcher dials OUT to the Gateway and no
        // inbound port is ever opened, so a workstation shows no Tailscale row at all.
        // The one optional use left is on the GATEWAY machine itself: phones and browsers
        // reaching the Cockpit off-machine need a secure (HTTPS) address, which Tailscale
        // can provide for free. Everything on the gateway machine itself works at
        // localhost over plain HTTP without it.
        if (role == CcDirector.Setup.Engine.InstallRole.Gateway)
        {
            checklist.Add(new PrerequisiteInfo
            {
                Name = "Tailscale",
                Description = "Optional, gateway machine only: lets your phone and browsers on "
                    + "other computers reach this gateway's Cockpit over a secure (HTTPS) "
                    + "connection. Directors and launchers connect to the gateway on their own "
                    + "and never need Tailscale. Everything on this machine works at localhost "
                    + "without it.",
                IsRequired = false,
                CanAutoInstall = true,
                WingetId = "tailscale.Tailscale",
                InstallUrl = "https://tailscale.com/download"
            });
        }

        return checklist;
    }

    /// <summary>
    /// The wizard's gate: may the user continue past the Prerequisites screen? One place decides,
    /// so the Next button and the screen's own subtitle can never disagree. An empty list means
    /// nothing has been checked yet and is treated as met, exactly as before.
    /// </summary>
    public static bool AllRequiredMet(IEnumerable<PrerequisiteInfo> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        return items.Where(p => p.IsRequired).All(p => p.IsFound);
    }

    public static async Task CheckAllAsync(List<PrerequisiteInfo> items)
    {
        SetupLog.Write("[PrerequisiteChecker] CheckAllAsync: starting");

        foreach (var item in items)
        {
            await Task.Run(() => CheckItem(item));
        }

        SetupLog.Write("[PrerequisiteChecker] CheckAllAsync: complete");
    }

    private static void CheckItem(PrerequisiteInfo item)
    {
        SetupLog.Write($"[PrerequisiteChecker] CheckItem: name={item.Name}");

        try
        {
            switch (item.Name)
            {
                case ".NET 10 Runtime":
                    CheckDotNetRuntime(item);
                    break;
                case "Claude Code":
                    CheckExecutable(item, "claude", "--version");
                    break;
                case "Python":
                    CheckPython(item);
                    break;
                case "Node.js":
                    CheckNode(item);
                    break;
                case "Tailscale":
                    CheckTailscale(item);
                    break;
            }
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[PrerequisiteChecker] CheckItem FAILED: {item.Name} - {ex.Message}");
            item.Status = "Not found";
            item.IsFound = false;
        }
    }

    private static void CheckExecutable(PrerequisiteInfo item, string exe, string args)
    {
        var (found, output) = RunCommand("where", exe);
        if (!found)
        {
            item.Status = "Not found";
            item.IsFound = false;
            SetupLog.Write($"[PrerequisiteChecker] {item.Name}: not found on PATH");
            return;
        }

        var resolved = output.Trim().Split('\n')[0].Trim();
        SetupLog.Write($"[PrerequisiteChecker] {item.Name}: resolved to {resolved}");

        var (versionFound, versionOutput) = RunCommand(exe, args);
        if (versionFound && !string.IsNullOrWhiteSpace(versionOutput))
        {
            item.Version = versionOutput.Trim().Split('\n')[0].Trim();
            item.Status = "Found";
            item.IsFound = true;
            SetupLog.Write($"[PrerequisiteChecker] {item.Name}: found, version={item.Version}");
        }
        else
        {
            item.Version = "";
            item.Status = "Found";
            item.IsFound = true;
            SetupLog.Write($"[PrerequisiteChecker] {item.Name}: found but no version output");
        }
    }

    private static void CheckDotNetRuntime(PrerequisiteInfo item)
    {
        var (found, _) = RunCommand("where", "dotnet");
        if (!found)
        {
            item.Status = "Not found";
            item.IsFound = false;
            SetupLog.Write("[PrerequisiteChecker] .NET: dotnet not found on PATH");
            return;
        }

        var (listed, output) = RunCommand("dotnet", "--list-runtimes");
        if (!listed || string.IsNullOrWhiteSpace(output))
        {
            item.Status = "Not found";
            item.IsFound = false;
            return;
        }

        // A line looks like: "Microsoft.AspNetCore.App 10.0.8 [C:\Program Files\dotnet\shared\...]".
        // Require the ASP.NET Core 10 shared framework; it is a superset that also installs
        // Microsoft.NETCore.App 10 (what the Avalonia Director needs).
        var lines = output.Split('\n');
        var hit = lines.FirstOrDefault(l =>
            l.TrimStart().StartsWith("Microsoft.AspNetCore.App 10.", StringComparison.Ordinal));

        if (hit != null)
        {
            var parts = hit.Trim().Split(' ');
            item.Version = parts.Length >= 2 ? $".NET {parts[1]}" : ".NET 10";
            item.Status = "Found";
            item.IsFound = true;
        }
        else
        {
            item.Status = "Not found (need .NET 10)";
            item.IsFound = false;
        }

        SetupLog.Write($"[PrerequisiteChecker] .NET 10 Runtime: found={item.IsFound}, version={item.Version}");
    }

    private static void CheckPython(PrerequisiteInfo item)
    {
        var (found, _) = RunCommand("where", "python");
        if (!found)
        {
            item.Status = "Not found";
            item.IsFound = false;
            SetupLog.Write("[PrerequisiteChecker] Python: not found on PATH");
            return;
        }

        var (versionFound, versionOutput) = RunCommand("python", "--version");
        if (!versionFound || string.IsNullOrWhiteSpace(versionOutput))
        {
            item.Status = "Not found";
            item.IsFound = false;
            return;
        }

        item.Version = versionOutput.Trim().Split('\n')[0].Trim();

        // Parse version: "Python 3.11.5" -> check >= 3.11
        var versionStr = item.Version.Replace("Python ", "");
        if (Version.TryParse(versionStr, out var ver) && ver.Major >= 3 && ver.Minor >= 11)
        {
            item.Status = "Found";
            item.IsFound = true;
        }
        else
        {
            item.Status = "Too old (need 3.11+)";
            item.IsFound = false;
        }

        SetupLog.Write($"[PrerequisiteChecker] Python: version={item.Version}, found={item.IsFound}");
    }

    private static void CheckNode(PrerequisiteInfo item)
    {
        var (found, _) = RunCommand("where", "node");
        if (!found)
        {
            item.Status = "Not found";
            item.IsFound = false;
            SetupLog.Write("[PrerequisiteChecker] Node.js: not found on PATH");
            return;
        }

        var (versionFound, versionOutput) = RunCommand("node", "--version");
        if (!versionFound || string.IsNullOrWhiteSpace(versionOutput))
        {
            item.Status = "Not found";
            item.IsFound = false;
            return;
        }

        item.Version = versionOutput.Trim().Split('\n')[0].Trim();

        // Parse "v20.11.0" -> check >= 20
        var versionStr = item.Version.TrimStart('v');
        if (Version.TryParse(versionStr, out var ver) && ver.Major >= 20)
        {
            item.Status = "Found";
            item.IsFound = true;
        }
        else
        {
            item.Status = "Too old (need 20+)";
            item.IsFound = false;
        }

        SetupLog.Write($"[PrerequisiteChecker] Node.js: version={item.Version}, found={item.IsFound}");
    }

    /// <summary>
    /// Tailscale status for the OPTIONAL gateway-role row (the workstation checklist has no
    /// Tailscale row - the tunnel-only architecture needs none). The shared engine check
    /// supplies the per-leg result; the row shows the first failing leg so the user knows
    /// what to do if they WANT off-machine Cockpit access.
    /// </summary>
    private static void CheckTailscale(PrerequisiteInfo item)
    {
        var results = CcDirector.Setup.Engine.TailscalePreflight.Run();
        if (CcDirector.Setup.Engine.TailscalePreflight.AllOk(results))
        {
            // The last check carries the MagicDNS name - the HTTPS address for the Cockpit.
            item.Version = results[^1].Detail;
            item.Status = "Found";
            item.IsFound = true;
            SetupLog.Write($"[PrerequisiteChecker] Tailscale: ready, magicdns={item.Version}");
            return;
        }

        var firstFail = results.First(r => !r.Ok);
        item.Version = "";
        item.Status = firstFail.Remedy is null
            ? $"{firstFail.Check}: {firstFail.Detail}"
            : $"{firstFail.Check}: {firstFail.Detail} - {firstFail.Remedy}";
        item.IsFound = false;
        SetupLog.Write($"[PrerequisiteChecker] Tailscale: {item.Status}");
    }

    /// <summary>
    /// Builds the current machine+user PATH straight from the registry. A process snapshots
    /// PATH at launch, so a tool on the USER PATH (e.g. Claude Code at %USERPROFILE%\.local\bin)
    /// is invisible to child "where"/"--version" checks when the wizard inherited a stale PATH.
    /// Reading the Machine/User targets pulls the live value (with %VAR% expansion). Returns null
    /// when nothing could be read, leaving the inherited process PATH in place.
    /// </summary>
    private static string? BuildRefreshedPath()
    {
        var machine = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "";
        var user = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";

        var parts = new[] { machine, user }.Where(p => !string.IsNullOrWhiteSpace(p));
        var combined = string.Join(";", parts);

        return string.IsNullOrWhiteSpace(combined) ? null : combined;
    }

    private static (bool found, string output) RunCommand(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            // Re-read the live PATH from the registry so a tool added to PATH after this wizard
            // launched (or sitting on the USER PATH the process did not inherit) is visible to
            // "where"/"--version" on Re-check without restarting the app.
            var refreshedPath = BuildRefreshedPath();
            if (refreshedPath != null)
                psi.Environment["PATH"] = refreshedPath;

            using var process = Process.Start(psi);
            if (process == null)
                return (false, "");

            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit(10_000);

            // Some tools write version to stderr
            if (string.IsNullOrWhiteSpace(output) && !string.IsNullOrWhiteSpace(error))
                output = error;

            return (process.ExitCode == 0, output);
        }
        catch
        {
            return (false, "");
        }
    }
}
