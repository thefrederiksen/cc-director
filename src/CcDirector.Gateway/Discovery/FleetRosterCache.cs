using System.Collections.Concurrent;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Discovery;

/// <summary>
/// Issue #1215 (Cockpit plan phase 6): the Gateway's last-known-good roster cache. It exists to stop a
/// single failed Director poll from making that Director's sessions "blink" out of the aggregated roster
/// for one refresh and then reappear.
///
/// The mechanism is a defined presentation grace window, NOT a fallback that hides an outage:
///  - Every time a Director's sessions are read successfully, its snapshot is stored and its failure
///    streak is reset. That Director reads as <see cref="FleetReachabilityState.Online"/>.
///  - When a poll fails, the failure streak increases. While the streak is within
///    <see cref="GraceWindowPollCycles"/> AND a last-known-good snapshot exists, the Director reads as
///    <see cref="FleetReachabilityState.Wobbly"/> and the stored snapshot is served (marked stale, with
///    the last-seen timestamp) instead of being dropped.
///  - Once the failure streak passes the grace window, the Director reads as
///    <see cref="FleetReachabilityState.Offline"/> and its sessions are dropped from the roster.
///
/// The grace window is counted in POLL CYCLES (each roster fan-out that considered this Director is one
/// cycle), matching how the existing reachability circuit in <see cref="DirectorRegistry"/> counts
/// consecutive failures. Because the window is only a few cycles it is always far shorter than the outer
/// eviction bounds <see cref="DirectorRegistry"/> already defines (its 60 s heartbeat timeout and 3 min
/// unreachable-evict), so a genuinely down machine still reaches Offline promptly and within those bounds.
/// This cache changes presentation only; it does not touch discovery, registration, heartbeats, or the
/// reachability-circuit constants.
///
/// Reads return deep COPIES (via <see cref="SessionDto.Clone"/>) so the aggregation may stamp them
/// without contaminating the cache, and recompute the idle clock from the absolute LastActivityAt so a
/// stale session's idle time keeps advancing while it is served through the grace window.
///
/// Thread-safe: a <see cref="ConcurrentDictionary{TKey,TValue}"/> of per-Director entries, each guarded
/// by its own lock.
/// </summary>
public sealed class FleetRosterCache
{
    /// <summary>
    /// The grace window, in consecutive failed poll cycles, during which a Director's last-known-good
    /// snapshot keeps being served (marked Wobbly) before the Director is declared Offline. Three cycles
    /// absorbs a transient miss (a firewall drop or a single lost poll) yet still reaches Offline quickly,
    /// well inside the outer eviction bounds <see cref="DirectorRegistry"/> already enforces. This is the
    /// single named value for the window - do not scatter the number elsewhere.
    /// </summary>
    public const int GraceWindowPollCycles = 3;

    private readonly ConcurrentDictionary<string, Entry> _byDirector = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTime> _utcNow;

    /// <summary>
    /// Create the cache. <paramref name="utcNow"/> is a test seam for the last-seen and idle-clock logic;
    /// production passes null and the cache reads <see cref="DateTime.UtcNow"/>.
    /// </summary>
    public FleetRosterCache(Func<DateTime>? utcNow = null)
    {
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// Record a SUCCESSFUL roster read for a Director: store the last-known-good snapshot, stamp the
    /// last-seen time, and reset the failure streak. Returns the Online projection for the envelope.
    /// </summary>
    public DirectorRosterProjection RecordReachable(string directorId, IReadOnlyList<SessionDto> sessions)
    {
        if (string.IsNullOrEmpty(directorId))
            throw new ArgumentException("directorId is required", nameof(directorId));
        if (sessions is null)
            throw new ArgumentNullException(nameof(sessions));

        var now = _utcNow();
        var entry = _byDirector.GetOrAdd(directorId, _ => new Entry());
        lock (entry.Gate)
        {
            entry.Snapshot = sessions.Select(s => s.Clone()).ToList();
            entry.LastSeenUtc = now;
            var wasFailing = entry.ConsecutiveFailures > 0;
            entry.ConsecutiveFailures = 0;
            if (wasFailing)
                FileLog.Write($"[FleetRosterCache] {directorId} reachable again; roster presentation ONLINE ({entry.Snapshot.Count} sessions)");
        }
        return new DirectorRosterProjection(FleetReachabilityState.Online, now, 0, null);
    }

    /// <summary>
    /// Record a FAILED roster read for a Director and project its presentation. Increments the failure
    /// streak, then decides Wobbly (still inside the grace window with a stored snapshot to serve) or
    /// Offline (grace window exhausted, or nothing was ever cached). For Wobbly the returned projection
    /// carries deep copies of the last-known-good sessions to serve; for Offline it carries no sessions.
    /// </summary>
    public DirectorRosterProjection RecordUnreachable(string directorId, string? error)
    {
        if (string.IsNullOrEmpty(directorId))
            throw new ArgumentException("directorId is required", nameof(directorId));

        var now = _utcNow();
        var entry = _byDirector.GetOrAdd(directorId, _ => new Entry());
        lock (entry.Gate)
        {
            entry.ConsecutiveFailures++;

            var withinGrace = entry.ConsecutiveFailures <= GraceWindowPollCycles;
            var haveSnapshot = entry.Snapshot is not null;
            if (withinGrace && haveSnapshot)
            {
                var age = entry.LastSeenUtc is { } seen ? (now - seen).TotalSeconds : (double?)null;
                var stale = entry.Snapshot!.Select(s => RecomputeClocks(s.Clone(), now)).ToList();
                if (entry.ConsecutiveFailures == 1)
                    FileLog.Write($"[FleetRosterCache] {directorId} poll failed; roster presentation WOBBLY, serving {stale.Count} last-known-good sessions (grace window {GraceWindowPollCycles} cycles): {error ?? "unreachable"}");
                return new DirectorRosterProjection(FleetReachabilityState.Wobbly, entry.LastSeenUtc, age, stale);
            }

            // Grace window exhausted (or the Director was never reachable): declare Offline and drop its
            // sessions. Log the single transition into Offline (the failure exactly at the boundary), not
            // every subsequent failed cycle, so a long outage does not flood the log.
            if (haveSnapshot && entry.ConsecutiveFailures == GraceWindowPollCycles + 1)
            {
                FileLog.Write($"[FleetRosterCache] {directorId} grace window exhausted after {entry.ConsecutiveFailures - 1} failed cycles; roster presentation OFFLINE (sessions dropped): {error ?? "unreachable"}");
                entry.Snapshot = null; // the last-known-good is now too old to serve; drop it
            }
            var ageOffline = entry.LastSeenUtc is { } lastSeen ? (now - lastSeen).TotalSeconds : (double?)null;
            return new DirectorRosterProjection(FleetReachabilityState.Offline, entry.LastSeenUtc, ageOffline, null);
        }
    }

    /// <summary>
    /// Forget a Director entirely (it was unregistered or evicted from the registry). Keeps the cache
    /// from growing without bound as Directors come and go; a Director that re-registers starts with a
    /// clean slate, exactly as the registry's own reachability state does.
    /// </summary>
    public void Forget(string directorId)
    {
        if (string.IsNullOrEmpty(directorId)) return;
        if (_byDirector.TryRemove(directorId, out _))
            FileLog.Write($"[FleetRosterCache] {directorId} forgotten (unregistered/evicted); roster cache cleared");
    }

    private static SessionDto RecomputeClocks(SessionDto s, DateTime nowUtc)
    {
        if (s.LastActivityAt is DateTime last)
        {
            var idle = (nowUtc - last).TotalSeconds;
            s.IdleSeconds = idle > 0 ? idle : 0;
        }
        return s;
    }

    private sealed class Entry
    {
        public readonly object Gate = new();
        public List<SessionDto>? Snapshot;
        public DateTime? LastSeenUtc;
        public int ConsecutiveFailures;
    }
}

/// <summary>The three fleet-sweep presentation states (issue #1215).</summary>
public enum FleetReachabilityState
{
    /// <summary>The last poll succeeded.</summary>
    Online,

    /// <summary>A recent poll failed but is absorbed by the grace window; last-known-good sessions are served, dimmed.</summary>
    Wobbly,

    /// <summary>The grace window is exhausted; the Director's sessions are dropped.</summary>
    Offline,
}

/// <summary>
/// The roster presentation decision for one Director on one refresh (issue #1215). Carries the state,
/// the last-seen timestamp and its age, and - only when <see cref="FleetReachabilityState.Wobbly"/> -
/// the deep-copied last-known-good sessions to serve stale.
/// </summary>
public readonly struct DirectorRosterProjection
{
    public DirectorRosterProjection(FleetReachabilityState state, DateTime? lastSeenUtc, double? lastSeenAgeSeconds, IReadOnlyList<SessionDto>? staleSessions)
    {
        State = state;
        LastSeenUtc = lastSeenUtc;
        LastSeenAgeSeconds = lastSeenAgeSeconds;
        StaleSessions = staleSessions;
    }

    /// <summary>The presentation state for this Director.</summary>
    public FleetReachabilityState State { get; }

    /// <summary>When the Director was last read successfully (UTC), or null if never.</summary>
    public DateTime? LastSeenUtc { get; }

    /// <summary>How long ago (seconds) the Director was last seen; zero when Online, null when never seen.</summary>
    public double? LastSeenAgeSeconds { get; }

    /// <summary>
    /// The last-known-good sessions to serve stale, non-null ONLY for <see cref="FleetReachabilityState.Wobbly"/>.
    /// Null for Online (the caller has the live list) and for Offline (nothing is served).
    /// </summary>
    public IReadOnlyList<SessionDto>? StaleSessions { get; }
}
