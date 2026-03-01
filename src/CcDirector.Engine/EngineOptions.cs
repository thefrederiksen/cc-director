using CcDirector.Core.Storage;

namespace CcDirector.Engine;

public sealed class EngineOptions
{
    public string DatabasePath { get; set; } = CcStorage.EngineDb();
    public string LogDirectory { get; set; } = CcStorage.ToolLogs("engine");
    public int CheckIntervalSeconds { get; set; } = 60;
    public int ShutdownTimeoutSeconds { get; set; } = 30;
    public int RunRetentionDays { get; set; } = 30;
    public string? CommunicationsDbPath { get; set; }
    public string CcOutlookPath { get; set; } = Path.Combine(CcStorage.Bin(), "cc-outlook.exe");
    public int DispatcherPollIntervalSeconds { get; set; } = 5;
}
