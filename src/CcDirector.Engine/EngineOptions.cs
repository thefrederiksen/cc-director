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
    public string CcOutlookPath { get; set; } = Path.Combine(CcStorage.Bin(), "cc-outlook.exe");
    public string CcGmailPath { get; set; } = Path.Combine(CcStorage.Bin(), "cc-gmail.exe");

    /// <summary>
    /// List of send_from values that should use cc-gmail instead of cc-outlook.
    /// Values are matched case-insensitively. If send_from contains "@gmail.com",
    /// it is also automatically routed to cc-gmail.
    /// </summary>
    public List<string> GmailSendFromAccounts { get; set; } = new() { "personal" };

    /// <summary>
    /// Maps persona/send_from names to their tool_account name (e.g. "consulting" -> "consulting").
    /// Passed as --account flag to cc-gmail/cc-outlook so the correct account is used.
    /// </summary>
    public Dictionary<string, string?> ToolAccountMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public int DispatcherPollIntervalSeconds { get; set; } = 5;
}
