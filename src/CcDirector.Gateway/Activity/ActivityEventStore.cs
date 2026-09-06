using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Activity;

/// <summary>
/// The Gateway's durable, tenant-scoped activity ledger over the <c>activity_events</c> table (the
/// trustworthy-Working-start plan, docs/PLAN-trustworthy-working-start-2026-07-24.md). Append-only: every
/// row is one observed fact - a submission, an explicit backend signal, terminal output on a settled
/// session, an authoritative state transition, a transcript-proven turn, or a Gateway snooze lifecycle
/// decision - so the July 24 class of incident ("why did this session read Working, and why did its snooze
/// die") is answerable from structured history instead of free-text log archaeology.
///
/// IDEMPOTENT APPEND: the producer mints <see cref="ActivityEventRecord.EventId"/> once, before its outbox,
/// and preserves it across retries; a replayed id is acknowledged as a duplicate and never becomes a second
/// row. Validation is fail-loud and whole-batch (<see cref="ActivityValidationException"/>): a malformed
/// event is a producer bug to surface, and a half-landed batch would make the ledger lie.
///
/// RETENTION is 30 days (the owner's ruling, 2026-07-24 - deliberately replacing the earlier "unbounded,
/// decide later"): <see cref="PurgeOlderThan"/> is called per tenant by <c>ActivityRetentionSweep</c>.
///
/// Threading matches the rest of the data layer: single writer, write lock, fresh pooled context per
/// operation, tenant resolved from the ambient scope by <see cref="GatewayDatabase.CreateContext()"/>.
/// </summary>
public sealed class ActivityEventStore
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    /// <summary>The hard ceiling on one appended batch.</summary>
    public const int MaxBatchSize = 500;

    /// <summary>The hard ceiling on one read; the default page is 1000.</summary>
    public const int MaxListLimit = 5000;

    /// <summary>Identity fields (director, session, context id): bounded, they are ids not prose.</summary>
    public const int MaxIdChars = 128;

    /// <summary>Name fields (machine, agent kind).</summary>
    public const int MaxNameChars = 128;

    /// <summary>State/origin/detector fields - single tokens from closed vocabularies.</summary>
    public const int MaxTokenChars = 64;

    /// <summary>A detail is a short structured control-flow note; cap it.</summary>
    public const int MaxDetailChars = 500;

    /// <summary>Screen hashes are fixed-size digests; cap generously.</summary>
    public const int MaxHashChars = 128;

    /// <summary>The bounded changed-row diff - the largest field, and still a hard cap: the ledger stores
    /// evidence, never the byte stream.</summary>
    public const int MaxDiffChars = 4000;

    public ActivityEventStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Append a batch of events in one unit of work, validated first: one invalid event rejects the whole
    /// batch (the ledger never lands a half-batch). An event whose id this tenant already holds is a
    /// successful idempotent replay - counted in <c>Duplicates</c>, never re-written, never an error.
    /// Returns what the ledger durably holds so the producer can drop acknowledged outbox records honestly.
    /// </summary>
    /// <exception cref="ActivityValidationException">The batch or an event in it is malformed.</exception>
    public (int Written, int Duplicates) AppendBatch(IReadOnlyList<ActivityEventRecord> events)
    {
        if (events is null)
            throw new ActivityValidationException("An events list is required.");
        if (events.Count == 0)
            return (0, 0);
        if (events.Count > MaxBatchSize)
            throw new ActivityValidationException(
                $"A batch carries at most {MaxBatchSize} events (got {events.Count}).");

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var now = DateTime.UtcNow;
            var tenant = ctx.ActiveTenant!;

            // Validate the WHOLE batch before touching the database, and collapse an id repeated within the
            // batch itself (a producer replaying inside one push) to its first occurrence - same identity,
            // same idempotent outcome as a cross-batch replay.
            var fresh = new Dictionary<Guid, ActivityEventEntity>(events.Count);
            var withinBatchDuplicates = 0;
            foreach (var record in events)
            {
                if (record is null)
                    throw new ActivityValidationException("The events list contains a null entry.");
                var entity = BuildEntity(record, tenant, now);
                if (!fresh.TryAdd(entity.EventId, entity))
                    withinBatchDuplicates++;
            }

            // The replay check: which of these ids does this tenant already hold? Reads through the global
            // tenant query filter, so tenant A replaying tenant B's id sees nothing and simply writes its
            // own row under its own composite key.
            var ids = fresh.Keys.ToList();
            var existing = ctx.ActivityEvents.AsNoTracking()
                .Where(e => ids.Contains(e.EventId))
                .Select(e => e.EventId)
                .ToHashSet();

            var toInsert = fresh.Values.Where(e => !existing.Contains(e.EventId)).ToList();

            if (toInsert.Count > 0)
            {
                var previousAutoDetect = ctx.ChangeTracker.AutoDetectChangesEnabled;
                ctx.ChangeTracker.AutoDetectChangesEnabled = false;
                try
                {
                    ctx.ActivityEvents.AddRange(toInsert);
                    ctx.SaveChanges();
                }
                finally
                {
                    ctx.ChangeTracker.AutoDetectChangesEnabled = previousAutoDetect;
                }
            }

            var duplicates = existing.Count + withinBatchDuplicates;
            FileLog.Write($"[ActivityEventStore] AppendBatch: wrote {toInsert.Count}, duplicates {duplicates}");
            return (toInsert.Count, duplicates);
        }
    }

    /// <summary>
    /// Events for diagnosis, in chronological order (OccurredUtc ascending, RecordedUtc as the tie-breaker),
    /// optionally filtered by session, event type, and UTC window (<paramref name="fromUtc"/> inclusive,
    /// <paramref name="toUtc"/> exclusive).
    /// </summary>
    public IReadOnlyList<ActivityEventRecord> Read(
        string? sessionId = null, string? eventType = null,
        DateTime? fromUtc = null, DateTime? toUtc = null, int limit = 1000)
    {
        var take = Math.Clamp(limit, 1, MaxListLimit);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            IQueryable<ActivityEventEntity> query = ctx.ActivityEvents.AsNoTracking();

            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                var sid = sessionId.Trim();
                query = query.Where(e => e.SessionId == sid);
            }
            if (!string.IsNullOrWhiteSpace(eventType))
            {
                var type = eventType.Trim().ToLowerInvariant();
                query = query.Where(e => e.EventType == type);
            }
            if (fromUtc.HasValue)
            {
                var from = DateTime.SpecifyKind(fromUtc.Value, DateTimeKind.Utc);
                query = query.Where(e => e.OccurredUtc >= from);
            }
            if (toUtc.HasValue)
            {
                var to = DateTime.SpecifyKind(toUtc.Value, DateTimeKind.Utc);
                query = query.Where(e => e.OccurredUtc < to);
            }

            return query
                .OrderBy(e => e.OccurredUtc)
                .ThenBy(e => e.RecordedUtc)
                .Take(take)
                .ToList()
                .Select(ToRecord)
                .ToList();
        }
    }

    /// <summary>
    /// Delete every event of the CURRENT tenant older than <paramref name="cutoffUtc"/> (on OccurredUtc).
    /// The 30-day retention's workhorse, called per tenant by the retention sweep. Returns the number of
    /// rows deleted.
    /// </summary>
    public int PurgeOlderThan(DateTime cutoffUtc)
    {
        var cutoff = DateTime.SpecifyKind(cutoffUtc.ToUniversalTime(), DateTimeKind.Utc);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            // ExecuteDelete applies the global tenant query filter, so this deletes only the ambient
            // tenant's expired rows - a set-based delete, no need to materialize thousands of old events.
            var deleted = ctx.ActivityEvents.Where(e => e.OccurredUtc < cutoff).ExecuteDelete();
            if (deleted > 0)
                FileLog.Write($"[ActivityEventStore] PurgeOlderThan: removed {deleted} events older than {cutoff:O}");
            return deleted;
        }
    }

    /// <summary>Validate one record and build the immutable row (tenant + recorded time stamped here).</summary>
    private static ActivityEventEntity BuildEntity(ActivityEventRecord record, string tenant, DateTime now)
    {
        if (record.EventId == Guid.Empty)
            throw new ActivityValidationException("An activity event needs a non-empty eventId.");

        var directorId = Required(record.DirectorId, "directorId", MaxIdChars);
        var sessionId = Required(record.SessionId, "sessionId", MaxIdChars);

        var eventType = (record.EventType ?? "").Trim().ToLowerInvariant();
        if (!ActivityEventTypes.All.Contains(eventType, StringComparer.Ordinal))
            throw new ActivityValidationException(
                $"'{record.EventType}' is not an activity event type. Legal values: " +
                string.Join(", ", ActivityEventTypes.All) + ".");

        var cause = (record.Cause ?? "").Trim().ToLowerInvariant();
        if (!ActivityCauses.All.Contains(cause, StringComparer.Ordinal))
            throw new ActivityValidationException(
                $"'{record.Cause}' is not an activity cause. Legal values: " +
                string.Join(", ", ActivityCauses.All) + ".");

        var occurred = DateTime.SpecifyKind(record.OccurredUtc.ToUniversalTime(), DateTimeKind.Utc);
        if (occurred > now)
            occurred = now;

        return new ActivityEventEntity
        {
            TenantId = tenant,
            EventId = record.EventId,
            DirectorSequence = record.DirectorSequence,
            OccurredUtc = occurred,
            RecordedUtc = now,
            DirectorId = directorId,
            SessionId = sessionId,
            Machine = Optional(record.Machine, "machine", MaxNameChars),
            AgentKind = Optional(record.AgentKind, "agentKind", MaxNameChars),
            ContextId = Optional(record.ContextId, "contextId", MaxIdChars),
            EventType = eventType,
            PreviousState = Optional(record.PreviousState, "previousState", MaxTokenChars),
            NewState = Optional(record.NewState, "newState", MaxTokenChars),
            Cause = cause,
            Detail = Optional(record.Detail, "detail", MaxDetailChars),
            InputOrigin = Optional(record.InputOrigin, "inputOrigin", MaxTokenChars),
            SendSource = Optional(record.SendSource, "sendSource", MaxTokenChars),
            DetectorMode = Optional(record.DetectorMode, "detectorMode", MaxTokenChars),
            DetectorVersion = Optional(record.DetectorVersion, "detectorVersion", MaxTokenChars),
            OutputByteCount = record.OutputByteCount,
            BeforeScreenHash = Optional(record.BeforeScreenHash, "beforeScreenHash", MaxHashChars),
            AfterScreenHash = Optional(record.AfterScreenHash, "afterScreenHash", MaxHashChars),
            BoundedScreenDiff = Optional(record.BoundedScreenDiff, "boundedScreenDiff", MaxDiffChars),
            Route = Optional(record.Route, "route", MaxTokenChars),
            IdentityKind = Optional(record.IdentityKind, "identityKind", MaxTokenChars),
            TranscriptId = Optional(record.TranscriptId, "transcriptId", MaxIdChars),
            SpokenSpans = Optional(record.SpokenSpans, "spokenSpans", MaxDetailChars),
            ContentSha256 = Optional(record.ContentSha256, "contentSha256", MaxHashChars),
            ContentLength = record.ContentLength,
        };
    }

    private static string Required(string? value, string field, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ActivityValidationException($"An activity event needs a {field}.");
        var trimmed = value.Trim();
        if (trimmed.Length > maxChars)
            throw new ActivityValidationException($"The {field} is too long (limit {maxChars} characters).");
        return trimmed;
    }

    private static string? Optional(string? value, string field, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        if (value.Length > maxChars)
            throw new ActivityValidationException($"The {field} is too long (limit {maxChars} characters).");
        return value;
    }

    private static ActivityEventRecord ToRecord(ActivityEventEntity e) => new()
    {
        EventId = e.EventId,
        DirectorSequence = e.DirectorSequence,
        OccurredUtc = e.OccurredUtc,
        DirectorId = e.DirectorId,
        SessionId = e.SessionId,
        Machine = e.Machine,
        AgentKind = e.AgentKind,
        ContextId = e.ContextId,
        EventType = e.EventType,
        PreviousState = e.PreviousState,
        NewState = e.NewState,
        Cause = e.Cause,
        Detail = e.Detail,
        InputOrigin = e.InputOrigin,
        SendSource = e.SendSource,
        DetectorMode = e.DetectorMode,
        DetectorVersion = e.DetectorVersion,
        OutputByteCount = e.OutputByteCount,
        BeforeScreenHash = e.BeforeScreenHash,
        AfterScreenHash = e.AfterScreenHash,
        BoundedScreenDiff = e.BoundedScreenDiff,
        Route = e.Route,
        IdentityKind = e.IdentityKind,
        TranscriptId = e.TranscriptId,
        SpokenSpans = e.SpokenSpans,
        ContentSha256 = e.ContentSha256,
        ContentLength = e.ContentLength,
    };
}
