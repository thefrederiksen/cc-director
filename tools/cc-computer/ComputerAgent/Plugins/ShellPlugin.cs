using System.ComponentModel;
using System.Diagnostics;
using Microsoft.SemanticKernel;

namespace CCComputer.Agent.Plugins;

/// <summary>
/// Plugin for running shell commands and launching applications.
/// </summary>
public class ShellPlugin
{
    // Allowed applications that can be launched
    private static readonly HashSet<string> AllowedApplications = new(StringComparer.OrdinalIgnoreCase)
    {
        "notepad",
        "calc",
        "mspaint",
        "outlook",
        "winword",      // Word
        "excel",
        "powerpnt",     // PowerPoint
        "explorer",
        "msedge",
        "chrome",
        "firefox",
        "code",         // VS Code
        "devenv"        // Visual Studio
    };

    [KernelFunction("launch_application")]
    [Description("Launch a Windows application by name (e.g., 'notepad', 'outlook', 'excel', 'chrome').")]
    public async Task<string> LaunchApplicationAsync(
        [Description("Application name to launch (e.g., 'notepad', 'outlook', 'chrome')")] string applicationName)
    {
        // Normalize the application name
        var normalizedName = applicationName.ToLowerInvariant().Trim();

        // Remove common suffixes
        normalizedName = normalizedName
            .Replace(".exe", "")
            .Replace("microsoft ", "")
            .Replace("ms ", "");

        // Map common names to actual executables
        var executableName = normalizedName switch
        {
            "word" => "winword",
            "powerpoint" => "powerpnt",
            "visual studio code" or "vscode" => "code",
            "visual studio" => "devenv",
            "edge" => "msedge",
            "file explorer" or "files" => "explorer",
            "calculator" => "calc",
            "paint" => "mspaint",
            _ => normalizedName
        };

        if (!AllowedApplications.Contains(executableName))
        {
            return $"Error: Application '{applicationName}' is not in the allowed list. Allowed: {string.Join(", ", AllowedApplications)}";
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executableName,
                UseShellExecute = true
            };

            Process.Start(psi);

            // Wait a moment for the application to start
            await Task.Delay(500);

            return $"Launched {applicationName} successfully";
        }
        catch (Exception ex)
        {
            return $"Error launching {applicationName}: {ex.Message}";
        }
    }

    [KernelFunction("run_command")]
    [Description("Run a simple shell command. For safety, only specific commands are allowed.")]
    public async Task<string> RunCommandAsync(
        [Description("Command to run (limited to safe operations)")] string command)
    {
        // Only allow very specific safe commands
        var allowedCommands = new[]
        {
            "dir",
            "echo",
            "date /t",
            "time /t",
            "whoami",
            "hostname"
        };

        var normalizedCommand = command.ToLowerInvariant().Trim();

        if (!allowedCommands.Any(c => normalizedCommand.StartsWith(c)))
        {
            return $"Error: Command not allowed for safety reasons. Allowed commands: {string.Join(", ", allowedCommands)}";
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c {command}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = psi };
            process.Start();

            var output = await process.StandardOutput.ReadToEndAsync();
            var error = await process.StandardError.ReadToEndAsync();

            await process.WaitForExitAsync();

            if (!string.IsNullOrEmpty(error))
            {
                return $"Error: {error}";
            }

            return output.Trim();
        }
        catch (Exception ex)
        {
            return $"Error running command: {ex.Message}";
        }
    }
}
