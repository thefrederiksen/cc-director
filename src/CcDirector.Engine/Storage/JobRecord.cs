namespace CcDirector.Engine.Storage;

public sealed class JobRecord
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Cron { get; set; } = string.Empty;
    public string Command { get; set; } = string.Empty;
    public string? WorkingDir { get; set; }
    public bool Enabled { get; set; } = true;
    public int TimeoutSeconds { get; set; } = 300;
    public string? Tags { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? NextRun { get; set; }
}
