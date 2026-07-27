namespace CcDirectorSetup.Services;

public static class SetupLog
{
    private static readonly string LogDir;
    private static readonly string LogPath;
    private static readonly object Lock = new();

    /// <summary>The current setup log file (shown on-screen so a user can find/attach it).</summary>
    public static string Path => LogPath;

    /// <summary>The setup log directory.</summary>
    public static string Dir => LogDir;

    static SetupLog()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        LogDir = System.IO.Path.Combine(localAppData, "cc-director", "logs", "setup");
        Directory.CreateDirectory(LogDir);
        LogPath = System.IO.Path.Combine(LogDir, $"setup-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (Lock)
        {
            // This log lives INSIDE the per-user root, and the uninstall's "also delete my data"
            // opt-in deletes that entire root - including this directory - and then keeps running.
            // The next write threw DirectoryNotFoundException, which escaped Uninstaller.Apply, was
            // caught by the uninstall screen, whose handler logged the failure and threw AGAIN on
            // the UI thread inside an async void. A successful uninstall took the wizard down.
            //
            // The directory is deliberately NOT recreated: the user asked for that data to be gone,
            // and re-making a folder underneath it would quietly defeat what they asked for. Once
            // the log is gone there is nowhere legitimate left to write, so the line is dropped.
            if (!Directory.Exists(LogDir))
                return;

            try
            {
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Includes the directory vanishing between the check above and the write. Logging
                // must never be the reason setup fails.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
