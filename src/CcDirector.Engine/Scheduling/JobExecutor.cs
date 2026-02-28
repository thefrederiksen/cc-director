using CcDirector.Core.Utilities;
using CcDirector.Engine.Jobs;
using CcDirector.Engine.Storage;

namespace CcDirector.Engine.Scheduling;

public sealed class JobExecutor
{
    private readonly EngineDatabase _db;

    public JobExecutor(EngineDatabase db)
    {
        _db = db;
    }

    public async Task<RunRecord> ExecuteJobAsync(JobRecord job, CancellationToken cancellationToken)
    {
        FileLog.Write($"[JobExecutor] Starting job: id={job.Id}, name={job.Name}");

        var run = new RunRecord
        {
            JobId = job.Id,
            JobName = job.Name,
            StartedAt = DateTime.UtcNow
        };
        run.Id = _db.CreateRun(run);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var processJob = new ProcessJob(job.Name, job.Command, job.WorkingDir, job.TimeoutSeconds);
            var result = await processJob.ExecuteAsync(cancellationToken);

            stopwatch.Stop();

            run.EndedAt = DateTime.UtcNow;
            run.ExitCode = result.Success ? 0 : 1;
            run.Stdout = result.Output;
            run.Stderr = result.Error;
            run.TimedOut = result.Error?.StartsWith("Timed out") == true;
            run.DurationSeconds = stopwatch.Elapsed.TotalSeconds;

            _db.UpdateRun(run);

            FileLog.Write($"[JobExecutor] Job completed: name={job.Name}, success={result.Success}, duration={run.DurationSeconds:F1}s");
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            run.EndedAt = DateTime.UtcNow;
            run.ExitCode = -1;
            run.Stderr = "Cancelled";
            run.DurationSeconds = stopwatch.Elapsed.TotalSeconds;
            _db.UpdateRun(run);

            FileLog.Write($"[JobExecutor] Job cancelled: name={job.Name}");
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            run.EndedAt = DateTime.UtcNow;
            run.ExitCode = -1;
            run.Stderr = ex.Message;
            run.DurationSeconds = stopwatch.Elapsed.TotalSeconds;
            _db.UpdateRun(run);

            FileLog.Write($"[JobExecutor] Job FAILED: name={job.Name}, error={ex.Message}");
        }

        // Update next_run for the job
        var nextRun = CronHelper.GetNextOccurrence(job.Cron, DateTime.UtcNow);
        _db.UpdateNextRun(job.Id, nextRun);

        return run;
    }
}
