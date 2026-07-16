using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Snooze;

/// <summary>
/// Retires snooze entries whose clock has run out. That is all it does, and it is bookkeeping rather than
/// correctness: the fold reads <see cref="SnoozeRegistry.HoldStateFor"/>, which reports an elapsed entry
/// as not-held the instant it elapses, on every read. A session returns to "needs you" the moment its
/// snooze is up whether or not this sweep has run yet. Dropping the entry just stops the registry growing.
///
/// WHAT THIS USED TO BE, AND WHY IT ISN'T. The hold state lived on the owning Director and the clock lived
/// here, so expiry had to be negotiated between two processes across a network. Every 15 seconds this
/// class read each Director's raw hold over the tunnel, interpreted a tri-state, acted as a BACKSTOP for a
/// landing missed on the push seam, compare-and-cleared so a racing re-snooze survived, nudged the live
/// Director off hold, and kept the entry until that Director agreed. It also needed a dead-man's-switch,
/// because a dead Director stranded a hold nobody else could release.
///
/// All of it is gone, because the premise is gone: the Gateway owns the state AND the clock, so there is
/// nobody to ask and nobody to nudge. Defect 20 - the one that deleted a twelve-hour timer 15 seconds
/// after it was asked for, by reading a boolean that could not tell "deferred" from "not held" - is not
/// defended against here any more. It is unreachable: this sweep never asks anyone whether a session is
/// held, because it already knows.
///
/// Already-past entries at startup need no special handling: the registry loads them back and they read as
/// expired on the first read after boot.
/// </summary>
public sealed class SnoozeExpirySweep : IDisposable
{
    /// <summary>How often the watchdog re-evaluates the registry.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

    /// <summary>A short settling delay before the first sweep, so startup finishes first (and any
    /// already-past entry fires promptly, one interval after boot at the latest).</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(12);

    private readonly SnoozeRegistry _registry;
    private readonly Func<DateTime> _utcNow;

    private System.Threading.Timer? _timer;
    private int _busy; // 0 = idle, 1 = a tick is running (reentrancy guard)

    /// <param name="registry">The pending-snooze registry to sweep.</param>
    /// <param name="utcNow">The clock; injected so the expiry boundary is deterministic in tests.</param>
    ///
    /// <remarks>
    /// This used to take three more dependencies - is the owning Director reachable, read its hold over
    /// the tunnel, and nudge it off hold - and every one of them is gone. They existed because the hold
    /// state lived on a Director while its clock lived here, so expiry meant negotiating with another
    /// process across a network. The Gateway owns both now, and a timer that runs out is a local fact.
    /// </remarks>
    public SnoozeExpirySweep(
        SnoozeRegistry registry,
        Func<DateTime> utcNow)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    /// <summary>Start the background sweep. Returns immediately; the first tick runs after a short delay.</summary>
    public void Start()
    {
        _timer = new System.Threading.Timer(_ => Tick(), null, StartupDelay, SweepInterval);
        FileLog.Write($"[SnoozeExpirySweep] started: every {SweepInterval.TotalSeconds:0}s");
    }

    /// <summary>
    /// One sweep pass over the whole registry. Public so a test can drive it directly. Each entry is
    /// handled independently; a failure on one entry is logged and never stops the others.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = _utcNow().ToUniversalTime();
        foreach (var entry in _registry.Entries())
        {
            if (cancellationToken.IsCancellationRequested) return;
            try
            {
                HandleEntry(entry, now);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SnoozeExpirySweep] entry sid={entry.SessionId} FAILED: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Retire an entry whose clock has run out. That is the sweep's WHOLE job now.
    ///
    /// It used to be a distributed-consensus engine: read the owning Director's hold over the tunnel,
    /// interpret its tri-state, act as a BACKSTOP for a landing missed on the push seam, compare-and-clear
    /// so a racing re-snooze was not clobbered, then nudge the Director off hold and wait for it to agree.
    /// Every line of that existed for one reason - the state lived on a Director and the clock lived here,
    /// so the two had to be reconciled across a network.
    ///
    /// They are the same object now. There is nobody to ask and nobody to nudge: the fold reads
    /// <see cref="SnoozeRegistry.HoldStateFor"/>, which reports an elapsed entry as None the instant it
    /// elapses, on every read, with no round trip. Dropping the entry here is bookkeeping, not correctness
    /// - and it is why an unreachable Director no longer needs a dead-man's-switch. A dead Director cannot
    /// strand a hold it never owned.
    /// </summary>
    private void HandleEntry(SnoozeRegistry.SnoozeEntry entry, DateTime now)
    {
        if (!_registry.IsExpired(entry.SessionId, now))
            return; // deferred (no clock yet) or still running. Nothing to do.

        // Compare-and-clear against the snapshot this pass took, so a re-snooze that landed while the pass
        // was running is never destroyed by a stale expiry decision. The fresh snooze wins.
        if (_registry.ClearIfUnchanged(entry.SessionId, entry.SnoozeUntilUtc, entry.PendingMinutes))
            FileLog.Write($"[SnoozeExpirySweep] sid={entry.SessionId}: snooze elapsed (untilUtc={entry.SnoozeUntilUtc:O}) -> entry retired; the session was already reading as not-held from the moment it expired");
    }

    private void Tick()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return; // a previous tick is still running - skip this one
        _ = TickAsync();
    }

    private async Task TickAsync()
    {
        try
        {
            await RunOnceAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SnoozeExpirySweep] tick FAILED: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }
}
