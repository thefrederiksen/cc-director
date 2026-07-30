namespace CcDirector.Gateway.Stats;

/// <summary>
/// What one statistics OBSERVER has to say about its own health: how often it has failed, how much it has
/// dropped, the last thing that went wrong, and when it last actually stored something.
///
/// THIS IS STEP 1'S SHAPE, DEPENDED ON RATHER THAN REBUILT. The statistics failure surface - the endpoint
/// that serves these numbers on <c>/stats/data</c> and puts them inside the 503 body - belongs to Step 1 and
/// lands separately. This interface is the small contract the startup boundary codes against so the two
/// pieces can be built at the same time without either guessing at the other. There is deliberately NO
/// endpoint wiring on this side. If Step 1 lands a different spelling, adapting to it is a rename.
///
/// WHY ALL FOUR, AND WHY "LAST SUCCESSFUL WRITE" IS THE ONE THAT MATTERS. A failure count alone cannot tell
/// a store that is broken from a store that is idle: zero failures is what both look like. The last
/// successful write is what distinguishes them, and it is the number that would have made the 2026-07-30
/// incident obvious in seconds instead of thirty-two minutes.
///
/// A DROP IS NOT A FAILURE AND THEY ARE COUNTED SEPARATELY. A failure is an attempt that went wrong; a drop
/// is an observation deliberately NOT attempted, because the store is known to be unavailable. Collapsing
/// them would hide exactly the thing the containment boundary does: refusing to attempt a write is the
/// CORRECT behaviour when the store is down, and it must be visible as its own number rather than looking
/// either like a failure storm or like nothing happening at all.
/// </summary>
public interface IStatsFailureState
{
    /// <summary>The observer this is about - a stable, lower-case, hyphenated identifier, not a display
    /// string. The surface groups by it, so it must not change spelling between builds.</summary>
    string Observer { get; }

    /// <summary>How many attempts have FAILED since this process started. Process-lifetime, not
    /// all-time: it measures this container's health, and a counter carried across restarts would describe a
    /// process that no longer exists.</summary>
    long FailureCount { get; }

    /// <summary>How many observations were deliberately NOT attempted because the store is unavailable.
    /// Bounded work refused on purpose - never an error, and never silence.</summary>
    long DropCount { get; }

    /// <summary>The last thing that went wrong, already reduced to something safe to serve, or null if
    /// nothing has. Never carries a connection string and never a credential.</summary>
    string? LastError { get; }

    /// <summary>When this observer last actually stored something, or null if it never has since this
    /// process started. The single most useful number on the surface: a store with recent failures and a
    /// recent successful write is degraded, and one with neither is dead.</summary>
    DateTimeOffset? LastSuccessfulWrite { get; }
}

/// <summary>
/// The counters behind <see cref="IStatsFailureState"/>: a small thread-safe tally an observer keeps about
/// itself.
///
/// Thread safety is not optional here. These are written from the roster path - many threads, concurrently -
/// and read from an endpoint on another. The counts move under <see cref="Interlocked"/>, and the two
/// reference-typed fields are published with volatile writes, so a reader sees a consistent value of each
/// field rather than a torn one. The four fields are NOT captured atomically with respect to each other and
/// deliberately are not: a lock on this would put the roster path behind the health surface, which is the
/// coupling the whole containment boundary exists to remove, and no decision is made on the four being
/// mutually consistent to the microsecond.
/// </summary>
public sealed class StatsFailureCounters : IStatsFailureState
{
    private long _failureCount;
    private long _dropCount;
    private string? _lastError;

    // UTC ticks, with zero meaning "never". A nullable DateTimeOffset cannot be published with
    // Interlocked/Volatile (they take a reference type or a machine word), and a 64-bit tick count can be,
    // so the value a reader sees is always one a writer actually wrote rather than a half-updated struct.
    private long _lastSuccessfulWriteTicks;

    /// <param name="observer">The stable identifier for the observer these counters describe.</param>
    public StatsFailureCounters(string observer)
    {
        if (string.IsNullOrWhiteSpace(observer))
            throw new ArgumentException("An observer identifier is required.", nameof(observer));
        Observer = observer;
    }

    public string Observer { get; }

    public long FailureCount => Interlocked.Read(ref _failureCount);

    public long DropCount => Interlocked.Read(ref _dropCount);

    public string? LastError => Volatile.Read(ref _lastError);

    public DateTimeOffset? LastSuccessfulWrite
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastSuccessfulWriteTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>Record an attempt that failed, and what went wrong.</summary>
    /// <param name="error">A message already safe to serve - no connection string, no credential.</param>
    public void RecordFailure(string error)
    {
        Interlocked.Increment(ref _failureCount);
        Volatile.Write(ref _lastError, error);
    }

    /// <summary>Record an observation deliberately not attempted because the store is unavailable.</summary>
    public void RecordDrop()
    {
        Interlocked.Increment(ref _dropCount);
    }

    /// <summary>Record that something was actually stored, at <paramref name="whenUtc"/>.</summary>
    public void RecordSuccessfulWrite(DateTimeOffset whenUtc)
    {
        Interlocked.Exchange(ref _lastSuccessfulWriteTicks, whenUtc.ToUniversalTime().Ticks);
    }
}
