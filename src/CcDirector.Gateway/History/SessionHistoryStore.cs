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
    /// <paramref name="promptSentAtUtc"/> is WHEN THE MEMBER SENT THE PROMPT, not when this call happens,
    /// and a prompt sent before an erasure is refused. The two differ by more than microseconds: the
    /// Director's ingest deliberately retries records it previously failed to deliver, so a push arriving
    /// today can carry prompts from last week - including the ones the member just asked to erase.
    /// Comparing the moment of writing would let exactly those through.
    /// </summary>
    public void SetFirstPrompt(string sessionId, string line, DateTime promptSentAtUtc)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            if (ErasedSince(ctx, promptSentAtUtc))
            {
                FileLog.Write($"[SessionHistoryStore] SetFirstPrompt REFUSED (prompt predates this account's erasure): session={sessionId}");
                return;
            }
            var entity = ctx.SessionHistory.FirstOrDefault(e => e.SessionId == sessionId);
            if (entity is null || !string.IsNullOrEmpty(entity.FirstPromptLine)) return;
            entity.FirstPromptLine = line;
            ctx.SaveChanges();
        }
    }

    /// <summary>
    /// Erase every prompt-derived field this database holds for the CURRENT tenant, as part of
    /// <c>DELETE /prompts</c> (the account data right, CR-3b). The prompt log is the single copy of the
    /// prompts themselves; this removes the copies the Gateway DERIVED from them, which is what makes
    /// the delete an erasure rather than a partial one.
    ///
    /// What goes, and why it is this list rather than the obvious one:
    ///
    ///  - <see cref="SessionHistoryEntity.FirstPromptLine"/> ALWAYS, on every row. It is the first 200
    ///    characters of the member's own prompt, and it is prompt material whatever else the row holds.
    ///  - The summary content - <see cref="SessionHistoryEntity.SummaryText"/> and the four JSON lists
    ///    beside it - on every row EXCEPT a SEALED one (see below). Those come out of the same summariser
    ///    reading the same prompt log, so they are the same material at one remove; leaving them would
    ///    erase the quote and keep the paraphrase.
    ///  - The three summary METADATA fields are RESET on the same rows, not cleared to nothing meaningful:
    ///    a row still claiming a summary exists with nothing behind it is a smaller lie of the same kind.
    ///    Reset also makes the row eligible for the sweep again, which now finds an empty prompt log and
    ///    settles it honestly as "none" - the erasure is self-healing rather than something later work can undo.
    ///  - The <c>session_history_rollups</c> rows are DELETED outright. They carry a written paragraph
    ///    derived from those same session summaries and cached per repository per day, so erasing the
    ///    columns and leaving them would keep serving the erased words as prose on the History page for up
    ///    to ninety days. The staleness hash is not a defence: eventually-recomputed is a promise about the
    ///    future, not an erasure now. Deleting them costs the member nothing they cannot get back - the row
    ///    is a CACHE, and the sweep recomputes it from whatever survives, which is the point.
    ///
    /// A SEALED SUMMARY SURVIVES, and this is a correction to how this method was first written.
    /// A sealed summary is the SESSION'S OWN farewell, submitted through <see cref="SealSummary"/>: it was
    /// never read out of the prompt log, so it is not prompt material. Erasing it would make the delete
    /// remove MORE than it claims, which is not a safer error than removing less - it is a different false
    /// claim, and a member who asked to delete prompt history has not asked to lose a farewell they never
    /// associated with prompts.
    ///
    /// That exemption is only sound because <c>SummaryKind</c> faithfully tracks the SOURCE of the content
    /// currently in the row, which two independent guards in THIS class make true - both on the store, so it
    /// is a property of the data rather than of one code path:
    ///
    ///  1. <see cref="PendingSummaries"/> only offers rows whose kind is null or empty, so the summariser is
    ///     never even handed a sealed row; and
    ///  2. <see cref="StoreGeneratedSummary"/> returns early on a sealed row, so a direct call writes nothing.
    ///
    /// And in the other order - generated first, sealed afterwards - <see cref="SealSummary"/> overwrites the
    /// text and ALL FOUR lists from the seal request (absent lists become null), so no generated remnant can
    /// survive underneath a seal. <see cref="SessionHistoryEntity.SummaryAttempts"/> is the one field a sealed
    /// row keeps: it is a counter of summariser attempts, not content, and it says nothing about any prompt.
    ///
    /// If a future change lets generated text land on a sealed row, this exemption becomes a hole and the
    /// erasure silently starts retaining prompt material. The guards above are the thing to check.
    ///
    /// TENANT-SCOPED by the global query filter, exactly like every other operation here - all three
    /// statements can only reach the ambient tenant's rows, and the caller enters that scope.
    ///
    /// LOUD ON FAILURE by design, matching <c>GatewayPromptLog.DeleteAll</c>: an erasure that half-happened
    /// must surface to the caller as an error, never as a success with content left behind. There is no
    /// catch here on purpose.
    ///
    /// The counts describe rows that ACTUALLY CARRIED something: a row already free of erasable content -
    /// including a sealed row with nothing but its seal - is not counted, so a second delete honestly
    /// reports nothing to do.
    /// </summary>
    public PromptDerivedErasure ErasePromptDerived()
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var erased = EraseWithin(ctx);
            StampErasureWatermark(ctx);
            return erased;
        }
    }

    /// <summary>
    /// Record that this tenant has erased, so the prompt-derived writers can refuse material older than
    /// this moment. See <see cref="PromptErasureWatermarkEntity"/> for why the erasure is not finished
    /// without it.
    ///
    /// STAMPED AFTER the clears, not before. A writer racing the statements above is caught either way,
    /// because its material predates every one of them. Stamping first would additionally refuse a writer
    /// whose material was read in the microseconds AFTER the stamp and BEFORE the clear - a write that is
    /// resurrecting nothing, because the clear had not happened when it read.
    ///
    /// Kept out of <see cref="EraseWithin"/> deliberately: that method exists so a test can capture the
    /// SQL of the BULK statements, which are the ones that can reach many rows and therefore the ones
    /// whose tenant predicate has to be proved. This row is keyed BY the tenant, so it cannot name another
    /// account's row even in principle - a different risk, and not one that proof is about.
    /// </summary>
    private static void StampErasureWatermark(GatewayDbContext ctx)
    {
        var nowUtc = DateTime.UtcNow;
        var watermark = ctx.PromptErasureWatermarks.FirstOrDefault();
        if (watermark is null)
        {
            watermark = new PromptErasureWatermarkEntity { TenantId = ctx.ActiveTenant! };
            ctx.PromptErasureWatermarks.Add(watermark);
        }
        // Only ever forward. A clock that stepped backwards must not lower a member's erasure line.
        if (nowUtc > watermark.ErasedAtUtc)
            watermark.ErasedAtUtc = nowUtc;
        ctx.SaveChanges();
        FileLog.Write($"[SessionHistoryStore] prompt-erasure watermark stamped: {watermark.ErasedAtUtc:O}");
    }

    /// <summary>
    /// The erasure's three statements, over a context the caller supplies. Split out from
    /// <see cref="ErasePromptDerived"/> for ONE reason: it lets a test drive these exact statements against
    /// the Npgsql provider and capture the SQL Entity Framework generates for them, so the claim that the
    /// tenant predicate survives translation is checked against the product's own statements rather than a
    /// re-typed copy of them in a test. A copy would prove the copy.
    ///
    /// Not a general-purpose seam: it does no locking and enters no scope, so <see cref="ErasePromptDerived"/>
    /// remains the only way the product calls it.
    /// </summary>
    internal static PromptDerivedErasure EraseWithin(GatewayDbContext ctx)
    {
            // NO EXPLICIT TRANSACTION ACROSS THE THREE STATEMENTS, which a reader will reasonably ask
            // about. Each bulk statement carries its own, so a failure part way through leaves a row with
            // some fields cleared and some not. That state is always LESS content than before and never
            // more - no statement here writes anything - the exception reaches the caller as a 500, and
            // the prompt log is still intact because the files are deleted after this returns. It is also
            // RESUMABLE without any bookkeeping: every predicate matches on the content still present, so
            // repeating the delete finishes exactly the part that did not happen. That is a stronger
            // property than atomicity for this operation, and it is the reason a transaction was not
            // added rather than an oversight.
            //
            // COUNTED FIRST, and separately from the two updates, because one row can carry both a prompt
            // line and a summary: adding the two row counts would report one row as two. This is a count of
            // DISTINCT rows about to change, taken under the same write lock the updates run under.
            var sessions = ctx.SessionHistory
                .Count(e => e.FirstPromptLine != null
                            || (e.SummaryKind != SessionHistorySummaryKinds.Sealed
                                && (e.SummaryText != null
                                    || e.WhatWasBuiltJson != null
                                    || e.LeftUnverifiedJson != null
                                    || e.BranchesJson != null
                                    || e.PullRequestsJson != null
                                    || e.CommitsJson != null
                                    || e.SummaryKind != null
                                    || e.SummaryIsPartial
                                    || e.SummaryAttempts != 0)));

            // The prompt line goes from EVERY row, sealed or not - it is the prompt itself.
            ctx.SessionHistory
                .Where(e => e.FirstPromptLine != null)
                .ExecuteUpdate(s => s.SetProperty(e => e.FirstPromptLine, (string?)null));

            // The summary content and its metadata go from every row EXCEPT a sealed one. A row whose kind
            // is null or empty is NOT sealed and is included - Entity Framework's C# null semantics make
            // `!= "sealed"` true for a null column, which is the behaviour wanted here and is pinned by a
            // test over a row that failed summarisation and never got a kind.
            ctx.SessionHistory
                .Where(e => e.SummaryKind != SessionHistorySummaryKinds.Sealed
                            && (e.SummaryText != null
                                || e.WhatWasBuiltJson != null
                                || e.LeftUnverifiedJson != null
                                || e.BranchesJson != null
                                || e.PullRequestsJson != null
                                || e.CommitsJson != null
                                || e.SummaryKind != null
                                || e.SummaryIsPartial
                                || e.SummaryAttempts != 0))
                .ExecuteUpdate(s => s
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

            FileLog.Write($"[SessionHistoryStore] ErasePromptDerived: cleared {sessions} session row(s), deleted {rollups} rollup row(s)");
            return new PromptDerivedErasure(sessions, rollups);
    }

    /// <summary>
    /// When this tenant last erased their prompt history, or null if they never have. Material older than
    /// this must not be written into the prompt-derived fields - see <see cref="PromptErasureWatermarkEntity"/>.
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
    /// True when material read at <paramref name="materialReadAtUtc"/> predates this tenant's erasure and
    /// must not be written back. Read on the SAME context as the write that follows it, so the check and
    /// the write cannot straddle a context boundary.
    ///
    /// The comparison is "at or before", not "before": two events in the same tick cannot be ordered, and
    /// the safe direction is to refuse. Refusing a write costs a summary that regenerates on the next
    /// sweep; accepting one costs the member content they asked to be rid of.
    /// </summary>
    private static bool ErasedSince(GatewayDbContext ctx, DateTime materialReadAtUtc)
    {
        var erasedAtUtc = ctx.PromptErasureWatermarks.AsNoTracking().FirstOrDefault()?.ErasedAtUtc;
        return erasedAtUtc is { } stamp && materialReadAtUtc <= stamp;
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

    /// <summary>
    /// Store a Gateway-generated summary (or the honest "none"/"unavailable" verdicts).
    /// Never overwrites a sealed summary - the session's own account wins.
    ///
    /// <paramref name="materialReadAtUtc"/> is when the summariser READ the prompt log this summary was
    /// made from, and a summary made from material older than an erasure is refused. Summarisation takes
    /// seconds to minutes - a model call in the middle - so a pass that began before a delete lands well
    /// after it, and would write the member's erased words straight back. The metadata reset makes that
    /// worse rather than better: it moves the kind to null, which is exactly the state the sealed guard
    /// below stops refusing.
    /// </summary>
    public void StoreGeneratedSummary(string sessionId, string summaryKind, bool isPartial, string? summaryText,
        IReadOnlyList<string>? whatWasBuilt, IReadOnlyList<string>? leftUnverified,
        IReadOnlyList<string>? branches, IReadOnlyList<string>? pullRequests, IReadOnlyList<string>? commits,
        DateTime materialReadAtUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            if (ErasedSince(ctx, materialReadAtUtc))
            {
                FileLog.Write($"[SessionHistoryStore] StoreGeneratedSummary REFUSED (material predates this account's erasure): session={sessionId}");
                return;
            }
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
            return ctx.SessionHistoryRollups.AsNoTracking()
                .Where(r => r.DayUtc >= fromDayUtc.Date && r.DayUtc <= toDayUtc.Date)
                .ToList();
        }
    }

    /// <summary>
    /// Insert or replace one cached roll-up row.
    ///
    /// <paramref name="materialReadAtUtc"/> is when the inputs this paragraph was written from were read.
    /// A roll-up pass snapshots the day's session summaries, asks a model, and saves afterwards, so one
    /// that began before an erasure would recreate a deleted row out of pre-delete text - and the row it
    /// recreates is the one the History page shows as prose.
    /// </summary>
    public void SaveRollup(string repoKey, DateTime dayUtc, string? summaryText, string inputHash, int attempts,
        DateTime nowUtc, DateTime materialReadAtUtc)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            if (ErasedSince(ctx, materialReadAtUtc))
            {
                FileLog.Write($"[SessionHistoryStore] SaveRollup REFUSED (material predates this account's erasure): repo={repoKey} day={dayUtc:yyyy-MM-dd}");
                return;
            }
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
