using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CcDirectorSetup.Services;

public static class InstallDetector
{
    private static readonly string ExePath = GetExePath();

    private static string GetExePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var binName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "cc-director.exe"
            : "cc-director";
        return Path.Combine(localAppData, "cc-director", "bin", binName);
    }

    public static bool IsInstalled() => File.Exists(ExePath);

    public static string? GetInstalledVersion()
    {
        if (!File.Exists(ExePath))
            return null;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var info = FileVersionInfo.GetVersionInfo(ExePath);
            return info.ProductVersion;
        }

        // On macOS, try running --version to get the version
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(psi);
            if (process == null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output.Trim();
        }
        catch
        {
            return null;
        }
    }
}
