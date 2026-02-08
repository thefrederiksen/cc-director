namespace CcDirector.Core.Configuration;

public class AgentOptions
{
    public string ClaudePath { get; set; } = "claude";
    public int DefaultBufferSizeBytes { get; set; } = 2_097_152; // 2 MB
    public int GracefulShutdownTimeoutSeconds { get; set; } = 5;
}
