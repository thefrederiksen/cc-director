using CcDirector.Core.Storage;

namespace CcDirector.Engine;

public sealed class EngineOptions
{
    public string DatabasePath { get; set; } = CcStorage.EngineDb();
    public string LogDirectory { get; set; } = CcStorage.ToolLogs("engine");
    public int CheckIntervalSeconds { get; set; } = 60;
    public int ShutdownTimeoutSeconds { get; set; } = 30;
    public int RunRetentionDays { get; set; } = 30;
    public string CommunicationsDbPath { get; set; } = CcStorage.CommQueueDb();

    /// <summary>
    /// List of email tool names to discover accounts from at startup.
    /// Each tool must support: {tool} accounts list --json
    /// </summary>
    public List<string> EmailToolNames { get; set; } = new() { "cc-gmail", "cc-outlook" };

    /// <summary>
    /// Directory where tool executables are installed.
    /// </summary>
    public string BinDirectory { get; set; } = CcStorage.Bin();

    public int DispatcherPollIntervalSeconds { get; set; } = 5;
}
