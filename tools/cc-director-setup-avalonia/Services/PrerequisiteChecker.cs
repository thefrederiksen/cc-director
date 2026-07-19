using System.Diagnostics;
using System.Runtime.InteropServices;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Models;

namespace CcDirectorSetup.Services;

public static class PrerequisiteChecker
{
    private static readonly bool IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    private static readonly bool IsMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

    private static string DocsBase =>
        GitHubRepositoryDefaults.GitHubUrl("blob/main/docs/public/getting-started/02-installation.md");

    public static List<PrerequisiteInfo> CreateChecklist()
    {
        // NONE of these is required, and this list carries no .NET row because the macOS Director
        // publishes self-contained (--self-contained true) and needs no shared runtime. Marking
        // these three required stopped a new user on this screen until they had installed three
        // programs by hand, and none of them is needed to install DevThrottle or to start it:
        // the cc-* tools ship their own relocatable CPython, Node.js is MCP and browser tools
        // only, and Claude Code is one of the eight agent command line tools the Director runs.
        // They stay checked and shown; what is missing is reported on the Complete screen.
        //
        // No winget on macOS, so there is no auto-install here - the install link is the path.
        return
        [
            new PrerequisiteInfo
            {
                Name = PrerequisiteNames.ClaudeCode,
                IsRecommended = true,
                Description = "Recommended: the default coding agent. DevThrottle runs other agents too, "
                    + "so you can install this later or use a different one.",
                IsRequired = false,
                InstallUrl = "https://docs.anthropic.com/en/docs/claude-code/overview",
                DocsUrl = $"{DocsBase}#claude-code"
            },
            new PrerequisiteInfo
            {
                Name = PrerequisiteNames.Python,
                IsRecommended = true,
                Description = "Recommended: Python 3.11 or higher, for your own scripts. The cc-* tools "
                    + "bring their own Python and do not need this.",
                IsRequired = false,
                InstallUrl = "https://www.python.org/downloads/",
                DocsUrl = $"{DocsBase}#python"
            },
            new PrerequisiteInfo
            {
                Name = PrerequisiteNames.NodeJs,
                IsRecommended = true,
                Description = "Recommended: Node.js 20+, needed only for MCP servers and browser tools",
                IsRequired = false,
                InstallUrl = "https://nodejs.org/",
                DocsUrl = $"{DocsBase}#nodejs"
            },
        ];
    }

    /// <summary>
    /// The wizard's gate: may the user continue past the Prerequisites screen? One place decides,
    /// so the Next button and the screen's own subtitle can never disagree. On macOS nothing is
    /// required, so this is always true - which is correct, and is exactly why the subtitle must
    /// speak for itself about the recommended rows.
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
                case "Claude Code":
                    CheckExecutable(item, "claude", "--version");
                    break;
                case "Python":
                    CheckPython(item);
                    break;
                case "Node.js":
                    CheckNode(item);
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
        var whichCmd = IsWindows ? "where" : "which";
        var (found, _) = RunCommand(whichCmd, exe);
        if (!found)
        {
            item.Status = "Not found";
            item.IsFound = false;
            SetupLog.Write($"[PrerequisiteChecker] {item.Name}: not found on PATH");
            return;
        }

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

    private static void CheckPython(PrerequisiteInfo item)
    {
        // On macOS, try python3 first, then python
        var pythonCmd = "python";
        if (IsMacOS)
        {
            var (py3Found, _) = RunCommand("which", "python3");
            if (py3Found) pythonCmd = "python3";
        }

        var whichCmd = IsWindows ? "where" : "which";
        var (found, _2) = RunCommand(whichCmd, pythonCmd);
        if (!found)
        {
            item.Status = "Not found";
            item.IsFound = false;
            SetupLog.Write("[PrerequisiteChecker] Python: not found on PATH");
            return;
        }

        var (versionFound, versionOutput) = RunCommand(pythonCmd, "--version");
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
        var whichCmd = IsWindows ? "where" : "which";
        var (found, _) = RunCommand(whichCmd, "node");
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
    /// Builds the current machine+user PATH straight from the registry (Windows only).
    /// A process snapshots PATH at launch, so a tool installed after the setup app
    /// started would be invisible to its child checks until restart. Reading the
    /// User/Machine targets pulls the live value (with %VAR% expansion) instead.
    /// Returns null on non-Windows or when nothing could be read, leaving the
    /// inherited process PATH in place.
    /// </summary>
    private static string? BuildRefreshedPath()
    {
        if (!IsWindows)
            return null;

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

            // Re-read the live PATH from the registry so that tools added to PATH
            // AFTER this setup app launched (e.g. a just-installed Claude Code) are
            // visible to "where"/"which" on Re-check without restarting the app.
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
