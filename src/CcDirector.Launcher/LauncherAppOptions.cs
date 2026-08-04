namespace CcDirector.Launcher;

/// <summary>
/// Process-wide options resolved from the command line in Program.Main before the
/// Avalonia app is constructed. A static holder is the clean way to hand parsed args
/// to the app (Avalonia instantiates the App class itself).
/// </summary>
public static class LauncherAppOptions
{
    /// <summary>When true, register the HKCU Run-key autostart entry on startup. --no-autostart disables.</summary>
    public static bool RegisterAutostart { get; set; } = true;

    /// <summary>
    /// Installed mode (--managed): run the periodic self-update check. Off by default so a dev launch
    /// never self-updates a repo build. The installer launches the shipped launcher with --managed.
    /// </summary>
    public static bool Managed { get; set; }

    /// <summary>The arguments equivalent to the current options, for the autostart Run key.</summary>
    public static string? AutostartArguments() => Managed ? "--managed" : null;

    /// <summary>Parse the supported flags: --no-autostart, --managed. Unknown flags are ignored, so an
    /// autostart entry written by an older build (which could carry --port) still starts this one.</summary>
    public static void Parse(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--no-autostart")
            {
                RegisterAutostart = false;
            }
            else if (args[i] == "--managed")
            {
                Managed = true;
            }
        }
    }
}
