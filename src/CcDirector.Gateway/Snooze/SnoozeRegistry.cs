using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Snooze;

/// <summary>
/// The Gateway-owned, restart-surviving snooze registry (Snooze Length mission,
/// docs/architecture/snooze-length-mission-2026-07-11.md). A snooze is a time-bounded hold with a
/// GUARANTEED return: the map <c>sessionId -&gt; SnoozeUntilUtc</c> is the one piece of new Gateway-owned
/// state, and it is the thing that keeps a snoozed session from vanishing when its owning Director dies. The
/// timer MUST live here (not on the Director) precisely so it survives a dead Director - the whole point of
/// the mission.
///
/// Each entry also carries the owning <see cref="SnoozeEntry.DirectorId"/> so the registry can be bounded:
/// when a Director is removed from the fleet (<c>Registry.OnDirectorRemoved</c>), every entry it owned is
/// dropped, so entries for sessions that permanently left the roster do not accumulate.
///
/// PERSISTENCE (Hosted Gateway mission, Step 1b): entries live in the EF data layer's <c>snoozes</c> table
/// (SQLite locally), NOT the old hand-rolled <c>snooze.json</c>. The public API and observable behavior are
/// unchanged. A pending snooze survives a restart because it is in the database; an entry already past its
/// time reads as expired on the first READ (<see cref="HoldStateFor"/> returns None), so the session returns
/// to "needs you" at once. There is no sweep: an elapsed entry lingers as a durable returned-by-timer
/// tombstone, retired only by a lifecycle edge (work, an owner turn, an exit, a re-snooze).
///
/// ONE-TIME IMPORT: on first run after the upgrade, if a legacy <c>snooze.json</c> exists and the table is
/// empty, every entry is imported inside one transaction - mirroring the old load exactly: a row with an
/// empty session id is skipped, a row carrying neither a deadline nor a deferred length is dropped loudly
/// (it could never expire or land), and a duplicate session id is last-wins - then the JSON is renamed aside.
/// The rename-recovery is the idempotent, best-effort one the worklist store established. A parse error is
/// fail-loud and all-or-nothing.
///
/// NO FALLBACK (the repository's no-fallback rule): a failed persist propagates - a snooze that cannot be written would not survive a
/// restart, so the caller fails loudly rather than silently running a snooze that will not come back. The
/// Gateway is single-writer: every operation runs under this registry's write lock over a fresh pooled
/// context.
/// </summary>
public sealed class SnoozeRegistry
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;
    private readonly string _legacyJsonPath;

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <param name="db">The Gateway EF database this registry reads and writes through.</param>
    /// <param name="legacyJsonPath">The legacy <c>snooze.json</c> path to import ONCE if it exists and the
    /// table is empty. REQUIRED (no silent default).</param>
    /// <exception cref="ArgumentNullException">The database is null.</exception>
    /// <exception cref="ArgumentException">The legacy path is null/empty/whitespace.</exception>
    public SnoozeRegistry(GatewayDatabase db, string legacyJsonPath)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        if (string.IsNullOrWhiteSpace(legacyJsonPath))
            throw new ArgumentException("legacy json path is required", nameof(legacyJsonPath));
        _legacyJsonPath = legacyJsonPath;

        lock (_gate)
            ImportLegacyJsonIfNeeded();
    }

    /// <summary>
    /// One pending snooze: the session, the Director that owned it when the snooze was set (used to bound
    /// the registry on Director removal), and the clock - which comes in exactly one of two shapes.
    ///
    ///  * ARMED (the ordinary snooze): <see cref="SnoozeUntilUtc"/> holds the absolute UTC time it
    ///    returns, and <see cref="PendingMinutes"/> is null. The hold has landed and the clock is running.
    ///  * DEFERRED (defect 20): the hold was asked for while the agent was working, so it has NOT landed.
    ///    <see cref="SnoozeUntilUtc"/> is null - there is no deadline yet, because THE CLOCK STARTS WHEN
    ///    THE WORK ENDS (the owner's ruling, 14 July 2026) - and <see cref="PendingMinutes"/> remembers
    ///    the length that was asked for so <see cref="Land"/> can start it at the right moment.
    ///
    /// Exactly one of the two is non-null; <see cref="IsDeferred"/> is the predicate. A deferred entry is
    /// never expired (there is nothing to expire yet) and is never cleared for reading "not held" - that
    /// is the whole of defect 20: the old shape had no way to say "asked for, not landed", so a deferral
    /// was indistinguishable from no snooze at all and its timer was deleted 15 seconds later.
    /// </summary>
    public sealed record SnoozeEntry(
        string SessionId,
        DateTime? SnoozeUntilUtc,
        string DirectorId,
        int? PendingMinutes = null,
        DateTime? OwnerTurnBaselineUtc = null)
    {
        /// <summary>True when the hold has been asked for but has not landed: no deadline yet, length remembered.</summary>
        public bool IsDeferred => SnoozeUntilUtc is null;

        /// <summary>
        /// Did the owner drive a turn on this session AFTER the hold was asked for? If so they are back,
        /// and a hold exists only to stop bothering someone who is away - so it is over.
        ///
        /// NEVER COMPARE TWO MACHINES' CLOCKS. <paramref name="lastOwnerTurnAtUtc"/> is stamped by the
        /// owning DIRECTOR, and <see cref="OwnerTurnBaselineUtc"/> is that same Director's value captured
        /// at the instant the hold was asked for - so this compares one clock against itself and skew
        /// cannot exist. The obvious version of this rule compared the Director's turn stamp against a
        /// GATEWAY-stamped request time; those are different machines, and a Director running even
        /// slightly fast would read as "the owner is back" the moment the hold was set, killing every
        /// hold instantly. That is the bug this whole refactor exists to end, reintroduced by a
        /// timestamp.
        ///
        /// A null baseline means the owner had NEVER driven a turn when the hold was set, so any turn at
        /// all is news. A null turn means the Director has not reported one (or is too old to send the
        /// field) and can never supersede: silence is not evidence the owner came back.
        /// </summary>
        public bool SupersededByOwnerTurn(DateTime? lastOwnerTurnAtUtc)
        {
            if (lastOwnerTurnAtUtc is not DateTime turn) return false;
            if (OwnerTurnBaselineUtc is not DateTime baseline) return true;
            // COMPARE AT THE STORE'S RESOLUTION, NOT .NET's. The baseline round-trips through the snooze
            // store between being captured and being read back here, and the hosted store (Postgres
            // timestamptz) keeps only MICROSECOND precision - it drops the sub-microsecond ticks a .NET
            // DateTime carries. The live turn value, in contrast, comes straight from the in-memory session
            // DTO at full 100-nanosecond tick precision. So the SAME owner turn reads as 1-9 ticks LATER
            // than its own persisted baseline, a strict `>` calls that "the owner is back", and the hold is
            // deleted on the very next Director push - ~200ms after it was set, on EVERY hosted snooze
            // (armed or deferred). Self-host stores the baseline as full-precision TEXT and never hit this,
            // which is why it only broke on the hosted phone. Truncating both sides to microseconds makes a
            // value that merely round-tripped equal to itself; a genuine later turn (milliseconds or more
            // away - a human driving a turn) still supersedes.
            return ToMicroseconds(turn.ToUniversalTime()) > ToMicroseconds(baseline.ToUniversalTime());
        }

        // Floor a UTC instant to whole microseconds (Postgres timestamptz resolution). One microsecond is
        // 10 ticks of 100ns, so drop the remainder. Store-agnostic: a full-precision (self-host TEXT)
        // baseline and a microsecond (hosted Postgres) baseline both compare correctly after this.
        private static DateTime ToMicroseconds(DateTime dt) => dt.AddTicks(-(dt.Ticks % 10));
    }

    /// <summary>
    /// Record (or refresh) an ARMED snooze for <paramref name="sessionId"/> that returns at
    /// <paramref name="untilUtc"/> - the hold has landed and the clock is running now. Re-snoozing is just
    /// calling this again - it overwrites the prior entry with the fresh time (an alarm-clock, no
    /// escalation, no cap). Written through to disk before returning.
    /// </summary>
    /// <exception cref="ArgumentException">The session id is null/empty/whitespace.</exception>
    public void Snooze(string sessionId, DateTime untilUtc, string directorId, DateTime? ownerTurnBaselineUtc = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("session id is required", nameof(sessionId));

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            Upsert(ctx, sessionId, untilUtc.ToUniversalTime(), directorId ?? "", null, ownerTurnBaselineUtc);
            ctx.SaveChanges();
            FileLog.Write($"[SnoozeRegistry] Snooze: sid={sessionId}, untilUtc={untilUtc.ToUniversalTime():O}, director={directorId}");
        }
    }

    /// <summary>
    /// Record a DEFERRED snooze for <paramref name="sessionId"/> (defect 20): the user or the agent asked
    /// for a <paramref name="minutes"/>-long snooze while the agent was WORKING, so the Director deferred
    /// the hold. No clock is started here - THE CLOCK STARTS WHEN THE WORK ENDS - but the length is
    /// remembered so <see cref="Land"/> can start it the moment the hold lands.
    ///
    /// This entry exists at all so the request is not simply lost between "asked for" and "landed": the
    /// alternative (arm a clock now) is what the owner's ruling forbids, because a clock started at
    /// request time can expire before the hold has even landed.
    /// </summary>
    /// <exception cref="ArgumentException">The session id is null/empty/whitespace, or minutes is not positive.</exception>
    public void SnoozeDeferred(string sessionId, int minutes, string directorId, DateTime? ownerTurnBaselineUtc = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("session id is required", nameof(sessionId));
        if (minutes <= 0)
            throw new ArgumentException("minutes must be positive", nameof(minutes));

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            Upsert(ctx, sessionId, null, directorId ?? "", minutes, ownerTurnBaselineUtc);
            ctx.SaveChanges();
            FileLog.Write($"[SnoozeRegistry] SnoozeDeferred: sid={sessionId}, minutes={minutes} (clock starts when the work ends), director={directorId}");
        }
    }

    /// <summary>
    /// The deferred hold has LANDED: start its clock at <paramref name="nowUtc"/> + the remembered length.
    /// This is the moment the owner's ruling names - "snooze me for 12 hours when this finishes" means
    /// twelve hours of quiet AFTER it finishes.
    ///
    /// Returns true only when it actually converted a deferred entry into an armed one. IDEMPOTENT and
    /// safe to call on anything: no entry, or an already-armed entry, returns false and changes nothing.
    /// That matters because the push seam calls it on EVERY settled push (a settled session re-pushes its
    /// state repeatedly), so repeated calls must not restart a running clock. It used to have a second
    /// caller - an expiry-sweep backstop - but the sweep is gone; the push is the only path now, and it is
    /// prompt (the Director reports the settle within milliseconds).
    /// </summary>
    public bool Land(string sessionId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var e = Find(ctx, sessionId);
            // Only a deferred entry (no deadline yet) can land; a missing or already-armed one is a no-op.
            if (e is null || e.SnoozeUntilUtc is not null)
                return false;
            // A deferred entry always carries its length (the record's invariant), so a missing one is a
            // real defect, not something to paper over with a default. Fail loudly.
            if (e.PendingMinutes is not int minutes)
                throw new InvalidOperationException(
                    $"deferred snooze entry for session {sessionId} has no PendingMinutes; the registry is corrupt");

            var untilUtc = nowUtc.ToUniversalTime().AddMinutes(minutes);
            e.SnoozeUntilUtc = untilUtc;
            e.PendingMinutes = null;
            ctx.SaveChanges();
            FileLog.Write($"[SnoozeRegistry] Land: sid={sessionId}, deferred hold landed -> clock started, untilUtc={untilUtc:O} ({minutes} min)");
            return true;
        }
    }

    /// <summary>
    /// Remove the snooze entry for <paramref name="sessionId"/>. Returns true when an entry was
    /// removed (and persisted), false when there was none. This is the ONE clear path - a manual
    /// unsnooze, the #470 early return, and the post-expiry confirm all funnel through it.
    /// </summary>
    public bool Clear(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var e = Find(ctx, sessionId);
            if (e is null) return false;
            ctx.Snoozes.Remove(e);
            ctx.SaveChanges();
            FileLog.Write($"[SnoozeRegistry] Clear: sid={sessionId}");
            return true;
        }
    }

    /// <summary>
    /// Delete the entry for <paramref name="sessionId"/> ONLY if it is ARMED (its clock is running or has
    /// already elapsed), and leave a DEFERRED entry untouched. Returns true when it removed an armed entry
    /// (and persisted), false when there was no entry or the entry was deferred.
    ///
    /// This is the working edge's clear (owner's law, 17 July 2026: any work on a snoozed terminal ends the
    /// snooze, completely - the entry is deleted, not merely outranked). A DEFERRED entry is deliberately
    /// spared: "snooze me when this finishes" is asked for WHILE the agent is working, so the very next
    /// working observation must not delete the request it just made - that would make it impossible for an
    /// agent to snooze its own session. Only <see cref="Land"/> (settle) ever converts a deferral; work
    /// leaves it alone. An armed entry has a deadline (<c>SnoozeUntilUtc</c> non-null) whether it is running
    /// or elapsed; a deferred one has none - that is the armed/deferred split, kept in one place so the
    /// caller cannot get it wrong.
    /// </summary>
    public bool ClearIfArmed(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var e = Find(ctx, sessionId);
            if (e is null || e.SnoozeUntilUtc is null) // no entry, or DEFERRED (no deadline yet) -> spare it
                return false;
            ctx.Snoozes.Remove(e);
            ctx.SaveChanges();
            FileLog.Write($"[SnoozeRegistry] ClearIfArmed: sid={sessionId} (armed snooze deleted - the session is working again, and work ends a snooze)");
            return true;
        }
    }

    /// <summary>
    /// Drop every entry owned by <paramref name="directorId"/>. Called from
    /// <c>Registry.OnDirectorRemoved</c> so entries for sessions whose Director permanently left the
    /// fleet do not accumulate. Returns the number of entries removed; persists once if any.
    /// </summary>
    public int ClearForDirector(string directorId)
    {
        if (string.IsNullOrWhiteSpace(directorId)) return 0;
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var gone = ctx.Snoozes.Where(e => e.DirectorId == directorId).ToList();
            if (gone.Count > 0)
            {
                ctx.Snoozes.RemoveRange(gone);
                ctx.SaveChanges();
                FileLog.Write($"[SnoozeRegistry] ClearForDirector: director={directorId}, removed={gone.Count}");
            }
            return gone.Count;
        }
    }


    /// <summary>
    /// Drop every entry owned by <paramref name="directorId"/> whose session is NOT in
    /// <paramref name="liveSessionIds"/>. Called from the aggregation for a Director that actually
    /// answered (its returned list is authoritative), so a session that has permanently exited is
    /// pruned - without ever guessing from a transient miss, since it runs only for a reachable
    /// Director. Returns the number removed; persists once if any.
    /// </summary>
    public int PruneNotLive(string directorId, ISet<string> liveSessionIds)
    {
        if (string.IsNullOrWhiteSpace(directorId)) return 0;
        liveSessionIds ??= new HashSet<string>(StringComparer.Ordinal);
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var gone = ctx.Snoozes.Where(e => e.DirectorId == directorId).ToList()
                .Where(e => !liveSessionIds.Contains(e.SessionId))
                .ToList();
            if (gone.Count > 0)
            {
                ctx.Snoozes.RemoveRange(gone);
                ctx.SaveChanges();
                FileLog.Write($"[SnoozeRegistry] PruneNotLive: director={directorId}, removed={gone.Count}");
            }
            return gone.Count;
        }
    }

    /// <summary>
    /// True when <paramref name="sessionId"/> has an ARMED entry whose return time is at or before
    /// <paramref name="nowUtc"/> - i.e. the snooze has elapsed. This is the ONE expiry predicate: the fold
    /// reads it to flip the session back into "needs you" and to stamp the durable "Snooze ended" badge
    /// (<see cref="SessionDto.SnoozeExpired"/>). It stays true for as long as the elapsed entry lingers -
    /// there is no sweep to retire it; only a lifecycle edge does. Pure (no mutation), so it is safe to
    /// call on the hot read path.
    ///
    /// A DEFERRED entry is never expired: its clock has not started, because the work it is waiting for
    /// has not ended. There is nothing to elapse.
    /// </summary>
    public bool IsExpired(string sessionId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var e = Find(ctx, sessionId, tracking: false);
            return e is not null
                && e.SnoozeUntilUtc is DateTime untilUtc
                && nowUtc.ToUniversalTime() >= untilUtc;
        }
    }

    /// <summary>
    /// THE AUTHORITATIVE HOLD STATE for a session. This registry is the only writer and the only owner of
    /// hold; a Director never decides it, and what a Director reports about hold is ignored.
    ///
    /// The tri-state was always here - it is the shape of the entry itself - it simply was not trusted:
    ///   * no entry          -> <see cref="HoldStates.None"/>. Never asked for, or already over.
    ///   * a deferred entry  -> <see cref="HoldStates.DeferredHold"/>. Asked for while the agent was
    ///                          working; no clock yet, because the clock starts when the work ENDS.
    ///   * an armed entry, elapsed -> <see cref="HoldStates.None"/>. The owner asked for N minutes of
    ///                          quiet and got them; the hold is over. This subsumes the old aggregation
    ///                          overlay, which had to patch OnHold=false on the way out precisely because
    ///                          the real state lived on a Director this Gateway could not write to.
    ///   * an armed entry, running -> <see cref="HoldStates.Held"/>.
    ///
    /// Pure (no mutation), so it is safe on the hot read path of the fold.
    /// </summary>
    public string HoldStateFor(string sessionId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return HoldStates.None;
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var e = Find(ctx, sessionId, tracking: false);
            if (e is null)
                return HoldStates.None;
            if (e.SnoozeUntilUtc is not DateTime untilUtc)
                return HoldStates.DeferredHold;
            return nowUtc.ToUniversalTime() >= untilUtc ? HoldStates.None : HoldStates.Held;
        }
    }

    /// <summary>
    /// The absolute UTC deadline an ARMED snooze returns at (<see cref="SessionDto.SnoozeUntil"/>), or null
    /// when there is no clock to show: no entry, or a DEFERRED entry whose clock has not started (the work
    /// it waits on has not ended - defect 20). Deliberately returns the deadline even once it is in the past
    /// - an elapsed armed entry still has a real <c>SnoozeUntilUtc</c>; whether that reads as "over" is
    /// <see cref="HoldStateFor"/>'s and <see cref="IsExpired"/>'s ruling, not this getter's. Pure, so it is
    /// safe on the fold's read path alongside <see cref="HoldStateFor"/>.
    /// </summary>
    public DateTime? SnoozeUntilFor(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return Find(ctx, sessionId, tracking: false)?.SnoozeUntilUtc;
        }
    }

    /// <summary>
    /// The owner came back: if they drove a turn on this session after the hold was asked for, drop it.
    /// Returns true only when an entry was actually removed.
    ///
    /// This is one of the four - and only four - ways a hold ends: the owner releases it, the owner drives
    /// a turn (here), the clock runs out, or the session exits. Nothing else, and in particular no amount
    /// of activity. See <see cref="SnoozeEntry.SupersededByOwnerTurn"/> for why the comparison is against
    /// the request instant.
    /// </summary>
    public bool ClearIfSupersededByOwnerTurn(string sessionId, DateTime? lastOwnerTurnAtUtc)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || lastOwnerTurnAtUtc is null) return false;
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var e = Find(ctx, sessionId);
            if (e is null) return false;
            if (!ToRecord(e).SupersededByOwnerTurn(lastOwnerTurnAtUtc)) return false;
            ctx.Snoozes.Remove(e);
            ctx.SaveChanges();
            FileLog.Write($"[SnoozeRegistry] ClearIfSupersededByOwnerTurn: sid={sessionId}, owner drove a turn at {lastOwnerTurnAtUtc:O}, past the baseline {e.OwnerTurnBaselineUtc:O} captured when the hold was set -> hold dropped");
            return true;
        }
    }

    /// <summary>
    /// The Director that owned this session when its hold was set, or null when nothing is held.
    /// </summary>
    public string? DirectorIdFor(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return Find(ctx, sessionId, tracking: false)?.DirectorId;
        }
    }

    /// <summary>True when <paramref name="sessionId"/> has a pending entry (expired or not).</summary>
    public bool Contains(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.Snoozes.Any(e => e.SessionId == sessionId);
        }
    }

    /// <summary>
    /// A snapshot of every pending entry. A copy detached from the store, ordered by session id for a
    /// DETERMINISTIC result. Its production caller was the expiry sweep, now deleted; it remains for tests
    /// that enumerate the pending entries. The legacy store returned .NET Dictionary enumeration order, which
    /// is undefined and unstable - never a guaranteed contract - so ordering here removes that nondeterminism
    /// rather than reproducing an order nothing relied on.
    /// </summary>
    public IReadOnlyList<SnoozeEntry> Entries()
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.Snoozes.AsNoTracking().OrderBy(e => e.SessionId).ToList().Select(ToRecord).ToList();
        }
    }

    // ---- mapping + helpers ------------------------------------------------------------------------

    /// <summary>Find the entry for a session id (ordinal, the primary key). Tracked by default so a caller
    /// can mutate and save it; pass tracking:false for a pure read.</summary>
    private static SnoozeEntity? Find(GatewayDbContext ctx, string sessionId, bool tracking = true)
        => (tracking ? ctx.Snoozes : ctx.Snoozes.AsNoTracking()).FirstOrDefault(e => e.SessionId == sessionId);

    /// <summary>Insert or overwrite the entry for a session id (the old Dictionary indexer semantics).</summary>
    private static void Upsert(GatewayDbContext ctx, string sessionId, DateTime? snoozeUntilUtc, string directorId,
        int? pendingMinutes, DateTime? ownerTurnBaselineUtc)
    {
        var e = Find(ctx, sessionId);
        if (e is null)
        {
            ctx.Snoozes.Add(new SnoozeEntity
            {
                SessionId = sessionId,
                TenantId = ctx.ActiveTenant!,
                SnoozeUntilUtc = snoozeUntilUtc,
                DirectorId = directorId,
                PendingMinutes = pendingMinutes,
                OwnerTurnBaselineUtc = ownerTurnBaselineUtc,
            });
        }
        else
        {
            e.SnoozeUntilUtc = snoozeUntilUtc;
            e.DirectorId = directorId;
            e.PendingMinutes = pendingMinutes;
            e.OwnerTurnBaselineUtc = ownerTurnBaselineUtc;
        }
    }

    // DirectorId is passed through exactly - including a null retained from a legacy import - so DirectorIdFor
    // and Entries return precisely what the old store held (the record's DirectorId carried a runtime null the
    // same way). The null-forgiving operator only silences the nullable-reference warning; the value flows as-is.
    private static SnoozeEntry ToRecord(SnoozeEntity e)
        => new(e.SessionId, e.SnoozeUntilUtc, e.DirectorId!, e.PendingMinutes, e.OwnerTurnBaselineUtc);

    // ---- one-time legacy JSON import --------------------------------------------------------------

    /// <summary>The on-disk shape of the legacy registry file: one document holding every pending snooze.</summary>
    private sealed class StoreFile
    {
        public List<SnoozeEntry> Entries { get; set; } = new();
    }

    /// <summary>
    /// Import a legacy <c>snooze.json</c> exactly once: only when it exists AND the table is empty. Mirrors
    /// the old load exactly - skip an empty session id, DROP loudly a row carrying neither a deadline nor a
    /// deferred length (it could never expire or land), last-wins on a duplicate session id, and normalize
    /// the deadline to UTC - then insert inside one transaction and rename the JSON aside. Fail-loud and
    /// all-or-nothing on a parse error. When the file lingers but the table is already populated, rename it
    /// aside idempotently (best-effort recovery) so a failed rename self-heals next boot without re-importing.
    /// </summary>
    private void ImportLegacyJsonIfNeeded()
        => LegacyJsonImport.Recoverable(
            _legacyJsonPath,
            "[SnoozeRegistry]",
            isPopulated: () => { using var ctx = _db.CreateContext(); return ctx.Snoozes.Any(); },
            importCommitted: ImportRowsFromLegacyJson);

    /// <summary>
    /// Parse the legacy file and insert every pending snooze inside one transaction (last-wins on a duplicate
    /// session id, deadline normalized to UTC, a null DirectorId retained losslessly). Fail-loud and
    /// all-or-nothing - a parse error or a null document throws and imports nothing (the file is left in
    /// place). Called by the recoverable-import plumbing only when the file exists and the table is empty; the
    /// plumbing renames the file aside after this returns.
    /// </summary>
    private void ImportRowsFromLegacyJson()
    {
        using var ctx = _db.CreateContext();

        StoreFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_legacyJsonPath), FileJsonOptions);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SnoozeRegistry] Import FAILED: legacy file {_legacyJsonPath} could not be read: {ex.Message}");
            throw new InvalidOperationException(
                $"The legacy snooze file '{_legacyJsonPath}' could not be parsed for the one-time import: " +
                $"{ex.Message}. The Gateway will not start with a partial import. Fix or move the file aside " +
                "and restart.", ex);
        }

        // A null root (the JSON literal "null") or a null entries list is NOT a valid, empty store - it is an
        // unreadable one. Fail loud and leave the file in place, exactly like a parse error, rather than
        // committing zero rows and renaming the file aside (which would permanently mark an invalid legacy
        // store as migrated). This matches the corrupt-JSON contract the cron and worklist imports use.
        if (parsed is null || parsed.Entries is null)
        {
            FileLog.Write($"[SnoozeRegistry] Import FAILED: legacy file {_legacyJsonPath} deserialized to a null document or a null entries list");
            throw new InvalidOperationException(
                $"The legacy snooze file '{_legacyJsonPath}' could not be parsed for the one-time import: the " +
                "document is null or carries no entries list. The Gateway will not start with a partial import. " +
                "Fix or move the file aside and restart.");
        }

        // Reproduce the old load's row handling, INCLUDING last-wins on a duplicate session id (the old
        // in-memory Dictionary keyed by session id overwrote), so the imported set matches byte-for-byte.
        var toImport = new Dictionary<string, SnoozeEntry>(StringComparer.Ordinal);
        foreach (var e in parsed.Entries)
        {
            if (string.IsNullOrWhiteSpace(e.SessionId))
                continue; // skip a malformed row rather than fail the whole boot
            if (e.SnoozeUntilUtc is null && e.PendingMinutes is null)
            {
                FileLog.Write($"[SnoozeRegistry] Import: DROPPED malformed row sid={e.SessionId} (neither a deadline nor a deferred length)");
                continue;
            }
            toImport[e.SessionId] = e with { SnoozeUntilUtc = e.SnoozeUntilUtc?.ToUniversalTime() };
        }

        using var tx = ctx.Database.BeginTransaction();
        foreach (var e in toImport.Values)
        {
            ctx.Snoozes.Add(new SnoozeEntity
            {
                SessionId = e.SessionId,
                TenantId = ctx.ActiveTenant!,
                SnoozeUntilUtc = e.SnoozeUntilUtc,
                DirectorId = e.DirectorId, // retain the exact value, including a null (losslessness - do NOT coerce to "")
                PendingMinutes = e.PendingMinutes,
                OwnerTurnBaselineUtc = e.OwnerTurnBaselineUtc,
            });
        }
        ctx.SaveChanges();
        tx.Commit();

        FileLog.Write($"[SnoozeRegistry] Import: {toImport.Count} pending snooze(s) imported from {_legacyJsonPath}");
    }
}
