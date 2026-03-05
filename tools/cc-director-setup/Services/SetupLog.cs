namespace CcDirectorSetup.Services;

public static class SetupLog
{
    private static readonly string LogDir;
    private static readonly string LogPath;
    private static readonly object Lock = new();

    static SetupLog()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        LogDir = Path.Combine(localAppData, "cc-director", "logs", "setup");
        Directory.CreateDirectory(LogDir);
        LogPath = Path.Combine(LogDir, $"setup-{DateTime.Now:yyyyMMdd-HHmmss}.log");
    }

    public static void Write(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        lock (Lock)
        {
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
    }
}
