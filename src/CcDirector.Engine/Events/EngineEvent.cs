namespace CcDirector.Engine.Events;

public record EngineEvent(
    EngineEventType Type,
    string? JobName = null,
    int? RunId = null,
    string? Message = null,
    DateTime Timestamp = default
)
{
    public DateTime Timestamp { get; init; } = Timestamp == default ? DateTime.UtcNow : Timestamp;
}

public enum EngineEventType
{
    EngineStarted,
    EngineStopping,
    EngineStopped,
    JobStarted,
    JobCompleted,
    JobFailed,
    JobTimeout,
    CommunicationDispatched,
    Error
}
