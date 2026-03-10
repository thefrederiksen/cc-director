using System.Runtime.InteropServices;

namespace CcDirectorSetup.Services;

public static class PathManager
{
    public static bool AddToPath(string directory)
    {
        SetupLog.Write($"[PathManager] AddToPath: directory={directory}");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return AddToPathWindows(directory);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return AddToPathMacOS(directory);

        SetupLog.Write("[PathManager] AddToPath: unsupported platform");
        return false;
    }

    private static bool AddToPathWindows(string directory)
    {
        try
        {
            // Read current user PATH from environment variable
            var currentPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
            var entries = currentPath.Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.Trim())
                .ToList();

            if (entries.Any(e => string.Equals(e, directory, StringComparison.OrdinalIgnoreCase)))
            {
                SetupLog.Write("[PathManager] AddToPath: already in PATH");
                return false;
            }

            entries.Add(directory);
            var newPath = string.Join(";", entries);
            Environment.SetEnvironmentVariable("PATH", newPath, EnvironmentVariableTarget.User);

            SetupLog.Write("[PathManager] AddToPath: success (Windows)");
            return true;
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[PathManager] AddToPath FAILED: {ex.Message}");
            return false;
        }
    }

    private static bool AddToPathMacOS(string directory)
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var zshrc = Path.Combine(home, ".zshrc");
            var exportLine = $"export PATH=\"$PATH:{directory}\"";

            // Check if already in .zshrc
            if (File.Exists(zshrc))
            {
                var content = File.ReadAllText(zshrc);
                if (content.Contains(directory))
                {
                    SetupLog.Write("[PathManager] AddToPath: already in .zshrc");
                    return false;
                }
            }

            // Append to .zshrc
            var comment = "# Added by CC Director Setup";
            File.AppendAllText(zshrc, $"\n{comment}\n{exportLine}\n");

            SetupLog.Write("[PathManager] AddToPath: success (macOS - added to .zshrc)");
            return true;
        }
        catch (Exception ex)
        {
            SetupLog.Write($"[PathManager] AddToPath FAILED: {ex.Message}");
            return false;
        }
    }
}
