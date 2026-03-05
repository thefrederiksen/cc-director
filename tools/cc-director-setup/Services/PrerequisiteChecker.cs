using System.Diagnostics;
using CcDirectorSetup.Models;

namespace CcDirectorSetup.Services;

public static class PrerequisiteChecker
{
    public static List<PrerequisiteInfo> CreateChecklist()
    {
        return
        [
            new PrerequisiteInfo
            {
                Name = "Claude Code",
                Description = "AI coding assistant CLI",
                IsRequired = true,
                InstallUrl = "https://docs.anthropic.com/en/docs/claude-code/overview"
            },
            new PrerequisiteInfo
            {
                Name = "Python",
                Description = "Python 3.11 or higher",
                IsRequired = true,
                InstallUrl = "https://www.python.org/downloads/"
            },
            new PrerequisiteInfo
            {
                Name = "Node.js",
                Description = "Node.js 20+ (MCP servers, browser tools)",
                IsRequired = true,
                InstallUrl = "https://nodejs.org/"
            },
            new PrerequisiteInfo
            {
                Name = "Brave Browser",
                Description = "Browser engine for cc-browser (Chrome stable blocks extensions)",
                IsRequired = true,
                InstallUrl = "https://brave.com/download/"
            },
        ];
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
                case "Brave Browser":
                    CheckBrave(item);
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

    private static readonly string[] BravePaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
    ];

    private static void CheckBrave(PrerequisiteInfo item)
    {
        foreach (var path in BravePaths)
        {
            if (File.Exists(path))
            {
                try
                {
                    var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                    item.Version = $"Brave {versionInfo.ProductVersion}";
                    item.Status = "Found";
                    item.IsFound = true;
                    SetupLog.Write($"[PrerequisiteChecker] Brave: found at {path}, version={item.Version}");
                    return;
                }
                catch
                {
                    item.Version = "";
                    item.Status = "Found";
                    item.IsFound = true;
                    SetupLog.Write($"[PrerequisiteChecker] Brave: found at {path}, no version info");
                    return;
                }
            }
        }

        item.Status = "Not found";
        item.IsFound = false;
        SetupLog.Write("[PrerequisiteChecker] Brave: not found at any known location");
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
