namespace CcDirector.Engine;

public sealed class EngineOptions
{
    public string DatabasePath { get; set; } = GetDefaultDatabasePath();
    public string LogDirectory { get; set; } = GetDefaultLogDirectory();
    public int CheckIntervalSeconds { get; set; } = 60;
    public int ShutdownTimeoutSeconds { get; set; } = 30;
    public int RunRetentionDays { get; set; } = 30;
    public string? CommunicationsDbPath { get; set; }
    public string CcOutlookPath { get; set; } = GetDefaultCcOutlookPath();
    public int DispatcherPollIntervalSeconds { get; set; } = 5;

    private static string GetDefaultDatabasePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "cc-myvault", "engine.db");
    }

    private static string GetDefaultLogDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "cc-myvault", "logs");
    }

    private static string GetDefaultCcOutlookPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "cc-tools", "bin", "cc-outlook.exe");
    }
}
