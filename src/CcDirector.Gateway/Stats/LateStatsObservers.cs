using CcDirector.Core.Utilities;
using CcDirector.Gateway.Stats.Data;

namespace CcDirector.Gateway.Stats;

/// <summary>
/// The hosted statistics observers, resolved WHEN THEY ARE FIRST NEEDED rather than once at startup.
///
/// WHY THIS EXISTS, AND WHY A ONE-TIME DECISION WAS WRONG. <see cref="GatewayStatsStore"/> bounds its
/// open-and-migrate on a clock, because a hosted Gateway has a platform startup deadline in front of its
/// port bind. When that deadline passes the attempt is NOT abandoned - the store says so explicitly and
/// keeps it running, and its <c>Factory</c> goes from null to non-null when the slow open finally
/// succeeds. That contract exists so a merely SLOW database costs the first seconds of one boot instead
/// of everything after it.
///
/// The first version of the hosted wiring read <c>Factory</c> exactly once, in the Gateway's constructor,
/// and froze whatever it saw. So a PostgreSQL cold start a shade over the deadline - the single most
/// likely hosted case, and the one the owner is about to exercise himself - left the store reporting
/// AVAILABLE while <c>/stats</c>, <c>/stats/data</c>, input recording and concurrency recording all stayed
/// dead until someone restarted the process. The store kept its promise and the caller threw it away.
///
/// So the decision is made at USE time. Both observers are built together, from the same factory, the
/// first time anything asks and the store has one.
///
/// THREE STATES, AND ONLY ONE OF THEM RETRIES:
///
///  - NOT YET. The store has no factory. Return nothing, ask again next time - this is the late-open
///    window, and it is expected to end on its own.
///  - RESOLVED. Built once, latched, and returned on every later call with no lock and no allocation.
///  - FAILED. The factory was there and building over it threw. LATCHED, deliberately: constructing an
///    aggregator loads the mirror, which is real queries, so retrying on every roster read would turn one
///    broken store into a query storm on the hottest path in the system. A factory that is present and
///    healthy but cannot be read is a defect, not a timing artefact, and it is reported as one - which is
///    also exactly what the startup path did before this type existed.
/// </summary>
public sealed class LateStatsObservers
{
    private readonly GatewayStatsStore _store;
    private readonly object _gate = new();

    private GatewayInputStatsAggregator? _aggregator;
    private ISessionConcurrencyRecorder? _concurrency;
    private string? _failure;

    public LateStatsObservers(GatewayStatsStore store) =>
        _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>The input-statistics aggregator over the hosted store, or null while there is not one.</summary>
    public GatewayInputStatsAggregator? Aggregator
    {
        get
        {
            Resolve();
            return Volatile.Read(ref _aggregator);
        }
    }

    /// <summary>The fleet concurrency recorder over the hosted store, or null while there is not one.
    /// Built in the same step as <see cref="Aggregator"/>, so the two halves of the statistics surface can
    /// never disagree about whether there is a store.</summary>
    public ISessionConcurrencyRecorder? Concurrency
    {
        get
        {
            Resolve();
            return Volatile.Read(ref _concurrency);
        }
    }

    /// <summary>Why there are no observers right now. Read from the STORE while the answer is still
    /// "not yet", so a caller waiting on a slow open is told what the store is actually doing rather than
    /// a sentence invented here; the latched build failure wins once there is one.</summary>
    public string Reason =>
        Volatile.Read(ref _failure)
        ?? $"The hosted statistics store is unavailable ({_store.Availability.ReasonCode}): {_store.Availability.Detail}";

    private void Resolve()
    {
        // The fast path, taken on every roster read and every hub push once resolution has settled either
        // way. No lock: both fields are written under the lock and published with Volatile.Write.
        if (Volatile.Read(ref _aggregator) is not null) return;
        if (Volatile.Read(ref _failure) is not null) return;

        var factory = _store.Factory;
        if (factory is null) return;   // NOT YET - the late open has not published one. Ask again.

        lock (_gate)
        {
            if (_aggregator is not null || _failure is not null) return;

            try
            {
                var aggregator = new GatewayInputStatsAggregator(factory);
                var concurrency = new GatewaySessionConcurrencyStore(factory);

                // Concurrency first, so that the instant the aggregator becomes visible to a lock-free
                // reader its partner already is. A reader that saw statistics arrive and concurrency still
                // missing would render half a surface for no reason.
                Volatile.Write(ref _concurrency, concurrency);
                Volatile.Write(ref _aggregator, aggregator);

                FileLog.Write(
                    "[LateStatsObservers] statistics are now AVAILABLE on the hosted store " +
                    $"(source={_store.Availability.Source} target={_store.Availability.Target}); " +
                    "resolved on first use, so a store that opened after the startup deadline is served rather than lost");
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _failure,
                    $"The hosted statistics store opened, but the statistics read path could not be built over it " +
                    $"({ex.GetType().Name}: {ex.Message}). Statistics are unavailable; the roster, the tunnels and " +
                    "every other Gateway surface are unaffected.");
                FileLog.Write($"[LateStatsObservers] statistics are UNAVAILABLE (ReadPathCouldNotBeBuilt): {_failure}");
            }
        }
    }
}
