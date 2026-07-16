using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Snooze;

/// <summary>
/// The Gateway-owned, restart-surviving snooze registry (Snooze Length mission,
/// docs/architecture/snooze-length-mission-2026-07-11.md). A snooze is a time-bounded hold with a
/// GUARANTEED return: the map <c>sessionId -&gt; SnoozeUntilUtc</c> is the one piece of new
/// Gateway-owned state, and it is the thing that keeps a snoozed session from vanishing when its
/// owning Director dies. The timer MUST live here (not on the Director) precisely so it survives a
/// dead Director - the whole point of the mission.
///
/// Each entry also carries the owning <see cref="SnoozeEntry.DirectorId"/> so the registry can be
/// bounded: when a Director is removed from the fleet (<c>Registry.OnDirectorRemoved</c>), every
/// entry it owned is dropped, so entries for sessions that permanently left the roster do not
/// accumulate on disk.
///
/// PERSISTENCE (WorkListStore precedent): the whole registry lives in ONE plain JSON file at the
/// path the constructor receives (production: snooze.json in the Gateway data dir). Every mutation
/// writes through immediately with an atomic temp-file + rename, so a crash mid-write can never
/// half-truncate the store. On construction the file is loaded back so every pending snooze is
/// re-armed - an entry already past its time at startup simply reads as expired on the first sweep
/// and fires immediately (the mission's "fire any already-past entry immediately" rule), rather than
/// being lost. A corrupt file is quarantined (never silently overwritten) and the registry starts
/// empty so the Gateway still boots.
///
/// NO FALLBACK (CLAUDE.md): a failed persist is a LOGGED error that PROPAGATES - a snooze that
/// cannot be written to disk would not survive a restart, so the caller fails loudly rather than
/// silently running a snooze that will not come back.
/// </summary>
public sealed class SnoozeRegistry
{
    private readonly object _gate = new();
    private readonly string _path;

    // sessionId -> entry. Ordinal keys: a session id is an exact GUID string.
    private readonly Dictionary<string, SnoozeEntry> _entries = new(StringComparer.Ordinal);

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <param name="path">
    /// The JSON file the registry persists to. REQUIRED so no caller can silently land on the real
    /// user's file: production (<see cref="GatewayHost"/>) passes snooze.json in the Gateway data
    /// dir; tests pass an isolated temp path.
    /// </param>
    /// <exception cref="ArgumentException">The path is null/empty/whitespace.</exception>
    public SnoozeRegistry(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("registry path is required", nameof(path));
        _path = path;
        Load();
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
            return turn.ToUniversalTime() > baseline.ToUniversalTime();
        }
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
            _entries[sessionId] = new SnoozeEntry(sessionId, untilUtc.ToUniversalTime(), directorId ?? "", null, ownerTurnBaselineUtc);
            Save();
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
            _entries[sessionId] = new SnoozeEntry(sessionId, null, directorId ?? "", minutes, ownerTurnBaselineUtc);
            Save();
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
    /// That matters because two independent things call it - the push seam (the Director's hold-state
    /// delta, which is prompt) and the expiry sweep (the backstop, which is certain) - and whichever
    /// arrives first must win without the other corrupting it. A landing must never restart a running
    /// clock.
    /// </summary>
    public bool Land(string sessionId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        lock (_gate)
        {
            if (!_entries.TryGetValue(sessionId, out var e) || !e.IsDeferred)
                return false;
            // A deferred entry always carries its length (the record's invariant), so a missing one is a
            // real defect, not something to paper over with a default. Fail loudly.
            if (e.PendingMinutes is not int minutes)
                throw new InvalidOperationException(
                    $"deferred snooze entry for session {sessionId} has no PendingMinutes; the registry is corrupt");

            var untilUtc = nowUtc.ToUniversalTime().AddMinutes(minutes);
            _entries[sessionId] = e with { SnoozeUntilUtc = untilUtc, PendingMinutes = null };
            Save();
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
            if (!_entries.Remove(sessionId)) return false;
            Save();
            FileLog.Write($"[SnoozeRegistry] Clear: sid={sessionId}");
            return true;
        }
    }

    /// <summary>
    /// Drop every entry owned by <paramref name="directorId"/>. Called from
    /// <c>Registry.OnDirectorRemoved</c> so entries for sessions whose Director permanently left the
    /// fleet do not accumulate on disk. Returns the number of entries removed; persists once if any.
    /// </summary>
    public int ClearForDirector(string directorId)
    {
        if (string.IsNullOrWhiteSpace(directorId)) return 0;
        lock (_gate)
        {
            var gone = _entries.Values
                .Where(e => string.Equals(e.DirectorId, directorId, StringComparison.Ordinal))
                .Select(e => e.SessionId)
                .ToList();
            foreach (var sid in gone)
                _entries.Remove(sid);
            if (gone.Count > 0)
            {
                Save();
                FileLog.Write($"[SnoozeRegistry] ClearForDirector: director={directorId}, removed={gone.Count}");
            }
            return gone.Count;
        }
    }

    /// <summary>
    /// Compare-and-clear: remove the entry for <paramref name="sessionId"/> ONLY if its clock is still
    /// exactly as the caller last saw it - both <paramref name="expectedUntilUtc"/> and
    /// <paramref name="expectedPendingMinutes"/>. The sweep uses this so a stale decision (taken from a
    /// snapshot at the start of a pass) can never clobber a snooze the user re-armed in the meantime - a
    /// re-snooze moves the time, so the compare fails and the fresh snooze stands. This protects the one
    /// invariant that matters most: a live snooze is never silently lost. Returns true when it cleared.
    ///
    /// Both halves of the clock are compared, not just the time, because a deferred entry has no time: a
    /// deferral that LANDED mid-pass moves from (null, 12) to (a time, null), so a decision taken against
    /// the deferred snapshot correctly refuses to clear the freshly-armed clock.
    /// </summary>
    public bool ClearIfUnchanged(string sessionId, DateTime? expectedUntilUtc, int? expectedPendingMinutes = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        lock (_gate)
        {
            if (_entries.TryGetValue(sessionId, out var e)
                && e.SnoozeUntilUtc == expectedUntilUtc?.ToUniversalTime()
                && e.PendingMinutes == expectedPendingMinutes)
            {
                _entries.Remove(sessionId);
                Save();
                FileLog.Write($"[SnoozeRegistry] ClearIfUnchanged: sid={sessionId} (unchanged since the sweep read it)");
                return true;
            }
            return false;
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
            var gone = _entries.Values
                .Where(e => string.Equals(e.DirectorId, directorId, StringComparison.Ordinal)
                            && !liveSessionIds.Contains(e.SessionId))
                .Select(e => e.SessionId)
                .ToList();
            foreach (var sid in gone)
                _entries.Remove(sid);
            if (gone.Count > 0)
            {
                Save();
                FileLog.Write($"[SnoozeRegistry] PruneNotLive: director={directorId}, removed={gone.Count}");
            }
            return gone.Count;
        }
    }

    /// <summary>
    /// True when <paramref name="sessionId"/> has an ARMED entry whose return time is at or before
    /// <paramref name="nowUtc"/> - i.e. the snooze has elapsed. This is the ONE expiry predicate the
    /// aggregation overlay uses to flip the session back into "needs you", and the sweep uses to
    /// decide whether to nudge the owning Director off hold. Pure (no mutation), so it is safe to
    /// call on the hot read path.
    ///
    /// A DEFERRED entry is never expired: its clock has not started, because the work it is waiting for
    /// has not ended. There is nothing to elapse.
    /// </summary>
    public bool IsExpired(string sessionId, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        lock (_gate)
            return _entries.TryGetValue(sessionId, out var e)
                && e.SnoozeUntilUtc is DateTime untilUtc
                && nowUtc.ToUniversalTime() >= untilUtc;
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
            if (!_entries.TryGetValue(sessionId, out var e))
                return HoldStates.None;
            if (e.SnoozeUntilUtc is not DateTime untilUtc)
                return HoldStates.DeferredHold;
            return nowUtc.ToUniversalTime() >= untilUtc ? HoldStates.None : HoldStates.Held;
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
            if (!_entries.TryGetValue(sessionId, out var e)) return false;
            if (!e.SupersededByOwnerTurn(lastOwnerTurnAtUtc)) return false;
            _entries.Remove(sessionId);
            Save();
            FileLog.Write($"[SnoozeRegistry] ClearIfSupersededByOwnerTurn: sid={sessionId}, owner drove a turn at {lastOwnerTurnAtUtc:O}, past the baseline {e.OwnerTurnBaselineUtc:O} captured when the hold was set -> hold dropped");
            return true;
        }
    }

    /// <summary>
    /// The Director that owned this session when its hold was set, or null when nothing is held. One
    /// lookup under one lock: a Contains() followed by a separate scan of Entries() is two reads of a
    /// mutable map with a gap in between, and the entry can vanish in the gap.
    /// </summary>
    public string? DirectorIdFor(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return null;
        lock (_gate)
            return _entries.TryGetValue(sessionId, out var e) ? e.DirectorId : null;
    }

    /// <summary>True when <paramref name="sessionId"/> has a pending entry (expired or not).</summary>
    public bool Contains(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        lock (_gate)
            return _entries.ContainsKey(sessionId);
    }

    /// <summary>A snapshot of every pending entry, for the expiry sweep. A copy, so the sweep can
    /// iterate and mutate the registry (Clear) without touching the live collection.</summary>
    public IReadOnlyList<SnoozeEntry> Entries()
    {
        lock (_gate)
            return _entries.Values.ToList();
    }

    // ---- persistence (WorkListStore precedent) -----------------------------------------------

    /// <summary>The on-disk shape: one document holding every pending snooze.</summary>
    private sealed class StoreFile
    {
        public List<SnoozeEntry> Entries { get; set; } = new();
    }

    /// <summary>
    /// Load the registry file written by a previous Gateway run - this is the re-arm on startup.
    /// Missing file = the normal first boot (empty registry, logged). A corrupt file is quarantined
    /// (renamed next to the original with a timestamp suffix) so its bytes are preserved for the
    /// operator and never silently overwritten; the registry then starts empty so the Gateway still
    /// boots. An entry already past its time is kept as-is: the first sweep reads it as expired and
    /// fires it immediately (mission rule), so nothing is dropped and nothing all-fires-at-once.
    /// </summary>
    private void Load()
    {
        if (!File.Exists(_path))
        {
            FileLog.Write($"[SnoozeRegistry] Load: no registry file at {_path}; starting empty");
            return;
        }

        StoreFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<StoreFile>(File.ReadAllText(_path), FileJsonOptions);
        }
        catch (JsonException ex)
        {
            Quarantine(ex.Message);
            return;
        }

        if (parsed is null)
        {
            Quarantine("file deserialized to null (no registry document)");
            return;
        }

        var rearmed = 0;
        foreach (var e in parsed.Entries)
        {
            if (string.IsNullOrWhiteSpace(e.SessionId))
                continue; // skip a malformed row rather than fail the whole boot
            // A row must carry exactly one half of the clock (armed OR deferred, never both, never
            // neither). A row that carries neither is unusable - it can never expire and can never land -
            // so it is dropped loudly rather than kept as a snooze that would silently never return.
            if (e.SnoozeUntilUtc is null && e.PendingMinutes is null)
            {
                FileLog.Write($"[SnoozeRegistry] Load: DROPPED malformed row sid={e.SessionId} (neither a deadline nor a deferred length)");
                continue;
            }
            _entries[e.SessionId] = e with { SnoozeUntilUtc = e.SnoozeUntilUtc?.ToUniversalTime() };
            rearmed++;
        }

        FileLog.Write($"[SnoozeRegistry] Load: re-armed {rearmed} pending snooze(s) from {_path}");
    }

    /// <summary>
    /// Preserve an unreadable registry file as "&lt;path&gt;.corrupt-&lt;stamp&gt;" and log loudly.
    /// The original path is then free for the next write-through. The move is not allowed to fail
    /// silently: if even the quarantine fails, the exception propagates and the Gateway does not
    /// start half-blind.
    /// </summary>
    private void Quarantine(string reason)
    {
        var quarantinePath = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}";
        File.Move(_path, quarantinePath);
        FileLog.Write($"[SnoozeRegistry] Load FAILED: registry file at {_path} is corrupt ({reason}); quarantined to {quarantinePath}; starting empty. Operator action: inspect the quarantined file to recover pending snoozes.");
    }

    /// <summary>
    /// Write-through: serialize the whole registry and atomically replace the file (temp + rename),
    /// so a concurrent reader or a crash mid-write never sees a half-written registry. Called inside
    /// the lock by every mutation. A failed save is a LOGGED error that PROPAGATES (the caller's
    /// request fails loudly) - never a silent skip, because a snooze that did not persist would not
    /// survive a restart.
    /// </summary>
    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var file = new StoreFile { Entries = _entries.Values.ToList() };
            var json = JsonSerializer.Serialize(file, FileJsonOptions);

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SnoozeRegistry] Save FAILED: path={_path}: {ex.Message}");
            throw;
        }
    }
}
