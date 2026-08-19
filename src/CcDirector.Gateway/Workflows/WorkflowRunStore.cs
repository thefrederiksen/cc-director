using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Workflows;

/// <summary>
/// The Gateway's workflow-run store (Workflows mission, phase 4 - the governance outcome spine,
/// issue #1771). A run is one execution of a workflow definition: it pins the exact published
/// version at creation, seeds its criteria results from that version's declared outcome criteria,
/// records participants (persisted run-to-session membership), and keeps lifecycle and acceptance
/// as two separate questions. Today's missions become runs of the built-in "mission" workflow via
/// the Gateway's mission-create path.
///
/// Lifecycle transitions are enforced here, in one place: created can start or be abandoned; an
/// active run can wait on a human and come back; any live run can end succeeded, failed, or
/// abandoned; a terminal status is FINAL. Acceptance never gates lifecycle and lifecycle never
/// implies acceptance.
///
/// THE SHARED WORKFLOW LIBRARY (devthrottle_internal issue 514, phase 1): the WORKFLOW a run
/// executes may be a built-in living in the shared library partition (System on hosted), while the
/// RUN itself is tenant data and always lands in the ambient tenant's partition. Workflow lookups
/// here therefore resolve the owning partition exactly as <see cref="WorkflowStore"/> does (the
/// library wins by id), which is what lets a hosted tenant seat sessions on the built-in "mission"
/// conduct. Library reads are read-only - this store never writes a workflow row anywhere.
///
/// Threading: the Gateway is a single writer. Every operation runs under this store's write lock
/// over a fresh pooled context.
/// </summary>
public sealed class WorkflowRunStore
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    /// <summary>The shared library partition - the tenant ambient at construction (System on hosted,
    /// Local on self-host), mirroring <see cref="WorkflowStore"/>.</summary>
    // Captured by InitializeCore, which may run after construction (the Gateway defers it so its listener
    // can bind first), so this cannot be readonly. It is still written EXACTLY ONCE: InitializeCore is
    // guarded by _initialized and never runs twice. Empty until then, and nothing reads it before the
    // readiness gate opens.
    private string _libraryTenant = "";

    private static readonly Dictionary<string, string[]> LegalTransitions = new(StringComparer.Ordinal)
    {
        [WorkflowRunStatus.Created] = new[] { WorkflowRunStatus.Active, WorkflowRunStatus.Abandoned },
        [WorkflowRunStatus.Active] = new[]
        {
            WorkflowRunStatus.AwaitingHuman, WorkflowRunStatus.Succeeded,
            WorkflowRunStatus.Failed, WorkflowRunStatus.Abandoned,
        },
        [WorkflowRunStatus.AwaitingHuman] = new[]
        {
            WorkflowRunStatus.Active, WorkflowRunStatus.Succeeded,
            WorkflowRunStatus.Failed, WorkflowRunStatus.Abandoned,
        },
        [WorkflowRunStatus.Succeeded] = Array.Empty<string>(),
        [WorkflowRunStatus.Failed] = Array.Empty<string>(),
        [WorkflowRunStatus.Abandoned] = Array.Empty<string>(),
    };

    private static readonly string[] AcceptanceStatuses =
    {
        WorkflowRunAcceptance.Pending, WorkflowRunAcceptance.Accepted,
        WorkflowRunAcceptance.Rejected, WorkflowRunAcceptance.Waived,
    };

    private static readonly string[] CriterionStatuses =
    {
        WorkflowRunCriterionStatus.Pending, WorkflowRunCriterionStatus.Met,
        WorkflowRunCriterionStatus.NotMet, WorkflowRunCriterionStatus.Waived,
    };
    /// <param name="deferInitialize">
    /// When true the constructor validates arguments and stops; the caller must call
    /// <see cref="Initialize"/> once the database is open. The Gateway passes true so its listener can bind
    /// BEFORE any database work - the load below used to sit in front of the bind, and a slow database
    /// therefore delayed it past the platform's container-start deadline (#2383, #2585).
    ///
    /// The caller MUST run Initialize inside the same ambient tenant scope the constructor would have had.
    /// Nothing is served in the meantime: the readiness gate refuses every request but /healthz.
    /// </param>
    public WorkflowRunStore(GatewayDatabase db, bool deferInitialize = false)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));

        // Capture the library partition from the construction-time ambient tenant, exactly as
        // WorkflowStore does (the composition root constructs both inside the startup System scope).
        if (!deferInitialize)
            InitializeCore();
    }

    /// <summary>
    /// Run the deferred load. Idempotent, and a no-op for an instance whose constructor already did it.
    /// </summary>
    public void Initialize()
    {
        if (_initialized) return;
        InitializeCore();
    }

    /// <summary>True once the load has run.</summary>
    public bool IsInitialized => _initialized;

    private bool _initialized;

    private void InitializeCore()
    {
        using var ctx = _db.CreateContext();
        _libraryTenant = ctx.ActiveTenant!;
        _initialized = true;
    }


    /// <summary>
    /// Open the context for the partition that OWNS workflow <paramref name="key"/>: the shared
    /// library when the id is a built-in there (the library wins by id), otherwise the ambient
    /// tenant's partition. On self-host both are the same partition. Caller disposes.
    /// </summary>
    private GatewayDbContext OpenOwningWorkflowContext(string key)
    {
        var library = _db.CreateContext(new TenantId(_libraryTenant));
        if (library.Workflows.AsNoTracking().Any(h => h.Id == key && h.IsBuiltIn))
            return library;
        library.Dispose();
        return _db.CreateContext();
    }

    /// <summary>The ambient tenant's on/off choice for a built-in (phase 2), or null when none.</summary>
    private bool? TenantChoiceFor(string key)
    {
        using var ctx = _db.CreateContext();
        return ctx.WorkflowTenantOverrides.AsNoTracking()
            .Where(o => o.WorkflowId == key)
            .Select(o => (bool?)o.Enabled)
            .FirstOrDefault();
    }

    /// <summary>A head's enabled state as THIS tenant sees it: for a built-in the tenant's own
    /// choice wins over the library's state; a user-owned head keeps its head-row switch.</summary>
    private bool EffectiveEnabled(WorkflowEntity head) =>
        head.IsBuiltIn ? TenantChoiceFor(head.Id) ?? head.Enabled : head.Enabled;

    /// <summary>
    /// Open a run of a workflow, pinned to its currently published version, with criteria seeded
    /// pending from that version's declared outcome criteria.
    /// </summary>
    /// <exception cref="WorkflowValidationException">The workflow is missing, archived, or has no
    /// published version - a run cannot execute conduct that is not fleet-readable.</exception>
    public WorkflowRunDto Create(
        string workflowId, string name, Guid? missionId = null, Guid? parentRunId = null,
        string? repoPath = null)
    {
        if (string.IsNullOrWhiteSpace(workflowId))
            throw new WorkflowValidationException("A run needs a workflow id.");
        if (string.IsNullOrWhiteSpace(name))
            throw new WorkflowValidationException("A run needs a name.");
        var key = workflowId.Trim().ToLowerInvariant();

        lock (_gate)
        {
            // The WORKFLOW may live in the shared library partition; the RUN always lands in the
            // ambient tenant's partition. Two contexts, deliberately: the workflow read resolves the
            // owning partition, the run write stays tenant data.
            WorkflowVersionEntity version;
            using (var wfCtx = OpenOwningWorkflowContext(key))
            {
                var head = wfCtx.Workflows.AsNoTracking().FirstOrDefault(h => h.Id == key);
                if (head is null || head.Archived || head.PublishedVersion is null)
                    throw new WorkflowValidationException(
                        $"Workflow '{key}' has no published version to run.");
                if (!EffectiveEnabled(head))
                    throw new WorkflowValidationException(
                        $"Workflow '{key}' is turned OFF on this fleet - no new runs while the owner has " +
                        "it disabled. Re-enable it in the cockpit or with: cc-devthrottle workflow enable " + key);
                version = wfCtx.WorkflowVersions.AsNoTracking().First(
                    v => v.WorkflowId == key && v.Status == WorkflowVersionStatus.Published);
            }

            using var ctx = _db.CreateContext();
            var now = DateTime.UtcNow;
            var entity = new WorkflowRunEntity
            {
                TenantId = ctx.ActiveTenant!,
                WorkflowId = key,
                WorkflowVersionId = version.Id,
                WorkflowVersion = version.Version,
                ContentHash = version.ContentHash,
                Name = name.Trim(),
                Status = WorkflowRunStatus.Created,
                AcceptanceStatus = WorkflowRunAcceptance.Pending,
                CriteriaResults = version.OutcomeCriteria.Select(c => new WorkflowRunCriterionResultDto
                {
                    CriterionId = c.CriterionId,
                    Status = WorkflowRunCriterionStatus.Pending,
                }).ToList(),
                MissionId = missionId,
                ParentRunId = parentRunId,
                RepoPath = repoPath,
                CreatedUtc = now,
            };
            ctx.WorkflowRuns.Add(entity);
            ctx.SaveChanges();

            FileLog.Write($"[WorkflowRunStore] Create: run={entity.Id}, workflow={key} v{version.Version}, " +
                          $"name=\"{entity.Name}\", mission={missionId?.ToString() ?? "-"}");
            return ToDto(entity, workflowEnabled: true);
        }
    }

    /// <summary>One run by id, or null.</summary>
    public WorkflowRunDto? Get(Guid id)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.WorkflowRuns.AsNoTracking().FirstOrDefault(r => r.Id == id);
            return entity is null ? null : ToDto(entity, WorkflowEnabledFor(entity.WorkflowId));
        }
    }

    /// <summary>The hard ceiling on one list read; the default page is 200. Runs accumulate forever
    /// (they are never hard-deleted - the reporting horizon depends on it), so an unbounded list
    /// would eventually materialize the whole history per request.</summary>
    public const int MaxListLimit = 1000;

    /// <summary>Runs, newest first, optionally filtered by workflow, lifecycle status, or mission.
    /// Ordered and bounded in the database.</summary>
    public IReadOnlyList<WorkflowRunDto> List(
        string? workflowId = null, string? status = null, Guid? missionId = null, int limit = 200)
    {
        var take = Math.Clamp(limit, 1, MaxListLimit);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            IQueryable<WorkflowRunEntity> query = ctx.WorkflowRuns.AsNoTracking();
            if (!string.IsNullOrWhiteSpace(workflowId))
            {
                var key = workflowId.Trim().ToLowerInvariant();
                query = query.Where(r => r.WorkflowId == key);
            }
            if (!string.IsNullOrWhiteSpace(status))
            {
                var wanted = status.Trim().ToLowerInvariant();
                query = query.Where(r => r.Status == wanted);
            }
            if (missionId.HasValue)
                query = query.Where(r => r.MissionId == missionId.Value);

            var page = query
                .OrderByDescending(r => r.CreatedUtc)
                .Take(take)
                .ToList();
            var enabledByWorkflow = WorkflowEnabledMap(page.Select(r => r.WorkflowId));
            return page
                .Select(r => ToDto(r, enabledByWorkflow.GetValueOrDefault(r.WorkflowId, false)))
                .ToList();
        }
    }

    /// <summary>
    /// Throws the same validation error <see cref="Create"/> would when the workflow cannot be run
    /// (missing, archived, or never published), WITHOUT creating anything. The mission-create path
    /// checks this BEFORE writing the Mission record, so the expected failure mode cannot leave a
    /// mission behind with no governance run.
    /// </summary>
    public void EnsureRunnable(string workflowId)
    {
        if (string.IsNullOrWhiteSpace(workflowId))
            throw new WorkflowValidationException("A run needs a workflow id.");
        var key = workflowId.Trim().ToLowerInvariant();
        lock (_gate)
        {
            using var ctx = OpenOwningWorkflowContext(key);
            var head = ctx.Workflows.AsNoTracking().FirstOrDefault(h => h.Id == key);
            if (head is null || head.Archived || head.PublishedVersion is null)
                throw new WorkflowValidationException(
                    $"Workflow '{key}' has no published version to run.");
            if (!EffectiveEnabled(head))
                throw new WorkflowValidationException(
                    $"Workflow '{key}' is turned OFF on this fleet - no new runs while the owner has " +
                    "it disabled.");
        }
    }

    /// <summary>
    /// Whether a workflow is currently in force (the owner's switch). The mission-create path asks
    /// THIS before <see cref="EnsureRunnable"/>: per the owner ruling, a mission whose workflow is
    /// off still gets created - it simply runs ungoverned (no run record) until the switch flips
    /// back, which is a different answer than the hard refusal an unrunnable workflow gets.
    /// </summary>
    public bool IsWorkflowEnabled(string workflowId) => GetWorkflowEnabled(workflowId) == true;

    /// <summary>
    /// Three-valued on purpose: TRUE = in force, FALSE = the owner explicitly turned it off, NULL =
    /// the workflow does not exist (or is archived). The mission-create path needs the distinction,
    /// because only the owner's explicit OFF may produce an ungoverned mission - a MISSING mission
    /// workflow is a broken store and must keep failing loud, never be mistaken for a choice.
    /// </summary>
    public bool? GetWorkflowEnabled(string workflowId)
    {
        if (string.IsNullOrWhiteSpace(workflowId))
            return null;
        var key = workflowId.Trim().ToLowerInvariant();
        lock (_gate)
        {
            using var ctx = OpenOwningWorkflowContext(key);
            var head = ctx.Workflows.AsNoTracking().FirstOrDefault(h => h.Id == key && !h.Archived);
            return head is null ? null : EffectiveEnabled(head);
        }
    }

    private bool WorkflowEnabledFor(string workflowId)
    {
        using var ctx = OpenOwningWorkflowContext(workflowId);
        var head = ctx.Workflows.AsNoTracking().FirstOrDefault(h => h.Id == workflowId && !h.Archived);
        return head is not null && EffectiveEnabled(head);
    }

    /// <summary>Enabled flags for a page of runs' workflow ids: the ambient tenant's own heads first,
    /// then the shared library's built-ins OVERRIDE by id (the library wins, matching the id-scoped
    /// resolution everywhere else). On self-host both reads hit the same partition and agree.</summary>
    private Dictionary<string, bool> WorkflowEnabledMap(IEnumerable<string> workflowIds)
    {
        var ids = workflowIds.Distinct(StringComparer.Ordinal).ToList();
        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        using (var ctx = _db.CreateContext())
        {
            foreach (var h in ctx.Workflows.AsNoTracking()
                         .Where(h => ids.Contains(h.Id) && !h.Archived)
                         .Select(h => new { h.Id, h.Enabled })
                         .ToList())
                map[h.Id] = h.Enabled;
        }
        var builtInIds = new List<string>();
        using (var library = _db.CreateContext(new TenantId(_libraryTenant)))
        {
            foreach (var h in library.Workflows.AsNoTracking()
                         .Where(h => ids.Contains(h.Id) && h.IsBuiltIn && !h.Archived)
                         .Select(h => new { h.Id, h.Enabled })
                         .ToList())
            {
                map[h.Id] = h.Enabled;
                builtInIds.Add(h.Id);
            }
        }
        // Fold the ambient tenant's own choices over the library state for the built-ins (phase 2).
        if (builtInIds.Count > 0)
        {
            using var ctx = _db.CreateContext();
            foreach (var choice in ctx.WorkflowTenantOverrides.AsNoTracking()
                         .Where(o => builtInIds.Contains(o.WorkflowId))
                         .ToList())
                map[choice.WorkflowId] = choice.Enabled;
        }
        return map;
    }

    /// <summary>
    /// Apply a patch: lifecycle transition (legal moves only; terminal is final), outcome text,
    /// acceptance, criterion results (ids must exist on the run), proof links, and participant
    /// joins/leaves. Null when no such run exists.
    /// </summary>
    public WorkflowRunDto? Patch(Guid id, PatchWorkflowRunRequest patch)
    {
        if (patch is null)
            throw new WorkflowValidationException("A patch body is required.");

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.WorkflowRuns.FirstOrDefault(r => r.Id == id);
            if (entity is null)
                return null;

            var now = DateTime.UtcNow;

            if (!string.IsNullOrWhiteSpace(patch.Status))
                ApplyStatus(entity, patch.Status.Trim().ToLowerInvariant(), now);

            if (patch.Outcome is not null)
                entity.Outcome = patch.Outcome;

            if (!string.IsNullOrWhiteSpace(patch.AcceptanceStatus))
                ApplyAcceptance(entity, patch.AcceptanceStatus.Trim().ToLowerInvariant(),
                    patch.AcceptedBy, now);

            if (patch.Criteria is not null)
                ApplyCriteria(entity, patch.Criteria, now);

            if (patch.ProofLinks is not null)
            {
                foreach (var link in patch.ProofLinks)
                {
                    if (link is null || string.IsNullOrWhiteSpace(link.Url))
                        throw new WorkflowValidationException("A proof link needs a url.");
                    entity.ProofLinks.Add(new WorkflowRunProofLinkDto
                    {
                        Label = link.Label ?? "",
                        Url = link.Url,
                    });
                }
            }

            if (patch.AddParticipants is not null)
                ApplyJoins(entity, patch.AddParticipants, now);

            if (patch.LeaveSessionIds is not null)
            {
                foreach (var sessionId in patch.LeaveSessionIds)
                {
                    var active = entity.Participants.FirstOrDefault(
                        p => p.SessionId == sessionId && p.LeftUtc is null);
                    if (active is not null)
                        active.LeftUtc = now;
                }
            }

            ctx.SaveChanges();
            FileLog.Write($"[WorkflowRunStore] Patch: run={id}, status={entity.Status}, " +
                          $"acceptance={entity.AcceptanceStatus}");
            return ToDto(entity, WorkflowEnabledFor(entity.WorkflowId));
        }
    }

    private static void ApplyStatus(WorkflowRunEntity entity, string next, DateTime now)
    {
        if (!LegalTransitions.TryGetValue(next, out _))
            throw new WorkflowValidationException(
                $"'{next}' is not a run status. Legal values: {string.Join(", ", LegalTransitions.Keys)}.");
        if (string.Equals(entity.Status, next, StringComparison.Ordinal))
            return; // idempotent no-move
        var allowed = LegalTransitions[entity.Status];
        if (!allowed.Contains(next, StringComparer.Ordinal))
            throw new WorkflowValidationException(
                $"A run cannot move from '{entity.Status}' to '{next}'" +
                (allowed.Length == 0
                    ? " - a terminal status is final."
                    : $". Legal moves: {string.Join(", ", allowed)}."));

        entity.Status = next;
        if (next == WorkflowRunStatus.Active && entity.StartedUtc is null)
            entity.StartedUtc = now;
        if (WorkflowRunStatus.Terminal.Contains(next, StringComparer.Ordinal))
            entity.CompletedUtc = now;
    }

    private static void ApplyAcceptance(
        WorkflowRunEntity entity, string acceptance, string? acceptedBy, DateTime now)
    {
        if (!AcceptanceStatuses.Contains(acceptance, StringComparer.Ordinal))
            throw new WorkflowValidationException(
                $"'{acceptance}' is not an acceptance status. Legal values: " +
                string.Join(", ", AcceptanceStatuses) + ".");
        if (acceptance == WorkflowRunAcceptance.Pending)
        {
            entity.AcceptanceStatus = acceptance;
            entity.AcceptedBy = null;
            entity.AcceptedUtc = null;
            return;
        }

        // A non-pending acceptance is a RULING, and a ruling has a ruler: the accepter identity is
        // what the verified-yield metric audits (issue #1771), so it must be EXPLICIT on every
        // decision - never inherited from a previous acceptance, or a later rejection would be
        // silently attributed to whoever accepted earlier.
        if (string.IsNullOrWhiteSpace(acceptedBy))
            throw new WorkflowValidationException(
                $"Setting acceptance to '{acceptance}' requires acceptedBy - who made the call.");
        entity.AcceptanceStatus = acceptance;
        entity.AcceptedBy = acceptedBy;
        entity.AcceptedUtc = now;
    }

    private static void ApplyCriteria(
        WorkflowRunEntity entity, List<WorkflowRunCriterionUpdateDto> updates, DateTime now)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var update in updates)
        {
            if (update is null || string.IsNullOrWhiteSpace(update.CriterionId))
                throw new WorkflowValidationException("A criterion update needs a criterionId.");
            if (!seen.Add(update.CriterionId))
                throw new WorkflowValidationException(
                    $"Criterion '{update.CriterionId}' appears more than once in one patch - the " +
                    "result would be an inconsistent blend of the two updates.");
            var existing = entity.CriteriaResults.FirstOrDefault(
                c => string.Equals(c.CriterionId, update.CriterionId, StringComparison.Ordinal));
            if (existing is null)
                throw new WorkflowValidationException(
                    $"This run has no criterion '{update.CriterionId}' - criteria come from the " +
                    "pinned workflow version and cannot be invented on the run.");
            // Null means UNCHANGED (the update type has no defaults on purpose): a patch adding only
            // a note to a met criterion never resets its status.
            if (update.Status is not null)
            {
                var status = update.Status.Trim().ToLowerInvariant();
                if (!CriterionStatuses.Contains(status, StringComparer.Ordinal))
                    throw new WorkflowValidationException(
                        $"'{update.Status}' is not a criterion status. Legal values: " +
                        string.Join(", ", CriterionStatuses) + ".");
                existing.Status = status;
            }
            if (update.ProofUrl is not null) existing.ProofUrl = update.ProofUrl;
            if (update.Note is not null) existing.Note = update.Note;
            if (update.Evaluator is not null) existing.Evaluator = update.Evaluator;
            // Server-stamped: the evaluation time is when the Gateway applied it, never caller-supplied.
            existing.EvaluatedUtc = now;
        }
    }

    private static void ApplyJoins(
        WorkflowRunEntity entity, List<WorkflowRunParticipantDto> joins, DateTime now)
    {
        foreach (var join in joins)
        {
            if (join is null || string.IsNullOrWhiteSpace(join.SessionId))
                throw new WorkflowValidationException("A participant needs a sessionId.");
            var alreadyActive = entity.Participants.Any(
                p => p.SessionId == join.SessionId && p.LeftUtc is null);
            if (alreadyActive)
                continue; // joining twice is a no-op, not an error - spawn paths may retry.
            entity.Participants.Add(new WorkflowRunParticipantDto
            {
                SessionId = join.SessionId,
                AgentKind = join.AgentKind ?? "",
                Role = join.Role ?? "",
                Machine = join.Machine ?? "",
                JoinedUtc = join.JoinedUtc == default ? now : join.JoinedUtc,
            });
        }
    }

    private static WorkflowRunDto ToDto(WorkflowRunEntity e, bool workflowEnabled) => new()
    {
        WorkflowEnabled = workflowEnabled,
        Id = e.Id,
        WorkflowId = e.WorkflowId,
        WorkflowVersionId = e.WorkflowVersionId,
        WorkflowVersion = e.WorkflowVersion,
        ContentHash = e.ContentHash,
        Name = e.Name,
        Status = e.Status,
        AcceptanceStatus = e.AcceptanceStatus,
        AcceptedBy = e.AcceptedBy,
        AcceptedUtc = e.AcceptedUtc,
        Outcome = e.Outcome,
        CriteriaResults = e.CriteriaResults.Select(c => new WorkflowRunCriterionResultDto
        {
            CriterionId = c.CriterionId,
            Status = c.Status,
            ProofUrl = c.ProofUrl,
            Note = c.Note,
            Evaluator = c.Evaluator,
            EvaluatedUtc = c.EvaluatedUtc,
        }).ToList(),
        ProofLinks = e.ProofLinks.Select(p => new WorkflowRunProofLinkDto
        {
            Label = p.Label,
            Url = p.Url,
        }).ToList(),
        Participants = e.Participants.Select(p => new WorkflowRunParticipantDto
        {
            SessionId = p.SessionId,
            AgentKind = p.AgentKind,
            Role = p.Role,
            Machine = p.Machine,
            JoinedUtc = p.JoinedUtc,
            LeftUtc = p.LeftUtc,
        }).ToList(),
        MissionId = e.MissionId,
        ParentRunId = e.ParentRunId,
        RepoPath = e.RepoPath,
        CreatedUtc = e.CreatedUtc,
        StartedUtc = e.StartedUtc,
        CompletedUtc = e.CompletedUtc,
    };
}
