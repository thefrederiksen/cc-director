using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Running;

/// <summary>
/// Production <see cref="ICronSessionStarter"/> (epic #479, #483, #503). Builds the cron job's
/// session request (the ONLY cron-specific work here) and hands it to the shared
/// <see cref="MachineSessionSpawner"/>, which resolves the target MACHINE to a Director (launching one
/// if none is running) and starts the session over the Gateway's existing session-create path. Cron
/// and the interactive POST /machines/{machine}/sessions relay therefore share ONE resolve-then-create
/// method; no new Director surface is introduced, and an unresolvable target is reported as an error,
/// not thrown.
/// </summary>
public sealed class DirectorCronSessionStarter : ICronSessionStarter
{
    private readonly MachineSessionSpawner _spawner;

    public DirectorCronSessionStarter(MachineSessionSpawner spawner)
    {
        _spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
    }

    public async Task<(string? sessionId, string? directorId, string? error)> StartAsync(CronJobDto job, CancellationToken ct)
    {
        if (job is null)
            throw new ArgumentNullException(nameof(job));

        var req = new NewSessionRequest
        {
            RepoPath = job.Action.RepoPath,
            Agent = "ClaudeCode",
            PrePrompt = job.Action.Seed,
            // Scheduled-run auto-dismiss (issue #1200): a seed run defaults to closing itself when it
            // finishes with nothing needing a human, so an hourly job stops piling leftover sessions into
            // the rail. The job can opt out (AutoDismiss=false) to keep the run open like a normal session.
            AutoDismiss = job.Action.AutoDismiss,
        };

        FileLog.Write($"[DirectorCronSessionStarter] start: job={job.Id}, machine={job.Target.Machine}, repo={job.Action.RepoPath}, seed={job.Action.Seed}");

        var (ok, dto, error, directorId) = await _spawner.SpawnOnMachineAsync(job.Target.Machine, req, ct);
        if (!ok || dto is null || string.IsNullOrEmpty(dto.SessionId))
        {
            FileLog.Write($"[DirectorCronSessionStarter] start FAILED: job={job.Id}, machine={job.Target.Machine}, {error}");
            return (null, directorId, error ?? "could not start a session on the target machine");
        }

        FileLog.Write($"[DirectorCronSessionStarter] started: job={job.Id}, sid={dto.SessionId}, director={directorId}");
        return (dto.SessionId, directorId, null);
    }
}
