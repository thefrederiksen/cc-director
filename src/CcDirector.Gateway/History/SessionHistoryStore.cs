using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.History;

/// <summary>
/// The durable work-history store over the <c>session_history</c> and <c>session_history_rollups</c>
/// tables (issue #2194). One row per fleet session, written while it RUNS and ruled on when it ends -
/// see <see cref="SessionHistoryEntity"/> for the design. The write cadence is the RECORDER's job
/// (<see cref="SessionHistoryRecorder"/>); this store executes whatever the recorder decided, one
/// operation per call.
///
/// Threading matches the rest of the data layer: single writer, write lock, fresh pooled context per
/// operation, tenant resolved from the ambient scope by <see cref="GatewayDatabase.CreateContext()"/>.
/// </summary>
public sealed class SessionHistoryStore
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    /// <summary>The hard ceiling on one range read.</summary>
    public const int MaxListLimit = 2000;

    /// <summary>How many times the Gateway summariser tries before a summary is marked unavailable.</summary>
    public const int MaxSummaryAttempts = 3;

    public SessionHistoryStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Insert or refresh one session's row from a pushed <see cref="SessionDto"/>. Called by the
    /// recorder only when it decided a write is due (first sight, material change, or freshness drift),
    /// so this is NOT a write per push. A row previously concluded "interrupted" that reappears on the
    /// stream is REOPENED - the interrupted ruling is an inference from absence, and presence is the
    /// stronger evidence.
    /// </summary>
    public void UpsertLive(string directorId, SessionDto session, DateTime nowUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.SessionHistory.FirstOrDefault(e => e.SessionId == session.SessionId);
            if (entity is null)
            {
                entity = new SessionHistoryEntity
                {
                    TenantId = ctx.ActiveTenant!,
                    SessionId = session.SessionId,
                    StartedAtUtc = Utc(session.CreatedAt),
                };
                ctx.SessionHistory.Add(entity);
                FileLog.Write($"[SessionHistoryStore] first sight: session={session.SessionId} repo={session.RepoName}");
            }
            else if (string.Equals(entity.EndingKind, SessionHistoryEndings.Interrupted, StringComparison.Ordinal))
            {
                entity.EndingKind = null;
                entity.EndingLabel = null;
                entity.EndedAtUtc = null;
                FileLog.Write($"[SessionHistoryStore] reopened interrupted row (session reappeared): session={session.SessionId}");
            }

            entity.DirectorId = directorId;
            entity.SessionNumber = session.Number ?? entity.SessionNumber;
            entity.SessionName = string.IsNullOrWhiteSpace(session.Name) ? entity.SessionName : session.Name;
            entity.MachineName = string.IsNullOrWhiteSpace(session.MachineName) ? entity.MachineName : session.MachineName;
            entity.RepoPath = string.IsNullOrWhiteSpace(session.RepoPath) ? entity.RepoPath : session.RepoPath;
            entity.RepoName = string.IsNullOrWhiteSpace(session.RepoName) ? entity.RepoName : session.RepoName;
            entity.AgentKind = string.IsNullOrWhiteSpace(session.Agent) ? entity.AgentKind : session.Agent;
            entity.Model = string.IsNullOrWhiteSpace(session.CurrentModel) ? entity.Model : session.CurrentModel;
            entity.MissionName = string.IsNullOrWhiteSpace(session.MissionName) ? entity.MissionName : session.MissionName;
            entity.SessionRole = string.IsNullOrWhiteSpace(session.ExplicitRole) ? entity.SessionRole : session.ExplicitRole;
            // CreatedAt is the Director-measured start and is stable; keep the first non-default value.
            if (entity.StartedAtUtc == default && session.CreatedAt != default)
                entity.StartedAtUtc = Utc(session.CreatedAt);
            if (session.LastActivityAt is { } activity)
                entity.LastActivityUtc = Utc(activity);
            entity.LastActivityState = string.IsNullOrWhiteSpace(session.ActivityState) ? entity.LastActivityState : session.ActivityState;
            var turns = TurnCountOf(session);
            if (turns is { } t && (entity.TurnCount is not { } existing || t > existing))
                entity.TurnCount = t;
            entity.LastSeenUtc = nowUtc;

            ctx.SaveChanges();
        }
    }

    /// <summary>Total input turns (operator plus agent-driven) off the pushed stats, or null.</summary>
    public static long? TurnCountOf(SessionDto session)
    {
        var stats = session.InputStats;
        if (stats is null) return null;
        return stats.Buckets.Sum(b => b.Turns) + stats.AgentDrivenTurns;
    }

    /// <summary>
    /// Stamp a farewell ending on one row. Only an OPEN or previously-interrupted row is stamped: a
    /// farewell arriving twice keeps the first ruling. No row is CREATED here - an ending for a session
    /// the Gateway never saw alive has nothing usable to record.
    /// </summary>
    public void RecordEnding(string sessionId, string endingKind, bool crashed, DateTime nowUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.SessionHistory.FirstOrDefault(e => e.SessionId == sessionId);
            if (entity is null)
            {
                FileLog.Write($"[SessionHistoryStore] RecordEnding skipped (no row): session={sessionId} kind={endingKind}");
                return;
            }
            if (!string.IsNullOrEmpty(entity.EndingKind)
                && !string.Equals(entity.EndingKind, SessionHistoryEndings.Interrupted, StringComparison.Ordinal))
                return; // already ruled by a farewell; keep the first ruling

            entity.EndingKind = endingKind;
            entity.EndedAtUtc = nowUtc;
            entity.EndingLabel = SessionHistoryFold.EndingLabel(endingKind, crashed, nowUtc);
            ctx.SaveChanges();
            FileLog.Write($"[SessionHistoryStore] ending: session={sessionId} kind={endingKind}");
        }
    }

    /// <summary>
    /// The Director's clean-shutdown farewell: every still-open row of this Director ends
    /// "director-stopped". Sessions the Director already removed individually keep their own ruling.
    /// Returns how many rows were stamped.
    /// </summary>
    public int MarkDirectorStopped(string directorId, DateTime nowUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var open = ctx.SessionHistory
                .Where(e => e.DirectorId == directorId && (e.EndingKind == null || e.EndingKind == ""))
                .ToList();
            foreach (var entity in open)
            {
                entity.EndingKind = SessionHistoryEndings.DirectorStopped;
                entity.EndedAtUtc = nowUtc;
                entity.EndingLabel = SessionHistoryFold.EndingLabel(SessionHistoryEndings.DirectorStopped, crashed: false, nowUtc);
            }
            if (open.Count > 0)
            {
                ctx.SaveChanges();
                FileLog.Write($"[SessionHistoryStore] director stopped: director={directorId} stamped {open.Count} open row(s)");
            }
            return open.Count;
        }
    }

    /// <summary>
    /// Reconcile against an authoritative full snapshot: open rows of this Director whose session is
    /// NOT in the snapshot were removed while the tunnel was down (the Director's per-session remove
    /// no-ops when disconnected, and only the next snapshot reconciles). The Director is alive and no
    /// longer runs them, so they ended deliberately on its side: ruled "closed". Returns how many.
    /// </summary>
    public int CloseAbsentSessions(string directorId, IReadOnlySet<string> presentSessionIds, DateTime nowUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var open = ctx.SessionHistory
                .Where(e => e.DirectorId == directorId && (e.EndingKind == null || e.EndingKind == ""))
                .ToList();
            var absent = open.Where(e => !presentSessionIds.Contains(e.SessionId)).ToList();
            foreach (var entity in absent)
            {
                entity.EndingKind = SessionHistoryEndings.Closed;
                entity.EndedAtUtc = nowUtc;
                entity.EndingLabel = SessionHistoryFold.EndingLabel(SessionHistoryEndings.Closed, crashed: false, nowUtc);
            }
            if (absent.Count > 0)
            {
                ctx.SaveChanges();
                FileLog.Write($"[SessionHistoryStore] snapshot reconcile: director={directorId} closed {absent.Count} absent row(s)");
            }
            return absent.Count;
        }
    }

    /// <summary>
    /// The silence rule (issue #1862 lifted to the Gateway): every open row not refreshed since
    /// <paramref name="lastSeenBeforeUtc"/> is CONCLUDED interrupted. Nobody reports this ending - the
    /// absence of a goodbye is the evidence. The end time is the last observation, and the label says
    /// "last seen" so the approximation is never read as a measurement. Returns how many were ruled.
    /// </summary>
    public int ConcludeInterrupted(DateTime lastSeenBeforeUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var silent = ctx.SessionHistory
                .Where(e => (e.EndingKind == null || e.EndingKind == "") && e.LastSeenUtc < lastSeenBeforeUtc)
                .ToList();
            foreach (var entity in silent)
            {
                entity.EndingKind = SessionHistoryEndings.Interrupted;
                entity.EndedAtUtc = entity.LastSeenUtc;
                entity.EndingLabel = SessionHistoryFold.EndingLabel(SessionHistoryEndings.Interrupted, crashed: false, entity.LastSeenUtc);
            }
            if (silent.Count > 0)
            {
                ctx.SaveChanges();
                FileLog.Write($"[SessionHistoryStore] concluded interrupted: {silent.Count} row(s) silent since before {lastSeenBeforeUtc:O}");
            }
            return silent.Count;
        }
    }

    /// <summary>Set the first-prompt description source, once. Later prompts never overwrite it.</summary>
    public void SetFirstPrompt(string sessionId, string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.SessionHistory.FirstOrDefault(e => e.SessionId == sessionId);
            if (entity is null || !string.IsNullOrEmpty(entity.FirstPromptLine)) return;
            entity.FirstPromptLine = line;
            ctx.SaveChanges();
        }
    }

    /// <summary>
    /// The session seals its own record on a clean shutdown - its account wins over anything the
    /// Gateway generated. Returns false when no row exists for the session.
    /// </summary>
    public bool SealSummary(string sessionId, SealSessionSummaryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Summary))
            throw new ArgumentException("A sealed summary needs prose; an empty seal records nothing.", nameof(request));

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.SessionHistory.FirstOrDefault(e => e.SessionId == sessionId);
            if (entity is null) return false;

            entity.SummaryKind = SessionHistorySummaryKinds.Sealed;
            entity.SummaryIsPartial = false;
            entity.SummaryText = request.Summary.Trim();
            entity.WhatWasBuiltJson = SessionHistoryFold.ToJsonList(request.WhatWasBuilt);
            entity.LeftUnverifiedJson = SessionHistoryFold.ToJsonList(request.LeftUnverified);
            entity.BranchesJson = SessionHistoryFold.ToJsonList(request.Branches);
            entity.PullRequestsJson = SessionHistoryFold.ToJsonList(request.PullRequests);
            entity.CommitsJson = SessionHistoryFold.ToJsonList(request.Commits);
            ctx.SaveChanges();
            FileLog.Write($"[SessionHistoryStore] summary sealed by the session: session={sessionId}");
            return true;
        }
    }

    /// <summary>
    /// Ended rows still owed a summary: no summary kind yet, attempts under the cap, ended before
    /// <paramref name="endedBeforeUtc"/> (a small settling delay so a seal arriving right after the
    /// farewell wins the race). Detached copies, oldest ending first, capped.
    /// </summary>
    public IReadOnlyList<SessionHistoryEntity> PendingSummaries(DateTime endedBeforeUtc, int max)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.SessionHistory.AsNoTracking()
                .Where(e => e.EndingKind != null && e.EndingKind != ""
                            && (e.SummaryKind == null || e.SummaryKind == "")
                            && e.SummaryAttempts < MaxSummaryAttempts
                            && e.EndedAtUtc != null && e.EndedAtUtc < endedBeforeUtc)
                .OrderBy(e => e.EndedAtUtc)
                .Take(Math.Clamp(max, 1, 50))
                .ToList();
        }
    }

    /// <summary>Store a Gateway-generated summary (or the honest "none"/"unavailable" verdicts).
    /// Never overwrites a sealed summary - the session's own account wins.</summary>
    public void StoreGeneratedSummary(string sessionId, string summaryKind, bool isPartial, string? summaryText,
        IReadOnlyList<string>? whatWasBuilt, IReadOnlyList<string>? leftUnverified,
        IReadOnlyList<string>? branches, IReadOnlyList<string>? pullRequests, IReadOnlyList<string>? commits)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.SessionHistory.FirstOrDefault(e => e.SessionId == sessionId);
            if (entity is null) return;
            if (string.Equals(entity.SummaryKind, SessionHistorySummaryKinds.Sealed, StringComparison.Ordinal))
                return;

            entity.SummaryKind = summaryKind;
            entity.SummaryIsPartial = isPartial;
            entity.SummaryText = string.IsNullOrWhiteSpace(summaryText) ? null : summaryText.Trim();
            entity.WhatWasBuiltJson = SessionHistoryFold.ToJsonList(whatWasBuilt);
            entity.LeftUnverifiedJson = SessionHistoryFold.ToJsonList(leftUnverified);
            entity.BranchesJson = SessionHistoryFold.ToJsonList(branches);
            entity.PullRequestsJson = SessionHistoryFold.ToJsonList(pullRequests);
            entity.CommitsJson = SessionHistoryFold.ToJsonList(commits);
            ctx.SaveChanges();
            FileLog.Write($"[SessionHistoryStore] summary stored: session={sessionId} kind={summaryKind} partial={isPartial}");
        }
    }

    /// <summary>
    /// Count one failed summarisation attempt. At <see cref="MaxSummaryAttempts"/> the summary is
    /// marked unavailable - the record stands without one rather than billing a broken path forever.
    /// </summary>
    public void NoteSummaryFailure(string sessionId)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.SessionHistory.FirstOrDefault(e => e.SessionId == sessionId);
            if (entity is null) return;
            entity.SummaryAttempts++;
            if (entity.SummaryAttempts >= MaxSummaryAttempts && string.IsNullOrEmpty(entity.SummaryKind))
            {
                entity.SummaryKind = SessionHistorySummaryKinds.Unavailable;
                FileLog.Write($"[SessionHistoryStore] summary marked unavailable after {entity.SummaryAttempts} attempts: session={sessionId}");
            }
            ctx.SaveChanges();
        }
    }

    /// <summary>
    /// Every session whose observed life overlaps the inclusive UTC window - ended and still running
    /// alike ("what am I working on" and "what did I work on Tuesday" are the same record). Folded
    /// DTOs, newest start first.
    /// </summary>
    public IReadOnlyList<WorkHistorySessionDto> ReadRange(DateTime fromUtc, DateTime toUtc, int limit = 1000)
    {
        var take = Math.Clamp(limit, 1, MaxListLimit);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.SessionHistory.AsNoTracking()
                .Where(e => e.LastSeenUtc >= fromUtc && e.StartedAtUtc <= toUtc)
                .OrderByDescending(e => e.StartedAtUtc)
                .Take(take)
                .ToList()
                .Select(SessionHistoryFold.ToDto)
                .ToList();
        }
    }

    /// <summary>One session's folded record, or null.</summary>
    public WorkHistorySessionDto? Get(string sessionId)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var entity = ctx.SessionHistory.AsNoTracking().FirstOrDefault(e => e.SessionId == sessionId);
            return entity is null ? null : SessionHistoryFold.ToDto(entity);
        }
    }

    /// <summary>Cached roll-ups for days in the inclusive window, any repository group.</summary>
    public IReadOnlyList<SessionHistoryRollupEntity> ReadRollups(DateTime fromDayUtc, DateTime toDayUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.SessionHistoryRollups.AsNoTracking()
                .Where(r => r.DayUtc >= fromDayUtc.Date && r.DayUtc <= toDayUtc.Date)
                .ToList();
        }
    }

    /// <summary>Insert or replace one cached roll-up row.</summary>
    public void SaveRollup(string repoKey, DateTime dayUtc, string? summaryText, string inputHash, int attempts, DateTime nowUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var day = dayUtc.Date;
            var entity = ctx.SessionHistoryRollups.FirstOrDefault(r => r.RepoKey == repoKey && r.DayUtc == day);
            if (entity is null)
            {
                entity = new SessionHistoryRollupEntity
                {
                    TenantId = ctx.ActiveTenant!,
                    RepoKey = repoKey,
                    DayUtc = day,
                };
                ctx.SessionHistoryRollups.Add(entity);
            }
            entity.SummaryText = string.IsNullOrWhiteSpace(summaryText) ? null : summaryText.Trim();
            entity.InputHash = inputHash;
            entity.Attempts = attempts;
            entity.ComputedAtUtc = nowUtc;
            ctx.SaveChanges();
        }
    }

    /// <summary>The 90-day retention prune, called per tenant by the history sweep. Only ENDED session
    /// rows are pruned - an open row past the cutoff is the interrupted ruling's job, never retention's.
    /// Returns rows deleted across both tables.</summary>
    public int PurgeOlderThan(DateTime cutoffUtc)
    {
        var cutoff = DateTime.SpecifyKind(cutoffUtc.ToUniversalTime(), DateTimeKind.Utc);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var sessions = ctx.SessionHistory
                .Where(e => e.EndingKind != null && e.EndingKind != "" && e.LastSeenUtc < cutoff)
                .ExecuteDelete();
            var rollups = ctx.SessionHistoryRollups
                .Where(r => r.DayUtc < cutoff.Date)
                .ExecuteDelete();
            var deleted = sessions + rollups;
            if (deleted > 0)
                FileLog.Write($"[SessionHistoryStore] PurgeOlderThan: removed {sessions} session row(s) and {rollups} rollup row(s) older than {cutoff:O}");
            return deleted;
        }
    }

    private static DateTime Utc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
}
