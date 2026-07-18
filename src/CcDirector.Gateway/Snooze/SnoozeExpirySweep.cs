using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Snooze;

/// <summary>
/// A periodic pass over the registry that, as of round 2 finding 2, RETIRES NOTHING ON TIME. The passage
/// of a snooze's clock no longer deletes its entry; only an edge that actually ends a snooze does - work,
/// an owner turn, an exit, or a re-snooze overwrite. See <see cref="HandleEntry"/> for the full reasoning;
/// the short version is that the elapsed entry is the "Snooze ended" badge's only source, so deleting it
/// on a timer erased the badge before any five-second display fold or eight-second web-push poll could see
/// it, and a genuine expiry could show no badge at all.
///
/// A session still returns to "needs you" the instant its clock runs out, with no sweep and no round trip:
/// <see cref="SnoozeRegistry.HoldStateFor"/> reports an elapsed entry as None on every read. The entry
/// lingers only as a durable returned-by-timer tombstone (<see cref="SnoozeRegistry.IsExpired"/> stays
/// true) until a consumer sees it and an end-of-snooze edge clears it. The registry stays bounded by the
/// live-session prune paths (<c>PruneNotLive</c> / <c>ClearForDirector</c>).
///
/// WHAT THIS USED TO BE. The hold state lived on the owning Director and the clock lived here, so expiry
/// was negotiated between two processes: read each Director's raw hold over the tunnel, interpret a
/// tri-state, back-stop a missed landing, compare-and-clear a racing re-snooze, nudge the Director off
/// hold, keep the entry until it agreed, and a dead-man's-switch for an unreachable Director. All of that
/// went when the Gateway took ownership of both the state and the clock (defect 20's boolean read is
/// unreachable now). This round removed the last thing it still did - deleting an elapsed entry - because
/// that delete was destroying the badge fact.
///
/// Already-past entries at startup need no special handling: the registry loads them back and they read as
/// expired (and now durable) on the first read after boot.
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
    /// Look at one entry - and, for an elapsed one, DO NOTHING. The passage of time no longer retires a
    /// snooze (owner's returned-by-timer rule, round 2 finding 2).
    ///
    /// Why this stopped deleting. The "Snooze ended" badge's ONLY source is the elapsed entry itself:
    /// the fold computes <c>SnoozeExpired = IsExpired(entry)</c>, which is true only while that entry
    /// exists. This sweep used to delete the entry the moment it saw expiry - about twelve seconds after
    /// the clock ran out - and the desktop's display fold (every five seconds) and the web-push poll
    /// (every eight seconds) could BOTH miss the one-second window in between, so a genuine expiry could
    /// show no badge at all. Retiring on time traded the old stuck-on badge for a never-shown one.
    ///
    /// So an elapsed snooze now LINGERS as an armed-but-elapsed tombstone. That is not a leak and not a
    /// held session: <see cref="SnoozeRegistry.HoldStateFor"/> already reports an elapsed entry as None on
    /// every read (so it is correctly "needs you", never "Snoozed"), while <see cref="SnoozeRegistry.IsExpired"/>
    /// stays true, so the badge is DURABLE until a consumer sees it. The entry is deleted only by an edge
    /// that actually ends a snooze - work (<c>ClearIfArmed</c>: an elapsed entry is not deferred, so it is
    /// removed), an owner turn, an exit, or a re-snooze overwrite (which arms a fresh clock, so
    /// <c>IsExpired</c> goes false and the badge clears). The registry stays bounded by the live-session
    /// prune paths (<c>PruneNotLive</c> / <c>ClearForDirector</c>), untouched by this change.
    /// </summary>
    private void HandleEntry(SnoozeRegistry.SnoozeEntry entry, DateTime now)
    {
        // Nothing to do, for any entry. A future or deferred entry has not elapsed; an elapsed one is a
        // durable returned-by-timer tombstone that only an end-of-snooze edge clears. Time never retires a
        // snooze. This method is kept (rather than the loop removed) so the sweep's structure and its
        // per-entry isolation remain, in case a future non-destructive per-entry task is added here.
        _ = (entry, now);
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
