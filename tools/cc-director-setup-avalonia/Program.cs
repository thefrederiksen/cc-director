using Avalonia;
using CcDirectorSetup.Services;

namespace CcDirectorSetup;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // A local directory acting as a full release (release-manifest.json + asset files), the
        // same override the setup command line offers. Lets the wizard run a complete install with
        // no network - hermetic testing of a not-yet-published release on real hardware. The
        // environment variable form exists because a Finder-launched .app receives no arguments.
        var releaseDir = ParseOption(args, "--release-dir")
            ?? Environment.GetEnvironmentVariable("DEVTHROTTLE_RELEASE_DIR");
        if (!string.IsNullOrWhiteSpace(releaseDir))
            EngineInstallRunner.ReleaseDirectoryOverride = releaseDir;

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static string? ParseOption(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
