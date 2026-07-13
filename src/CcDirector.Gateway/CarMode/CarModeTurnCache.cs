using System.Collections.Concurrent;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.CarMode;

/// <summary>
/// Idempotency + single-flight cache for POST /carmode/turn (Car Mode mission, offline-resilience Phase
/// 4b, issue #1427). It makes a Car Mode turn ACT and APPEND to the conversation AT MOST ONCE per
/// client-supplied turn key, so the one ambiguous case Phase 4a had to hold for the owner - a turn whose
/// brain call was already SENT but whose result was lost on the way back - can now auto-retry safely.
///
/// How it works. The client sends its durable command-audio record id (the same id that keys the on-device
/// pending-turn store) as the Idempotency-Key. The first request for a key runs the brain and caches the
/// result; any concurrent or later duplicate for the SAME key awaits or returns that SAME cached result,
/// WITHOUT re-running the tool loop - so the fleet action (start / message / approve / confirmed-delete)
/// and the per-device conversation append happen exactly once.
///
/// Single-flight is the crux for the dead zone: the brain must run to completion even after the client has
/// disconnected, so the endpoint runs it on <see cref="CancellationToken.None"/> (not the request token).
/// A client that drops mid-turn therefore does not abort the work - it finishes and caches, and the
/// client's retry gets the cached result instead of re-acting.
///
/// Cache policy: SUCCESS only. On a brain EXCEPTION the key is EVICTED so a transient failure can still
/// recover on the next retry (caching a failure would trap the error and block recovery). Two residual,
/// accepted-for-v1 double-act windows, both rare and annoying-not-catastrophic (and delete is idempotent),
/// documented per the Architect's decision (2026-07-13):
///   1. A brain exception thrown AFTER a tool already acted: the key is evicted, so the retry re-runs and
///      may act again.
///   2. A Gateway RESTART between the original call and the retry: this in-memory cache (and the in-memory
///      conversation store) are lost, so the retry re-runs. Gateway restarts are independent of the owner's
///      connection drops, so a real dead-zone drive (Gateway up throughout) is fully safe.
/// Durable persistence is deliberately NOT built here; it is tracked as a follow-up (issue #1458).
///
/// In-memory and per-device-keyed, mirroring <see cref="CarModePendingStore"/> and
/// <see cref="CarModeConversationStore"/>. Entries expire after a TTL aligned to the client's staleness cap
/// (a held turn older than that is surfaced to the owner rather than auto-fired), so the cache never grows
/// unbounded. Thread-safe: duplicates can arrive concurrently from the same device.
/// </summary>
public sealed class CarModeTurnCache
{
    private sealed record Entry(Lazy<Task<CarModeTurnResponse>> Work, DateTime CreatedUtc);

    /// <summary>How long a cached turn result is retained. Aligned to the client's 30-minute staleness cap:
    ///  past that the client asks the owner before re-firing, so a cached result is no longer needed.</summary>
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, Entry> _byKey = new(StringComparer.Ordinal);
    private readonly Action<string> _log;

    public CarModeTurnCache(Action<string>? log = null) => _log = log ?? FileLog.Write;

    /// <summary>
    /// Run the turn for (device, turnKey) exactly once, or return the cached / in-flight result for a
    /// duplicate. <paramref name="run"/> MUST run the brain on a token that is NOT the request's, so a
    /// client disconnect does not abort the work (see the class remarks). The returned task faults if the
    /// brain throws, and the key is evicted so a retry re-runs.
    /// </summary>
    public Task<CarModeTurnResponse> GetOrRunAsync(string deviceKey, string turnKey, Func<Task<CarModeTurnResponse>> run)
    {
        SweepExpired();
        var k = Compose(deviceKey, turnKey);
        // The Lazy is created but not started by the GetOrAdd factory; only .Value on the STORED entry
        // starts the work, so even under contention run() executes exactly once (losing Lazy instances are
        // discarded without ever being started). ExecutionAndPublication is the Lazy default.
        var entry = _byKey.GetOrAdd(k, _ => new Entry(
            new Lazy<Task<CarModeTurnResponse>>(() => RunAndEvictOnFailureAsync(k, run)),
            DateTime.UtcNow));
        if (entry.Work.IsValueCreated)
            _log("[CarModeTurnCache] duplicate turn key - returning the single-flight / cached result (no re-run)");
        return entry.Work.Value;
    }

    private async Task<CarModeTurnResponse> RunAndEvictOnFailureAsync(string k, Func<Task<CarModeTurnResponse>> run)
    {
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch
        {
            // Do NOT cache a failure: evict so a later retry with the same key re-runs and can recover from a
            // transient error. (A brain throw AFTER a partial action can therefore double-act on retry - the
            // accepted residual documented in the class remarks.)
            _byKey.TryRemove(k, out _);
            throw;
        }
    }

    /// <summary>Number of cached / in-flight keys (for tests and diagnostics).</summary>
    public int Count()
    {
        SweepExpired();
        return _byKey.Count;
    }

    /// <summary>Forget everything (for tests).</summary>
    public void Clear() => _byKey.Clear();

    private void SweepExpired()
    {
        var cutoff = DateTime.UtcNow - Ttl;
        foreach (var kv in _byKey)
        {
            if (kv.Value.CreatedUtc < cutoff)
                _byKey.TryRemove(kv.Key, out _);
        }
    }

    // A NUL separator so a device key and a turn key can never collide across the boundary. A blank device
    // key (auth-off debug) maps to one shared anonymous namespace, like the other Car Mode stores.
    private static string Compose(string? deviceKey, string turnKey)
        => (string.IsNullOrWhiteSpace(deviceKey) ? "anonymous" : deviceKey) + "\0" + turnKey;
}
