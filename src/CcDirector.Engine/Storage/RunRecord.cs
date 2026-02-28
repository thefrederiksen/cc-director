namespace CcDirector.Engine.Storage;

public sealed class RunRecord
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public string JobName { get; set; } = string.Empty;
    public DateTime StartedAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? Stdout { get; set; }
    public string? Stderr { get; set; }
    public bool TimedOut { get; set; }
    public double? DurationSeconds { get; set; }
}
