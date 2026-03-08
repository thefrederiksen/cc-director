using System.Diagnostics;
using System.IO;

namespace CcDirectorSetup.Services;

public static class InstallDetector
{
    private static readonly string ExePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "cc-director", "bin", "cc-director.exe");

    public static bool IsInstalled() => File.Exists(ExePath);

    public static string? GetInstalledVersion()
    {
        if (!File.Exists(ExePath))
            return null;

        var info = FileVersionInfo.GetVersionInfo(ExePath);
        return info.ProductVersion;
    }
}
