namespace CcDirector.Engine;

public sealed class EngineStatus
{
    public bool IsRunning { get; init; }
    public DateTime StartedAt { get; init; }
    public TimeSpan Uptime => IsRunning ? DateTime.UtcNow - StartedAt : TimeSpan.Zero;
    public int TotalJobs { get; init; }
    public int EnabledJobs { get; init; }
    public int RunningJobs { get; init; }
    public DateTime? NextScheduledRun { get; init; }
    public int RunsToday { get; init; }
    public int FailedRunsToday { get; init; }
}
