using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Running;

/// <summary>
/// Production <see cref="ICronWorkListRunner"/> (epic #479, #484): when a cron job's action names a
/// work list, this triggers the shipped #274 drain on the job's target Director. It mirrors the
/// <c>POST /lists/{name}/run</c> endpoint's guards - synchronous pre-checks (no list / empty /
/// already-claimed / no director / machine busy) return a clean outcome and never launch a drain -
/// then launches the actual drain in the BACKGROUND (a cron sweep must not block for the hours a
/// drain can take), releasing the machine's single-drain slot when it finishes.
/// </summary>
public sealed class DirectorCronWorkListRunner : ICronWorkListRunner
{
    private readonly WorkListStore _store;
    private readonly IDirectorTargetResolver _resolver;
    private readonly WorkListRunnerManager _manager;
    private readonly ICronWorkListDrainLauncher _launcher;
    private readonly Func<TenantId?> _resolveTenant;

    /// <param name="resolveTenant">
    /// Resolves the tenant of the current cron fire - the same background-loop/request seam the engine keys its
    /// overlap guard by (audit MED, gap audit-e). The machine drain slot is partitioned by this tenant so one
    /// tenant's drain never refuses another tenant's drain on a shared machine key. Omitted (self-host wiring,
    /// tests) it is <see cref="TenantId.Local"/> - one partition, unchanged. A fire is only ever reached inside
    /// a resolved scope, so an unresolved tenant here is a boundary bug and fails loud rather than defaulting.
    /// </param>
    public DirectorCronWorkListRunner(
        WorkListStore store,
        IDirectorTargetResolver resolver,
        WorkListRunnerManager manager,
        ICronWorkListDrainLauncher launcher,
        Func<TenantId?>? resolveTenant = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _resolveTenant = resolveTenant ?? (() => TenantId.Local);
    }

    public async Task<CronWorkListOutcome> TriggerAsync(CronJobDto job, CancellationToken ct)
    {
        if (job is null)
            throw new ArgumentNullException(nameof(job));
        var listName = job.Action.WorkListName;
        if (string.IsNullOrWhiteSpace(listName))
            throw new ArgumentException("job is not a work-list action", nameof(job));

        // Synchronous pre-checks (AC3): a clean outcome, no drain launched, no duplicate claim.
        var list = _store.Get(listName);
        if (list is null)
        {
            FileLog.Write($"[DirectorCronWorkListRunner] job={job.Id}: no such list={listName}");
            return CronWorkListOutcome.NoSuchList;
        }
        if (list.Items.Count == 0)
        {
            FileLog.Write($"[DirectorCronWorkListRunner] job={job.Id}: list={listName} is empty; nothing to drain");
            return CronWorkListOutcome.EmptyList;
        }
        if (!string.IsNullOrEmpty(list.Consumer))
        {
            FileLog.Write($"[DirectorCronWorkListRunner] job={job.Id}: list={listName} already claimed by {list.Consumer}");
            return CronWorkListOutcome.AlreadyClaimed;
        }

        // Resolve the target MACHINE to a Director (launching one if none is running, #503). A cron job
        // targets a machine and no particular Director, so it names none and keeps the launch-on-demand
        // behavior it has always had.
        var machine = job.Target.Machine;
        var target = await _resolver.ResolveAsync(machine, director: null, ct);
        // Tunnel-only: a resolved DirectorId is the target; a blank control endpoint is still reachable
        // over the tunnel. Fail only on the resolver's error or a missing DirectorId, NOT on an endpoint
        // (the same stale REST-era guard issue #1727 removed from the machine-session spawn path).
        if (!string.IsNullOrEmpty(target.Error) || string.IsNullOrEmpty(target.DirectorId))
        {
            FileLog.Write($"[DirectorCronWorkListRunner] job={job.Id}: no director on machine={machine}: {target.Error}");
            return CronWorkListOutcome.NoSuchDirector;
        }
        var directorId = target.DirectorId;

        // Partition the machine drain slot by the tenant of this fire (audit MED): a caller-controlled machine
        // key shared across tenants must not let one tenant's drain refuse another's. Resolved once here and
        // used for admit AND the finally-release below, so the release always matches the admit.
        var tenant = _resolveTenant()
            ?? throw new InvalidOperationException(
                "A cron work-list drain ran with no tenant scope in effect. The machine drain slot is " +
                "partitioned by tenant; the caller path was not bound at its tenant boundary.");

        var machineKey = string.IsNullOrWhiteSpace(machine) ? directorId : machine;
        if (_manager.TryAdmit(tenant, machineKey, listName) == WorkListRunnerManager.AdmitResult.RefusedMachineBusy)
        {
            FileLog.Write($"[DirectorCronWorkListRunner] job={job.Id}: tenant={tenant.ToLogString()} machine={machineKey} busy (active={_manager.ActiveList(tenant, machineKey)})");
            return CronWorkListOutcome.MachineBusy;
        }

        // Admitted: launch the drain in the background so the cron sweep is not blocked for the
        // hours a drain can take. The machine slot is released when the drain finishes.
        var consumer = $"cron:{job.Id}:{Guid.NewGuid():N}";
        var repoPath = job.Action.RepoPath;
        FileLog.Write($"[DirectorCronWorkListRunner] job={job.Id}: launching drain list={listName} on machine={machineKey}, consumer={consumer}");

        _ = Task.Run(async () =>
        {
            // Detached background task: it owns its try/catch so a drain failure is logged, never
            // unobserved, and the machine slot is always released.
            try
            {
                // The launcher's endpoint parameter is a separate pre-existing vestige, ignored post-cut
                // (tunnel-only): delivery is by directorId. Pass empty; retiring that parameter chain is a
                // follow-up beyond issue #1727's scope.
                await _launcher.LaunchAsync(directorId, string.Empty, repoPath, listName, consumer, ct);
                FileLog.Write($"[DirectorCronWorkListRunner] job={job.Id}: drain complete list={listName}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorCronWorkListRunner] job={job.Id}: drain FAILED list={listName}: {ex.Message}");
            }
            finally
            {
                _manager.Complete(tenant, machineKey);
            }
        }, CancellationToken.None);

        return CronWorkListOutcome.Started;
    }
}
