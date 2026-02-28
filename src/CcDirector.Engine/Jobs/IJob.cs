namespace CcDirector.Engine.Jobs;

public interface IJob
{
    string Name { get; }
    Task<JobResult> ExecuteAsync(CancellationToken cancellationToken);
}

public record JobResult(
    bool Success,
    string Output,
    string? Error = null
);
