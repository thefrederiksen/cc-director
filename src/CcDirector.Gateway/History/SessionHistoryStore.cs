using CcDirector.Core.Tenancy;
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
    public void UpsertLive(string directorId, SessionDto session, DateTime nowUtc, DirectorFacts facts = default)
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
                    // OUR clock, stamped once: the seal route's admission bound must not be a value a
                    // caller supplies. See SessionHistoryEntity.FirstSeenAtUtc.
                    FirstSeenAtUtc = nowUtc,
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
            // The pushed machine name is believed when it is there, and the Gateway's own record of
            // the connection is used when it is not - which today is always. See the entity's
            // MachineName doc for why the pushed field is empty on every client in the field.
            // Known is never overwritten by unknown, in either direction.
            var machine = !string.IsNullOrWhiteSpace(session.MachineName) ? session.MachineName
                : (!string.IsNullOrWhiteSpace(facts.MachineName) ? facts.MachineName : null);
            entity.MachineName = machine ?? entity.MachineName;
            // The version has no pushed counterpart at all - it exists only on the connection.
            entity.DirectorVersion = string.IsNullOrWhiteSpace(facts.Version) ? entity.DirectorVersion : facts.Version;
            entity.RepoPath = string.IsNullOrWhiteSpace(session.RepoPath) ? entity.RepoPath : session.RepoPath;
            entity.RepoName = string.IsNullOrWhiteSpace(session.RepoName) ? entity.RepoName : session.RepoName;
            entity.AgentKind = string.IsNullOrWhiteSpace(session.Agent) ? entity.AgentKind : session.Agent;
            entity.Model = string.IsNullOrWhiteSpace(session.CurrentModel) ? entity.Model : session.CurrentModel;
            entity.MissionName = string.IsNullOrWhiteSpace(session.MissionName) ? entity.MissionName : session.MissionName;
            // The mission KEY beside the name (issue #982): a name reads well but cannot be joined on -
            // missions get renamed, and two can share a name. Unknown never overwrites known.
            entity.MissionId = session.MissionId ?? entity.MissionId;
            entity.SessionRole = string.IsNullOrWhiteSpace(session.ExplicitRole) ? entity.SessionRole : session.ExplicitRole;
            // Birth facts (devthrottle_internal issue #982). WRITE-ONCE, unlike everything around them:
            // these describe the create call, so the first push that carries them is as good as the
            // last, and a later push that lost them (an older Director taking over mid-upgrade, a
            // reconnect from a build that predates the fields) must not be able to blank them. The
            // guard is on the STORED value being empty, not on first-sight, so a row created by an old
            // Director is still filled in the moment a new one reports the same session.
            //
            // "unknown" counts as a value here and is not overwritten later. It is the honest answer for
            // a create path that did not say, and letting a subsequent push replace it would mean the
            // recorded origin depended on which push happened to arrive - the same fact reading
            // differently run to run.
            if (string.IsNullOrEmpty(entity.OriginKind) && !string.IsNullOrWhiteSpace(session.OriginKind))
                entity.OriginKind = session.OriginKind;
            if (string.IsNullOrEmpty(entity.OriginSurface) && !string.IsNullOrWhiteSpace(session.OriginSurface))
                entity.OriginSurface = session.OriginSurface;
            if (string.IsNullOrEmpty(entity.ParentSessionId) && !string.IsNullOrWhiteSpace(session.ParentSessionId))
                entity.ParentSessionId = session.ParentSessionId;
            // CreatedAt is the Director-measured start and is stable; keep the first non-default value.
            if (entity.StartedAtUtc == default && session.CreatedAt != default)
                entity.StartedAtUtc = Utc(session.CreatedAt);
            if (session.LastActivityAt is { } activity)
                entity.LastActivityUtc = Utc(activity);
            entity.LastActivityState = string.IsNullOrWhiteSpace(session.ActivityState) ? entity.LastActivityState : session.ActivityState;
            var turns = TurnCountOf(session);
            if (turns is { } t && (entity.TurnCount is not { } existing || t > existing))
                entity.TurnCount = t;
            // The supervision facts (internal#625 phase 4). Null means an older Director - unknown
            // never overwrites a known value - and both only move forward, so a Director restart
            // (whose counters start again at zero) cannot erase the run's high-water mark.
            if (session.TurnCount is { } agentTurns && (entity.AgentTurnCount is not { } at || agentTurns > at))
                entity.AgentTurnCount = agentTurns;
            if (session.CumulativeIdleSeconds is { } idle && (entity.CumulativeIdleSeconds is not { } ci || idle > ci))
                entity.CumulativeIdleSeconds = idle;
            // The remaining per-session facts issue #982 asked for, all on the SAME high-water-mark
            // rule as the two above and for the same reason: a Director restart begins its counters
            // again at zero, and a monotonic record must not follow them down. Null is unknown and
            // never overwrites a known value.
            if (session.WaitingStretchCount is { } waits && (entity.WaitingStretchCount is not { } ws || waits > ws))
                entity.WaitingStretchCount = waits;
            if (CharacterCountOf(session) is { } chars && (entity.InputCharacterCount is not { } ic || chars > ic))
                entity.InputCharacterCount = chars;
            if (session.TokenTotals is { } tokens)
            {
                // Cumulative spend, kept per kind rather than pre-summed: cache reads and cache
                // creation are priced differently from plain input, so one total could never be turned
                // back into money - which is the whole point of keeping spend per session.
                if (entity.InputTokens is not { } it || tokens.InputTokens > it) entity.InputTokens = tokens.InputTokens;
                if (entity.OutputTokens is not { } ot || tokens.OutputTokens > ot) entity.OutputTokens = tokens.OutputTokens;
                if (entity.CacheReadTokens is not { } crt || tokens.CacheReadTokens > crt) entity.CacheReadTokens = tokens.CacheReadTokens;
                if (entity.CacheCreationTokens is not { } cct || tokens.CacheCreationTokens > cct) entity.CacheCreationTokens = tokens.CacheCreationTokens;
                // Context occupancy is a GAUGE - it rises through a turn and DROPS on a compaction -
                // so the maximum is the only reduction of it that means anything. Summing observations
                // of a gauge produces a number with no unit.
                if (entity.PeakContextTokens is not { } pct || tokens.ContextTokens > pct) entity.PeakContextTokens = tokens.ContextTokens;
            }
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

    /// <summary>Total input CHARACTER volume (operator plus agent-driven) off the pushed stats, or
    /// null (issue #982). Counted on the same population as <see cref="TurnCountOf"/> so the two are
    /// comparable: a turn count alone cannot tell a one-word "yes" from a pasted design document.</summary>
    public static long? CharacterCountOf(SessionDto session)
    {
        var stats = session.InputStats;
        if (stats is null) return null;
        return stats.Buckets.Sum(b => b.Characters) + stats.AgentDrivenCharacters;
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

    /// <summary>
    /// Set the first-prompt description source, once. Later prompts never overwrite it.
    ///
    /// ONE CONDITIONAL STATEMENT. Every condition - the row exists, it has no line yet, and this account has
    /// not erased since the prompt was sent - lives in the WHERE clause, so the database decides them
    /// together with the write. The previous version read the watermark, then read the row, then wrote:
    /// three operations with two windows between them, guarded by a lock that is an INSTANCE lock. The
    /// hosted Gateway is documented to run two overlapping containers during a slot swap, so that lock
    /// protected nothing that mattered.
    ///
    /// <paramref name="materialTimeUtc"/> is WHEN THE MEMBER SENT THE PROMPT, clamped by the caller to the
    /// Gateway's own receipt time - not when this call happens. The Director's ingest retries records it
    /// previously failed to deliver, so a push arriving today can carry prompts from last week, including
    /// ones the member has since erased.
    /// </summary>
    public void SetFirstPrompt(string sessionId, string line, DateTime materialTimeUtc)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var written = ctx.SessionHistory
                .Where(e => e.SessionId == sessionId
                            && (e.FirstPromptLine == null || e.FirstPromptLine == "")
                            && !ctx.PromptErasureWatermarks.Any(w => w.ErasedAtUtc >= materialTimeUtc))
                .ExecuteUpdate(s => s.SetProperty(e => e.FirstPromptLine, line));
            if (written == 0)
                FileLog.Write($"[SessionHistoryStore] SetFirstPrompt wrote nothing (no row, already set, or erased since): session={sessionId}");
        }
    }

    /// <summary>
    /// Erase the prompt-derived fields this database holds for the CURRENT tenant, as part of
    /// <c>DELETE /prompts</c> (the account data right, CR-3b).
    ///
    /// What goes:
    ///
    ///  - <see cref="SessionHistoryEntity.FirstPromptLine"/> on every row - the first 200 characters of the
    ///    member's own prompt.
    ///  - The summary content on every row - <see cref="SessionHistoryEntity.SummaryText"/> and the FIVE
    ///    JSON lists beside it (what was built, left unverified, branches, pull requests, commits).
    ///  - The three summary METADATA fields, RESET rather than left claiming a summary that is not there.
    ///    Reset also makes the row eligible for the sweep again, which finds an empty prompt log and settles
    ///    it honestly as "none".
    ///  - The <c>session_history_rollups</c> rows, DELETED outright. They cache a written paragraph derived
    ///    from those same session summaries, so leaving them would keep serving the erased words as prose
    ///    for up to ninety days. They are a CACHE and the sweep recomputes them from whatever survives.
    ///
    /// A SEALED SUMMARY IS ERASED TOO, and that REVERSES how this method was written for two rounds. It is
    /// worth reading why, because the mistake was not in the code:
    ///
    /// A sealed summary arrives through <see cref="SealSummary"/> from the session itself, and the earlier
    /// reasoning was that a farewell the session wrote is not prompt material, so this method should leave
    /// it alone. The verification offered for that was that <c>SummaryKind</c> reliably
    /// tracks which WRITER wrote the row - which is TRUE, and is the wrong question. What the exemption
    /// needed was that the CONTENT is not prompt-derived, and nothing establishes that: the seal route takes
    /// caller-supplied prose and all five lists with no material time and no provenance of any kind. Arriving
    /// through the seal route is an OPERATION, not a provenance. A seal composed from the member's own
    /// prompts is accepted exactly like any other.
    ///
    /// So the exemption was retaining content that MAY be the member's prompts, permanently, because every
    /// later delete would exempt it again. Retaining prompt material is the worse error, so the seal goes
    /// with the rest.
    ///
    /// TENANT-SCOPED by the global query filter, exactly like every other operation here.
    ///
    /// LOUD ON FAILURE by design, matching <c>GatewayPromptLog.DeleteAll</c>: an erasure that half-happened
    /// must surface to the caller as an error, never as a success with content left behind.
    ///
    /// The counts describe rows that ACTUALLY CARRIED something, so a second delete honestly reports
    /// nothing to do.
    /// </summary>
    public PromptDerivedErasure ErasePromptDerived()
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            // STAMPED FIRST, which reverses the earlier order and is the safer direction under a race.
            // A writer blocked by a stamp that is slightly early is CORRECT - its material is older than a
            // delete that is about to happen anyway. A writer admitted by a stamp that is late is the bug.
            // The old order optimised for the harmless case and left the harmful one open.
            StampErasureWatermark(ctx);
            return EraseWithin(ctx);
        }
    }

    /// <summary>
    /// Record that this tenant has erased, so the prompt-derived writers refuse material older than this
    /// moment. See <see cref="PromptErasureWatermarkEntity"/>.
    ///
    /// ONE CONDITIONAL STATEMENT, not a read-compare-save. The previous version read the row, compared in
    /// memory and issued an unconditional save, which is only monotonic within one process: two Gateways
    /// racing could let the older value win, LOWERING a member's erasure line - the one direction that
    /// admits resurrected material. The database now does the comparison, so the guarantee holds however
    /// many processes are stamping.
    ///
    /// The insert is the one part that cannot be conditional in a single portable statement, so it races
    /// with another process's insert. That race is decided by the primary key - one insert wins, the other
    /// throws - and the loser then runs the same conditional update, which is exactly the outcome wanted.
    /// </summary>
    private static void StampErasureWatermark(GatewayDbContext ctx)
    {
        var nowUtc = DateTime.UtcNow;
        var raised = ctx.PromptErasureWatermarks
            .Where(w => w.ErasedAtUtc < nowUtc)
            .ExecuteUpdate(s => s.SetProperty(w => w.ErasedAtUtc, nowUtc));
        if (raised == 0 && !ctx.PromptErasureWatermarks.Any())
        {
            try
            {
                ctx.PromptErasureWatermarks.Add(new PromptErasureWatermarkEntity
                {
                    TenantId = ctx.ActiveTenant!,
                    ErasedAtUtc = nowUtc,
                });
                ctx.SaveChanges();
            }
            catch (DbUpdateException)
            {
                // Another Gateway inserted first. Raise its value instead - never lower it.
                ctx.ChangeTracker.Clear();
                ctx.PromptErasureWatermarks
                    .Where(w => w.ErasedAtUtc < nowUtc)
                    .ExecuteUpdate(s => s.SetProperty(w => w.ErasedAtUtc, nowUtc));
            }
        }
        FileLog.Write($"[SessionHistoryStore] prompt-erasure watermark stamped: {nowUtc:O}");
    }

    /// <summary>
    /// When this tenant last erased their prompt history, or null if they never have. A reader for tests
    /// and diagnostics: the guards themselves never read it separately, because a separate read is exactly
    /// the window this mechanism had to close.
    /// </summary>
    public DateTime? PromptErasureWatermarkUtc()
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.PromptErasureWatermarks.AsNoTracking().FirstOrDefault()?.ErasedAtUtc;
        }
    }

    /// <summary>
    /// The same reading for an EXPLICITLY named tenant, for the prompt log's door check. The ingest path
    /// appends to a file partition addressed by tenant and does not enter the ambient scope, so a read that
    /// depended on ambient state would silently answer for the wrong account - the quiet direction, where
    /// nothing errors and material is simply admitted or refused against somebody else's erasure.
    /// </summary>
    public DateTime? PromptErasureWatermarkUtc(TenantId tenant)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.PromptErasureWatermarks.AsNoTracking().FirstOrDefault()?.ErasedAtUtc;
        }
    }

    /// <summary>
    /// The erasure's bulk statements, over a context the caller supplies. Split out from
    /// <see cref="ErasePromptDerived"/> for ONE reason: it lets a test drive these exact statements against
    /// the Npgsql provider and capture the SQL Entity Framework generates for them, so the claim that the
    /// tenant predicate survives translation is checked against the product's own statements rather than a
    /// re-typed copy of them in a test. A copy would prove the copy.
    ///
    /// Not a general-purpose seam: it does no locking, enters no scope and does not stamp the watermark, so
    /// <see cref="ErasePromptDerived"/> remains the only way the product calls it.
    /// </summary>
    internal static PromptDerivedErasure EraseWithin(GatewayDbContext ctx)
    {
            // NO EXPLICIT TRANSACTION across these statements. Each carries its own, so a failure part way
            // through leaves a row with some fields cleared and some not - always LESS content than before
            // and never more, since no statement here writes content. The exception reaches the caller as a
            // 500, and the operation is RESUMABLE without bookkeeping: every predicate matches on the
            // content still present, so repeating the delete finishes exactly the part that did not happen.
            //
            // COUNTED FIRST because one row can carry both a prompt line and a summary, and adding the two
            // update counts would report one row as two.
            var sessions = ctx.SessionHistory
                .Count(e => e.FirstPromptLine != null
                            || e.SummaryText != null
                            || e.WhatWasBuiltJson != null
                            || e.LeftUnverifiedJson != null
                            || e.BranchesJson != null
                            || e.PullRequestsJson != null
                            || e.CommitsJson != null
                            || e.SummaryKind != null
                            || e.SummaryIsPartial
                            || e.SummaryAttempts != 0);

            var cleared = ctx.SessionHistory
                .Where(e => e.FirstPromptLine != null
                            || e.SummaryText != null
                            || e.WhatWasBuiltJson != null
                            || e.LeftUnverifiedJson != null
                            || e.BranchesJson != null
                            || e.PullRequestsJson != null
                            || e.CommitsJson != null
                            || e.SummaryKind != null
                            || e.SummaryIsPartial
                            || e.SummaryAttempts != 0)
                .ExecuteUpdate(s => s
                    .SetProperty(e => e.FirstPromptLine, (string?)null)
                    .SetProperty(e => e.SummaryText, (string?)null)
                    .SetProperty(e => e.WhatWasBuiltJson, (string?)null)
                    .SetProperty(e => e.LeftUnverifiedJson, (string?)null)
                    .SetProperty(e => e.BranchesJson, (string?)null)
                    .SetProperty(e => e.PullRequestsJson, (string?)null)
                    .SetProperty(e => e.CommitsJson, (string?)null)
                    .SetProperty(e => e.SummaryKind, (string?)null)
                    .SetProperty(e => e.SummaryIsPartial, false)
                    .SetProperty(e => e.SummaryAttempts, 0));

            var rollups = ctx.SessionHistoryRollups.ExecuteDelete();

            FileLog.Write($"[SessionHistoryStore] ErasePromptDerived: cleared {cleared} session row(s), deleted {rollups} rollup row(s)");
            return new PromptDerivedErasure(sessions, rollups);
    }

    /// <summary>
    /// The session seals its own record on a clean shutdown - its account wins over anything the Gateway
    /// generated. Returns false when no row exists, or when this account erased at or after the point the
    /// sealed material could only have come from.
    ///
    /// ADMISSION USES A SERVER-OWNED TIME: <see cref="SessionHistoryEntity.FirstSeenAtUtc"/>, stamped by this
    /// Gateway when it first saw the session. Two corrections led here and both are worth keeping.
    ///
    /// First the seal took a material time as an ARGUMENT, and the endpoint - having nothing better -
    /// substituted the moment the request arrived, which is always newer than an erasure that already
    /// happened. Every seal was admitted after every delete.
    ///
    /// Then it used the row's <c>StartedAtUtc</c>, which removed the parameter but not the problem: that
    /// value is the DIRECTOR'S measured start, pushed over the wire. A caller reporting a start after the
    /// erasure would be admitted, so admission still rode a clock we do not own. The lesson is the one this
    /// mission keeps relearning - "no caller passes it" is not the same as "no caller controls it".
    ///
    /// So the bound is the first moment THIS GATEWAY observed the session. A seal carries no provenance, so
    /// the safe bound is the whole life of the session as we saw it. The consequence, stated rather than
    /// discovered: a session this Gateway first saw before an erasure can never seal afterwards. That is the
    /// correct side to err on - the member has just erased the prompts that farewell would be written from -
    /// and a session first seen after the erasure seals normally.
    /// </summary>
    public bool SealSummary(string sessionId, SealSessionSummaryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Summary))
            throw new ArgumentException("A sealed summary needs prose; an empty seal records nothing.", nameof(request));

        var text = request.Summary.Trim();
        var built = SessionHistoryFold.ToJsonList(request.WhatWasBuilt);
        var unverified = SessionHistoryFold.ToJsonList(request.LeftUnverified);
        var branches = SessionHistoryFold.ToJsonList(request.Branches);
        var pullRequests = SessionHistoryFold.ToJsonList(request.PullRequests);
        var commits = SessionHistoryFold.ToJsonList(request.Commits);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            // THIS OVER-REFUSES, DELIBERATELY, AND HERE IS EXACTLY WHAT IT COSTS. Admission is keyed to
            // when this Gateway first saw the SESSION, not to when the seal's material was written - so a
            // session that STARTED before the member's delete and ENDED after it has its farewell refused,
            // even though that farewell describes work done entirely after the delete and is genuinely new
            // material. That is a real loss and it is not an oversight.
            //
            // It is the safe direction. The seal route carries no provenance of any kind: nothing in the
            // request establishes where its prose came from. Bounding admission by the session's whole
            // observed life is the only bound available that a caller cannot move.
            //
            // And it is cheap NOW, which was not true before seals were erased on delete: a refused seal
            // leaves a row the background summariser can still fill in later from material that postdates
            // the erasure. The member loses the session's own words about itself, not the record of it.
            //
            // A reader who arrives here because a farewell is missing has found the answer: it was refused
            // because this Gateway first saw that session before the account erased its prompt history.
            var written = ctx.SessionHistory
                .Where(e => e.SessionId == sessionId
                            && !ctx.PromptErasureWatermarks.Any(w => w.ErasedAtUtc >= e.FirstSeenAtUtc))
                .ExecuteUpdate(s => s
                    .SetProperty(e => e.SummaryKind, SessionHistorySummaryKinds.Sealed)
                    .SetProperty(e => e.SummaryIsPartial, false)
                    .SetProperty(e => e.SummaryText, text)
                    .SetProperty(e => e.WhatWasBuiltJson, built)
                    .SetProperty(e => e.LeftUnverifiedJson, unverified)
                    .SetProperty(e => e.BranchesJson, branches)
                    .SetProperty(e => e.PullRequestsJson, pullRequests)
                    .SetProperty(e => e.CommitsJson, commits));
            if (written > 0)
            {
                FileLog.Write($"[SessionHistoryStore] summary sealed by the session: session={sessionId}");
                return true;
            }
            FileLog.Write($"[SessionHistoryStore] seal not stored (no row, or this account erased after the session began): session={sessionId}");
            return false;
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

    /// <summary>
    /// Store a Gateway-generated summary (or the honest "none"/"unavailable" verdicts).
    ///
    /// ONE CONDITIONAL STATEMENT, for the reason given on <see cref="SetFirstPrompt"/>: the sealed check and
    /// the watermark check are in the WHERE clause of the update that writes, so no other process can slip
    /// an erasure between deciding and writing.
    ///
    /// A SEALED ROW IS STILL NOT OVERWRITTEN by the generator - the session's own account wins over anything
    /// the Gateway wrote. That is a product rule about who wins, and it is unrelated to erasure: sealed rows
    /// ARE erased by <see cref="ErasePromptDerived"/> now, because the seal route carries no provenance.
    ///
    /// <paramref name="materialReadAtUtc"/> is when the summariser read the prompt log this summary was made
    /// from. It is NOT evidence that the material is recent - a re-read of retried pre-delete records
    /// carries a fresh read time - which is why old material is now refused at INGEST rather than policed
    /// here. This check remains as the second line of defence: it stops a summary that was already in
    /// flight when the delete landed.
    /// </summary>
    public void StoreGeneratedSummary(string sessionId, string summaryKind, bool isPartial, string? summaryText,
        IReadOnlyList<string>? whatWasBuilt, IReadOnlyList<string>? leftUnverified,
        IReadOnlyList<string>? branches, IReadOnlyList<string>? pullRequests, IReadOnlyList<string>? commits,
        DateTime materialReadAtUtc)
    {
        var text = string.IsNullOrWhiteSpace(summaryText) ? null : summaryText.Trim();
        var built = SessionHistoryFold.ToJsonList(whatWasBuilt);
        var unverified = SessionHistoryFold.ToJsonList(leftUnverified);
        var branchesJson = SessionHistoryFold.ToJsonList(branches);
        var pullRequestsJson = SessionHistoryFold.ToJsonList(pullRequests);
        var commitsJson = SessionHistoryFold.ToJsonList(commits);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var written = ctx.SessionHistory
                .Where(e => e.SessionId == sessionId
                            && e.SummaryKind != SessionHistorySummaryKinds.Sealed
                            && !ctx.PromptErasureWatermarks.Any(w => w.ErasedAtUtc >= materialReadAtUtc))
                .ExecuteUpdate(s => s
                    .SetProperty(e => e.SummaryKind, summaryKind)
                    .SetProperty(e => e.SummaryIsPartial, isPartial)
                    .SetProperty(e => e.SummaryText, text)
                    .SetProperty(e => e.WhatWasBuiltJson, built)
                    .SetProperty(e => e.LeftUnverifiedJson, unverified)
                    .SetProperty(e => e.BranchesJson, branchesJson)
                    .SetProperty(e => e.PullRequestsJson, pullRequestsJson)
                    .SetProperty(e => e.CommitsJson, commitsJson));
            FileLog.Write(written > 0
                ? $"[SessionHistoryStore] summary stored: session={sessionId} kind={summaryKind} partial={isPartial}"
                : $"[SessionHistoryStore] summary NOT stored (no row, sealed, or erased since the material was read): session={sessionId}");
        }
    }

    /// <summary>
    /// Count one failed summarisation attempt. At <see cref="MaxSummaryAttempts"/> the summary is
    /// marked unavailable - the record stands without one rather than billing a broken path forever.
    ///
    /// CONDITIONAL ON THE WATERMARK, exactly like <see cref="StoreGeneratedSummary"/>, and it was not
    /// before. This is the failure twin of the writer that was already guarded, and it was missed because
    /// it carries no prompt prose - which made it look harmless. It is not:
    ///
    ///  - it undoes the erasure's metadata reset, putting an attempt count and possibly an "unavailable"
    ///    kind back on a row the delete had cleared; and
    ///  - WORSE THAN THE METADATA, it can leave the row permanently unable to become pending again.
    ///    <see cref="PendingSummaries"/> only offers rows with no kind and attempts under the cap, so a
    ///    failure writer that lands after an erasure can push a cleared row straight back to the cap, or
    ///    stamp "unavailable" on it, and nothing will ever summarise that session again. The erasure's
    ///    self-healing property - reset, re-summarise from an empty log, settle honestly as "none" -
    ///    depended on this row being reachable.
    ///
    /// <paramref name="materialReadAtUtc"/> is the same value the successful write carries: when the
    /// summariser READ the material this attempt was made from.
    /// </summary>
    public void NoteSummaryFailure(string sessionId, DateTime materialReadAtUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            // Two conditional statements rather than one, because the "unavailable" stamp depends on the
            // NEW attempt count. Both carry the watermark predicate, so an erasure between them stops the
            // second exactly as it stops the first.
            var counted = ctx.SessionHistory
                .Where(e => e.SessionId == sessionId
                            && !ctx.PromptErasureWatermarks.Any(w => w.ErasedAtUtc >= materialReadAtUtc))
                .ExecuteUpdate(s => s.SetProperty(e => e.SummaryAttempts, e => e.SummaryAttempts + 1));
            if (counted == 0)
            {
                FileLog.Write($"[SessionHistoryStore] summary failure NOT counted (no row, or erased since the material was read): session={sessionId}");
                return;
            }

            var markedUnavailable = ctx.SessionHistory
                .Where(e => e.SessionId == sessionId
                            && e.SummaryAttempts >= MaxSummaryAttempts
                            && (e.SummaryKind == null || e.SummaryKind == "")
                            && !ctx.PromptErasureWatermarks.Any(w => w.ErasedAtUtc >= materialReadAtUtc))
                .ExecuteUpdate(s => s.SetProperty(e => e.SummaryKind, SessionHistorySummaryKinds.Unavailable));
            if (markedUnavailable > 0)
                FileLog.Write($"[SessionHistoryStore] summary marked unavailable after {MaxSummaryAttempts} attempts: session={sessionId}");
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

    /// <summary>
    /// How the sessions that STARTED in the inclusive UTC window came to exist (devthrottle_internal
    /// issue #982) - the counts behind "what share of sessions do agents start", plus how many carry a
    /// lineage edge at all.
    ///
    /// Counted by START, not by overlap. <see cref="ReadRange"/> deliberately returns every session
    /// whose life TOUCHED the window, because "what was I working on Tuesday" includes a session that
    /// began on Monday. This question is a different one - how sessions come into being - and a session
    /// is born once. Using the overlap window here would count a long-running session in every window
    /// it survived into, which for a fleet whose agent-started sessions are typically short and whose
    /// human-started ones are long would bias the share the wrong way, quietly, and by more the wider
    /// the window.
    ///
    /// A row that predates the origin fields is counted under the null key, kept apart from "unknown":
    /// one means the Gateway was not asking, the other that it asked and got nothing. A caller
    /// reporting a share must say what it did with both.
    /// </summary>
    public SessionOriginTotals OriginTotals(DateTime fromUtc, DateTime toUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var rows = ctx.SessionHistory.AsNoTracking()
                .Where(e => e.StartedAtUtc >= fromUtc && e.StartedAtUtc <= toUtc)
                .Select(e => new { e.OriginKind, e.OriginSurface, e.ParentSessionId, e.StartedAtUtc })
                .ToList();

            var byKind = new Dictionary<string, int>(StringComparer.Ordinal);
            var bySurface = new Dictionary<string, int>(StringComparer.Ordinal);
            var withParent = 0;
            DateTime? earliest = null;
            foreach (var r in rows)
            {
                var kind = string.IsNullOrEmpty(r.OriginKind) ? NotRecorded : r.OriginKind;
                var surface = string.IsNullOrEmpty(r.OriginSurface) ? NotRecorded : r.OriginSurface;
                byKind[kind] = byKind.TryGetValue(kind, out var k) ? k + 1 : 1;
                bySurface[surface] = bySurface.TryGetValue(surface, out var s) ? s + 1 : 1;
                if (!string.IsNullOrEmpty(r.ParentSessionId)) withParent++;
                if (earliest is not { } e || r.StartedAtUtc < e) earliest = r.StartedAtUtc;
            }

            return new SessionOriginTotals(fromUtc, toUtc, earliest, rows.Count, withParent, byKind, bySurface);
        }
    }

    /// <summary>The bucket key for a row written before the origin fields existed. Deliberately NOT
    /// "unknown", which is a recorded answer.</summary>
    public const string NotRecorded = "notRecorded";

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
            // NEVER SERVE A PARAGRAPH WHOSE MATERIAL PREDATES THE ACCOUNT'S ERASURE. The insert path cannot
            // be one conditional statement (an INSERT carries no WHERE portably), so a paragraph computed
            // before a delete can land after it and its compensating delete can be interrupted. This
            // predicate is what makes that orphan harmless rather than merely rare: it is unreachable from
            // the moment it exists, and the next erasure or the retention prune removes it.
            return ctx.SessionHistoryRollups.AsNoTracking()
                .Where(r => r.DayUtc >= fromDayUtc.Date && r.DayUtc <= toDayUtc.Date
                            && !ctx.PromptErasureWatermarks.Any(w => w.ErasedAtUtc >= r.MaterialReadAtUtc))
                .ToList();
        }
    }

    /// <summary>
    /// Insert or replace one cached roll-up row.
    ///
    /// The watermark comparison is IN the statements, but this one is an upsert and an INSERT cannot carry
    /// a WHERE clause in one portable statement. So:
    ///
    ///  1. A CONDITIONAL UPDATE for a row that already exists - watermark in the WHERE clause, atomic.
    ///  2. If nothing was updated and no row exists, INSERT, then immediately a CONDITIONAL DELETE that
    ///     removes what was just inserted if this account erased while it was being written.
    ///
    /// STEP 2 IS NOT ATOMIC AND THIS METHOD DOES NOT CLAIM IT IS. The insert commits before the
    /// compensating delete runs, so a process stopped between the two statements leaves the inserted row
    /// in the table. <see cref="ReadRollups"/> excludes rows at or before the watermark, and a later
    /// erasure deletes this tenant's roll-up rows outright; both are documented where they happen. The
    /// alternative is provider-specific raw SQL for a conditional insert - a narrower window bought with
    /// two hand-written statements that nothing here can check against each other.
    ///
    /// <paramref name="materialReadAtUtc"/> is when the inputs this paragraph was written from were read.
    /// </summary>
    public void SaveRollup(string repoKey, DateTime dayUtc, string? summaryText, string inputHash, int attempts,
        DateTime nowUtc, DateTime materialReadAtUtc)
    {
        var day = dayUtc.Date;
        var text = string.IsNullOrWhiteSpace(summaryText) ? null : summaryText.Trim();
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var updated = ctx.SessionHistoryRollups
                .Where(r => r.RepoKey == repoKey && r.DayUtc == day
                            && !ctx.PromptErasureWatermarks.Any(w => w.ErasedAtUtc >= materialReadAtUtc))
                .ExecuteUpdate(s => s
                    .SetProperty(r => r.SummaryText, text)
                    .SetProperty(r => r.InputHash, inputHash)
                    .SetProperty(r => r.Attempts, attempts)
                    .SetProperty(r => r.ComputedAtUtc, nowUtc)
                    .SetProperty(r => r.MaterialReadAtUtc, materialReadAtUtc));
            if (updated > 0) return;

            if (ctx.SessionHistoryRollups.Any(r => r.RepoKey == repoKey && r.DayUtc == day))
            {
                // The row is there and the conditional update did not take it: this account erased since the
                // material was read. That is the refusal.
                FileLog.Write($"[SessionHistoryStore] SaveRollup REFUSED (material predates this account's erasure): repo={repoKey} day={day:yyyy-MM-dd}");
                return;
            }

            ctx.SessionHistoryRollups.Add(new SessionHistoryRollupEntity
            {
                TenantId = ctx.ActiveTenant!,
                RepoKey = repoKey,
                DayUtc = day,
                SummaryText = text,
                InputHash = inputHash,
                Attempts = attempts,
                ComputedAtUtc = nowUtc,
                MaterialReadAtUtc = materialReadAtUtc,
            });
            ctx.SaveChanges();

            var undone = ctx.SessionHistoryRollups
                .Where(r => r.RepoKey == repoKey && r.DayUtc == day
                            && ctx.PromptErasureWatermarks.Any(w => w.ErasedAtUtc >= materialReadAtUtc))
                .ExecuteDelete();
            if (undone > 0)
                FileLog.Write($"[SessionHistoryStore] SaveRollup UNDONE (this account erased while the row was inserted): repo={repoKey} day={day:yyyy-MM-dd}");
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

/// <summary>
/// What <see cref="SessionHistoryStore.ErasePromptDerived"/> actually removed, so the delete endpoint can
/// report it rather than assert it. Both counts are rows that CARRIED something: zeroes mean there was
/// nothing derived left to erase, which is what a second delete should say.
/// </summary>
/// <param name="SessionRows">Rows on <c>session_history</c> whose prompt-derived fields were cleared.</param>
/// <param name="RollupRows">Rows deleted from <c>session_history_rollups</c>.</param>
public sealed record PromptDerivedErasure(int SessionRows, int RollupRows);

/// <summary>
/// How the sessions born in one window came to exist (devthrottle_internal issue #982), as counted by
/// <see cref="SessionHistoryStore.OriginTotals"/>.
///
/// Counts only - no share, no percentage. The interesting ratio ("agents start X% of sessions") depends
/// on what its author decides to do with the <see cref="SessionHistoryStore.NotRecorded"/> and
/// "unknown" buckets, and there is no single right answer: excluding them measures the sessions we can
/// account for, including them measures the fleet. Computing one here would fix that choice for every
/// reader and hide it from all of them, which is how a number becomes a claim nobody can check.
/// </summary>
/// <param name="FromUtc">Inclusive start of the birth window ASKED FOR - not where the record
/// actually begins. See <paramref name="EarliestStartUtc"/>.</param>
/// <param name="ToUtc">Inclusive end of the birth window.</param>
/// <param name="EarliestStartUtc">The oldest birth actually found, or null when the window is empty.
/// This is where the RECORD begins, which is rarely where the window does and is never where the fleet
/// does: retention prunes from the front, and the origin fields only started being written the day they
/// shipped. A caller quoting a share over "all time" has to say this date out loud, or it is quoting a
/// denominator it has not got.</param>
/// <param name="Sessions">How many sessions started in the window - the denominator.</param>
/// <param name="WithParent">How many of those name a parent session. Never larger than the "agent"
/// count in <paramref name="ByKind"/>: a parent is only kept on an agent origin.</param>
/// <param name="ByKind">Session count per origin kind, plus the not-recorded bucket.</param>
/// <param name="BySurface">Session count per origin surface, plus the not-recorded bucket.</param>
public sealed record SessionOriginTotals(
    DateTime FromUtc,
    DateTime ToUtc,
    DateTime? EarliestStartUtc,
    int Sessions,
    int WithParent,
    IReadOnlyDictionary<string, int> ByKind,
    IReadOnlyDictionary<string, int> BySurface);
