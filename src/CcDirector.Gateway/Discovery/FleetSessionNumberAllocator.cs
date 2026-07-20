using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Discovery;

/// <summary>
/// The fleet-wide authority for the short three-digit session numbers (100-999) shown on the rail
/// (issue #1292). One instance lives on the Gateway for the whole fleet, so a number names exactly
/// ONE session across every Director on every machine - unlike the old per-Director allocator, where
/// each Director counted independently from 100 and the same number appeared on several Directors.
///
/// Every Director asks the Gateway for a number when it creates a session (<see cref="Allocate"/>).
/// The Gateway hands out the lowest free number, records which session (and Director) owns it, and
/// guarantees it is unique across the fleet.
///
/// Two bands (the issue's refinement):
///   - The COORDINATED band, <see cref="MinNumber"/>..<see cref="CoordinatedMaxNumber"/> (100-799),
///     is what the Gateway hands out. It fills from the low end.
///   - The OFFLINE band, <see cref="CoordinatedMaxNumber"/>+1..<see cref="MaxNumber"/> (800-999), is
///     left clear for Directors that cannot reach the Gateway at creation time. Those Directors pick a
///     random number in that band locally. Keeping the coordinated hand-outs in the low band means a
///     random offline pick is very unlikely to collide in normal use. The Gateway only spills into the
///     offline band when the coordinated band is fully exhausted (700 concurrent sessions).
///
/// The Gateway also learns numbers it did NOT hand out - a number a Director assigned offline, or any
/// number still in use after a Gateway restart - via <see cref="Adopt"/>, called as the fleet is
/// aggregated. Adopt only ever marks a number in use; it never frees one, so a momentarily-unreachable
/// Director (whose sessions drop out of the aggregation) can never have its numbers reclaimed and
/// re-handed. Numbers are freed only by an explicit <see cref="Release"/> when a session ends, or by
/// <see cref="ReleaseForDirector"/> when a whole Director is swept from the registry.
///
/// Thread-safe: every public method takes the single lock.
/// </summary>
public sealed class FleetSessionNumberAllocator
{
    /// <summary>Lowest assignable number (inclusive).</summary>
    public const int MinNumber = 100;

    /// <summary>Highest number the Gateway hands out from before it spills into the offline band.</summary>
    public const int CoordinatedMaxNumber = 799;

    /// <summary>Highest assignable number (inclusive). 800-999 is the offline band.</summary>
    public const int MaxNumber = 999;

    private readonly object _lock = new();

    /// <summary>sessionId -> the number and owning Director assigned to it.</summary>
    private readonly Dictionary<string, Assignment> _bySession = new(StringComparer.Ordinal);

    /// <summary>Every number currently reserved, for O(1) free-number probing.</summary>
    private readonly HashSet<int> _inUse = new();

    private readonly record struct Assignment(int Number, string DirectorId);

    /// <summary>Count of numbers currently reserved. For tests and diagnostics.</summary>
    public int InUseCount
    {
        get { lock (_lock) return _inUse.Count; }
    }

    /// <summary>The number currently assigned to <paramref name="sessionId"/>, or null if none.</summary>
    public int? NumberFor(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return null;
        lock (_lock)
            return _bySession.TryGetValue(sessionId, out var a) ? a.Number : (int?)null;
    }

    /// <summary>
    /// Hand out a fleet-unique number for <paramref name="sessionId"/>, owned by
    /// <paramref name="directorId"/>. Idempotent: asking again for a session that already has a number
    /// returns the SAME number, so a retry never double-assigns. Fills the coordinated band (100-799)
    /// from the low end, spilling into the offline band (800-999) only when the coordinated band is full.
    /// Returns null only when every number 100-999 is in use (pool exhausted) - the Director then shows
    /// the session without a number rather than blocking real work over a cosmetic handle.
    /// </summary>
    public int? Allocate(string sessionId, string directorId)
    {
        if (string.IsNullOrEmpty(sessionId))
            throw new ArgumentException("sessionId is required", nameof(sessionId));

        lock (_lock)
        {
            if (_bySession.TryGetValue(sessionId, out var existing))
            {
                FileLog.Write($"[FleetSessionNumberAllocator] Allocate: {sessionId} already has {existing.Number} (idempotent)");
                return existing.Number;
            }

            var chosen = FirstFree(MinNumber, CoordinatedMaxNumber) ?? FirstFree(CoordinatedMaxNumber + 1, MaxNumber);
            if (chosen is not int number)
            {
                FileLog.Write($"[FleetSessionNumberAllocator] Allocate: pool exhausted ({_inUse.Count} in use), no number for {sessionId}");
                return null;
            }

            _inUse.Add(number);
            _bySession[sessionId] = new Assignment(number, directorId ?? "");
            FileLog.Write($"[FleetSessionNumberAllocator] Allocate: {number} -> {sessionId} (director={directorId}, {_inUse.Count} in use)");
            return number;
        }
    }

    /// <summary>
    /// Mark a number the Gateway did NOT hand out as in use - a number a Director assigned offline, or a
    /// number still live after a Gateway restart - learned as the fleet is aggregated. Only ever marks a
    /// number in use; never frees one, so it is safe to call from the (possibly partial) fleet view. A
    /// no-op when the session is already known, or when the number is out of range. If the number is
    /// already held by a DIFFERENT session (a pre-existing offline collision the Gateway cannot resolve),
    /// the first owner keeps it and the conflict is logged.
    /// </summary>
    public void Adopt(string sessionId, string directorId, int number)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        if (number < MinNumber || number > MaxNumber) return;

        lock (_lock)
        {
            if (_bySession.TryGetValue(sessionId, out var existing))
            {
                // Already tracked. Keep the number it already has (its own hand-out or a prior adopt).
                if (existing.Number != number)
                    FileLog.Write($"[FleetSessionNumberAllocator] Adopt: {sessionId} already holds {existing.Number}, ignoring observed {number}");
                return;
            }

            if (_inUse.Contains(number))
            {
                FileLog.Write($"[FleetSessionNumberAllocator] Adopt: {number} already held by another session; {sessionId} keeps its offline number (pre-existing collision)");
                // Still record ownership so a later Allocate for this session is idempotent and a
                // ReleaseForDirector can clean it up; the number simply is not exclusively ours.
                _bySession[sessionId] = new Assignment(number, directorId ?? "");
                return;
            }

            _inUse.Add(number);
            _bySession[sessionId] = new Assignment(number, directorId ?? "");
            FileLog.Write($"[FleetSessionNumberAllocator] Adopt: {number} <- {sessionId} (director={directorId}, {_inUse.Count} in use)");
        }
    }

    /// <summary>
    /// Free the number owned by <paramref name="sessionId"/> back to the pool when its session ends.
    /// The number may later be reused. Releasing an unknown session is a harmless no-op.
    /// </summary>
    public void Release(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        lock (_lock)
        {
            if (!_bySession.Remove(sessionId, out var a))
                return;
            // Only clear the shared reservation if no other session still holds this number (a
            // pre-existing offline collision could have two sessions on one number).
            if (!_bySession.Values.Any(x => x.Number == a.Number))
                _inUse.Remove(a.Number);
            FileLog.Write($"[FleetSessionNumberAllocator] Release: freed {a.Number} from {sessionId} ({_inUse.Count} in use)");
        }
    }

    /// <summary>
    /// Free every number owned by <paramref name="directorId"/>. Called when a Director is removed from
    /// the registry (graceful unregister, or swept after its heartbeat went stale / its endpoint stayed
    /// unreachable past the evict window) - a Director that died without releasing its sessions' numbers.
    /// This is tied to the registry's own liveness decision, so it never fires for a Director that is
    /// merely momentarily unreachable.
    ///
    /// NOT TENANT-SCOPED, and knowingly so. <see cref="DirectorRegistry.OnDirectorRemoved"/> now carries the
    /// owning tenant, but this allocator's <c>_bySession</c> records only a bare director id beside each
    /// assignment, so there is nothing here to filter by yet. A director id is unique only within its tenant,
    /// so one tenant's removal can free another's numbers - which surfaces as a duplicated rail number, not
    /// as data loss or disclosure. Closing it means partitioning the whole allocator (the pool, the
    /// assignment record, and every Allocate/Adopt/Release caller) by tenant, which is its own unit of work
    /// and is booked as such.
    /// </summary>
    public void ReleaseForDirector(string directorId)
    {
        if (string.IsNullOrEmpty(directorId)) return;
        lock (_lock)
        {
            var gone = _bySession.Where(kv => string.Equals(kv.Value.DirectorId, directorId, StringComparison.Ordinal))
                                  .Select(kv => kv.Key).ToList();
            foreach (var sid in gone)
                _bySession.Remove(sid);
            // Recompute the reserved set from what remains, so numbers shared by a surviving session stay held.
            _inUse.Clear();
            foreach (var a in _bySession.Values)
                _inUse.Add(a.Number);
            if (gone.Count > 0)
                FileLog.Write($"[FleetSessionNumberAllocator] ReleaseForDirector {directorId}: freed {gone.Count} number(s) ({_inUse.Count} in use)");
        }
    }

    /// <summary>Lowest free number in [lo, hi], or null when the whole band is in use. Caller holds the lock.</summary>
    private int? FirstFree(int lo, int hi)
    {
        for (int n = lo; n <= hi; n++)
            if (!_inUse.Contains(n))
                return n;
        return null;
    }
}
