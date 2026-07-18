using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Governance;

/// <summary>
/// The Gateway's append-only governance event ledger (issue #1771, spine item 2) over the
/// <c>governance_events</c> table. Every row is one immutable state transition of a session or a run -
/// active, idle, waiting-on-human, waiting-on-permission, blocked, recovered - stamped with when it
/// happened. This is the durable timeline the merged run row cannot give: the run carries only
/// started/completed, but "how long was this idle / waiting on me / blocked" needs the transitions between.
///
/// Append-only by contract: there is <b>no</b> update or delete. A ledger you can rewrite is not a ledger,
/// and the weekly Outcome Ledger's duration and attention-burden figures are only trustworthy if the record
/// they read from is immutable. Runs and sessions accumulate transitions for the whole reporting horizon.
///
/// Two write shapes:
///  - <see cref="Append"/> writes one transition (the common case - a Director reports a single move).
///  - <see cref="AppendBatch"/> writes many in one unit of work with change-tracking detection disabled -
///    the batched append seam the migration protocol calls for, so a burst of reported transitions is one
///    round trip, not one per row. It is still fully tenant-scoped (the base entity's tenant_id + the global
///    query filter): the batched path lowers per-insert overhead, it does not bypass tenant isolation.
///
/// Threading matches the rest of the EF data layer: the Gateway is a single writer; every operation runs
/// under this ledger's write lock over a fresh pooled context.
/// </summary>
public sealed class GovernanceEventLedger
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    /// <summary>A reason is a short control-flow note, never prose or prompt content; cap it so an
    /// authenticated caller cannot persist megabytes into the column.</summary>
    public const int MaxReasonChars = 500;

    /// <summary>The hard ceiling on one batched append, so a single request cannot ask the Gateway to
    /// materialize an unbounded write set.</summary>
    public const int MaxBatchSize = 1000;

    /// <summary>The hard ceiling on one list read; the default page is 500. The ledger is append-only and
    /// never trimmed, so an unbounded read would eventually materialize the whole history per request.</summary>
    public const int MaxListLimit = 5000;

    public GovernanceEventLedger(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>Append one transition. Returns the recorded row (its id and server-stamped RecordedUtc).</summary>
    public GovernanceEventDto Append(AppendGovernanceEventRequest request)
    {
        if (request is null)
            throw new GovernanceValidationException("An event body is required.");

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var now = DateTime.UtcNow;
            var entity = BuildEntity(request, ctx.ActiveTenant!, now);
            ctx.GovernanceEvents.Add(entity);
            ctx.SaveChanges();

            FileLog.Write($"[GovernanceEventLedger] Append: id={entity.Id}, subject={entity.SubjectKind}, " +
                          $"session={entity.SessionId ?? "-"}, run={entity.RunId?.ToString() ?? "-"}, " +
                          $"state={entity.State}");
            return ToDto(entity);
        }
    }

    /// <summary>
    /// Append many transitions in one unit of work. The whole batch is validated first: one invalid entry
    /// rejects the entire batch (the ledger never lands a half-batch), so a caller reporting a burst learns
    /// of a bad row instead of silently dropping it. Returns the number of rows written.
    /// </summary>
    public int AppendBatch(IReadOnlyList<AppendGovernanceEventRequest> requests)
    {
        if (requests is null)
            throw new GovernanceValidationException("An events list is required.");
        if (requests.Count == 0)
            return 0;
        if (requests.Count > MaxBatchSize)
            throw new GovernanceValidationException(
                $"A batch carries at most {MaxBatchSize} events (got {requests.Count}).");

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var now = DateTime.UtcNow;
            var tenant = ctx.ActiveTenant!;

            // Validate-then-build the whole batch before touching the context, so a bad entry aborts with
            // nothing added.
            var entities = new List<GovernanceEventEntity>(requests.Count);
            foreach (var request in requests)
            {
                if (request is null)
                    throw new GovernanceValidationException("The events list contains a null entry.");
                entities.Add(BuildEntity(request, tenant, now));
            }

            // The batched append seam: with change detection off, AddRange + a single SaveChanges inserts the
            // set in one round trip without EF scanning every entity for modifications (these are all fresh
            // inserts - there is nothing to detect). Tenant scoping is unaffected: tenant_id is set on each row.
            var previousAutoDetect = ctx.ChangeTracker.AutoDetectChangesEnabled;
            ctx.ChangeTracker.AutoDetectChangesEnabled = false;
            try
            {
                ctx.GovernanceEvents.AddRange(entities);
                ctx.SaveChanges();
            }
            finally
            {
                ctx.ChangeTracker.AutoDetectChangesEnabled = previousAutoDetect;
            }

            FileLog.Write($"[GovernanceEventLedger] AppendBatch: wrote {entities.Count} events");
            return entities.Count;
        }
    }

    /// <summary>
    /// Transitions, newest first, optionally filtered by session, run, subject kind, state, and time window
    /// (<paramref name="sinceUtc"/> inclusive, <paramref name="untilUtc"/> exclusive on OccurredUtc). Ordered
    /// and bounded in the database. The time window is how the weekly report reads a single week's slice.
    /// </summary>
    public IReadOnlyList<GovernanceEventDto> List(
        string? sessionId = null, Guid? runId = null, string? subjectKind = null, string? state = null,
        DateTime? sinceUtc = null, DateTime? untilUtc = null, int limit = 500)
    {
        var take = Math.Clamp(limit, 1, MaxListLimit);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            IQueryable<GovernanceEventEntity> query = ctx.GovernanceEvents.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                var id = sessionId.Trim();
                query = query.Where(e => e.SessionId == id);
            }
            if (runId.HasValue)
                query = query.Where(e => e.RunId == runId.Value);
            if (!string.IsNullOrWhiteSpace(subjectKind))
            {
                var kind = subjectKind.Trim().ToLowerInvariant();
                query = query.Where(e => e.SubjectKind == kind);
            }
            if (!string.IsNullOrWhiteSpace(state))
            {
                var wanted = state.Trim().ToLowerInvariant();
                query = query.Where(e => e.State == wanted);
            }
            if (sinceUtc.HasValue)
            {
                var since = DateTime.SpecifyKind(sinceUtc.Value, DateTimeKind.Utc);
                query = query.Where(e => e.OccurredUtc >= since);
            }
            if (untilUtc.HasValue)
            {
                var until = DateTime.SpecifyKind(untilUtc.Value, DateTimeKind.Utc);
                query = query.Where(e => e.OccurredUtc < until);
            }

            return query
                .OrderByDescending(e => e.OccurredUtc)
                .ThenByDescending(e => e.RecordedUtc)
                .Take(take)
                .ToList()
                .Select(ToDto)
                .ToList();
        }
    }

    /// <summary>Validate one request and build the immutable row (tenant + timestamps stamped here).</summary>
    private static GovernanceEventEntity BuildEntity(
        AppendGovernanceEventRequest request, string tenant, DateTime now)
    {
        var subject = (request.SubjectKind ?? "").Trim().ToLowerInvariant();
        if (!GovernanceEventSubject.All.Contains(subject, StringComparer.Ordinal))
            throw new GovernanceValidationException(
                $"'{request.SubjectKind}' is not a subject kind. Legal values: " +
                string.Join(", ", GovernanceEventSubject.All) + ".");

        var state = (request.State ?? "").Trim().ToLowerInvariant();
        if (!GovernanceEventState.All.Contains(state, StringComparer.Ordinal))
            throw new GovernanceValidationException(
                $"'{request.State}' is not a governance state. Legal values: " +
                string.Join(", ", GovernanceEventState.All) + ".");

        var sessionId = string.IsNullOrWhiteSpace(request.SessionId) ? null : request.SessionId.Trim();
        var runId = request.RunId;

        // The subject's OWN key is required; the other key is an optional denormalized join hint.
        if (subject == GovernanceEventSubject.Session && sessionId is null)
            throw new GovernanceValidationException("A session event needs a sessionId.");
        if (subject == GovernanceEventSubject.Run && (runId is null || runId.Value == Guid.Empty))
            throw new GovernanceValidationException("A run event needs a runId.");

        if (request.Reason is not null && request.Reason.Length > MaxReasonChars)
            throw new GovernanceValidationException(
                $"The reason is too long (limit {MaxReasonChars} characters).");

        // Caller-supplied occurred time, defaulting to the append time; a future timestamp is a clock error,
        // not a real transition, so clamp it to now rather than trusting a caller's skewed clock.
        var occurred = request.OccurredUtc.HasValue
            ? DateTime.SpecifyKind(request.OccurredUtc.Value, DateTimeKind.Utc)
            : now;
        if (occurred > now)
            occurred = now;

        return new GovernanceEventEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            SubjectKind = subject,
            SessionId = sessionId,
            RunId = runId,
            State = state,
            Reason = request.Reason,
            OccurredUtc = occurred,
            RecordedUtc = now,
        };
    }

    private static GovernanceEventDto ToDto(GovernanceEventEntity e) => new()
    {
        Id = e.Id,
        SubjectKind = e.SubjectKind,
        SessionId = e.SessionId,
        RunId = e.RunId,
        State = e.State,
        Reason = e.Reason,
        OccurredUtc = e.OccurredUtc,
        RecordedUtc = e.RecordedUtc,
    };
}
