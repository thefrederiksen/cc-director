using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Snooze;

/// <summary>
/// The Gateway-owned watchdog that makes a snooze always come back (Snooze Length mission,
/// docs/architecture/snooze-length-mission-2026-07-11.md). On a fixed cadence it walks the
/// <see cref="SnoozeRegistry"/> and, for each pending snooze, decides one of three things from the
/// OWNING DIRECTOR'S RAW state (read straight from the Director, NOT from the overlaid /sessions
/// roster, so the aggregation overlay can never mask the Director's own signal):
///
///   * The Director reports the session is NO LONGER held (OnHold=false) - the session came back on
///     its own (a new turn or a keystroke cleared the hold, issue #470) OR a prior expiry nudge has
///     now taken. The snooze has served its purpose, so the entry is CLEARED.
///   * The snooze has EXPIRED (now &gt;= SnoozeUntilUtc) and the Director is still holding it - the
///     session went quiet and never came back on its own, the exact stuck/dead population the
///     watchdog exists to surface. The sweep NUDGES the live Director off hold (so its own state and
///     voice rotation agree) and KEEPS the entry; the next sweep sees the Director report OnHold=false
///     and clears it. The entry is kept across the nudge on purpose: the aggregation overlay pins the
///     session to "needs you" CONTINUOUSLY from the instant of expiry, so there is never a beat where
///     the roster flashes back to "Snoozed".
///   * The owning Director is UNREACHABLE (dead/offline). The sweep does NOTHING and KEEPS the entry -
///     this is the dead-man's-switch: the aggregation overlay surfaces the session from the cached
///     roster as "needs you" without the Director's help. The entry is dropped only when that Director
///     is removed from the fleet (SnoozeRegistry.ClearForDirector via Registry.OnDirectorRemoved).
///
/// The sweep NEVER clears an entry merely because a read came back empty (a transient miss or a
/// momentarily-unreachable Director must not lose a pending snooze); "the session permanently left
/// the roster" is handled authoritatively by the aggregation (a reachable Director's live set prunes
/// exited sessions) and by Director removal - not by this sweep guessing from one failed read.
///
/// Already-past entries at startup need no special handling: the registry re-arms them on load, and
/// the first sweep tick reads them as expired and fires them immediately.
/// </summary>
public sealed class SnoozeExpirySweep : IDisposable
{
    /// <summary>How often the watchdog re-evaluates the registry.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

    /// <summary>A short settling delay before the first sweep, so startup finishes first (and any
    /// already-past entry fires promptly, one interval after boot at the latest).</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(12);

    private readonly SnoozeRegistry _registry;
    private readonly Func<string, string?> _resolveEndpoint;
    private readonly Func<string, string, CancellationToken, Task<bool?>> _readOnHold;
    private readonly Func<string, string, CancellationToken, Task> _forwardUnhold;
    private readonly Func<DateTime> _utcNow;

    private System.Threading.Timer? _timer;
    private int _busy; // 0 = idle, 1 = a tick is running (reentrancy guard)

    /// <param name="registry">The pending-snooze registry to sweep.</param>
    /// <param name="resolveEndpoint">
    /// Maps an owning Director id to the base URL the Gateway dials to reach it, or null when that
    /// Director has no reachable endpoint (offline/dead) - the dead-man's-switch case, which the sweep
    /// leaves untouched.
    /// </param>
    /// <param name="readOnHold">
    /// Reads the owning Director's RAW hold state for a session: true = still held, false = no longer
    /// held, null = the session is absent from that Director or the read did not land. Reads the
    /// Director directly, never the overlaid roster.
    /// </param>
    /// <param name="forwardUnhold">Forwards a hold=false to the owning Director (the expiry nudge).</param>
    /// <param name="utcNow">The clock; injected so the expiry boundary is deterministic in tests.</param>
    public SnoozeExpirySweep(
        SnoozeRegistry registry,
        Func<string, string?> resolveEndpoint,
        Func<string, string, CancellationToken, Task<bool?>> readOnHold,
        Func<string, string, CancellationToken, Task> forwardUnhold,
        Func<DateTime> utcNow)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _resolveEndpoint = resolveEndpoint ?? throw new ArgumentNullException(nameof(resolveEndpoint));
        _readOnHold = readOnHold ?? throw new ArgumentNullException(nameof(readOnHold));
        _forwardUnhold = forwardUnhold ?? throw new ArgumentNullException(nameof(forwardUnhold));
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
                await HandleEntryAsync(entry, now, cancellationToken);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SnoozeExpirySweep] entry sid={entry.SessionId} FAILED: {ex.Message}");
            }
        }
    }

    private async Task HandleEntryAsync(SnoozeRegistry.SnoozeEntry entry, DateTime now, CancellationToken ct)
    {
        var endpoint = _resolveEndpoint(entry.DirectorId);
        if (endpoint is null)
        {
            // Owning Director unreachable/dead: leave the entry alone. The aggregation overlay
            // surfaces the session as "needs you" from the cached roster (the dead-man's-switch);
            // the entry is dropped only when the Director is removed from the fleet.
            return;
        }

        var onHold = await _readOnHold(endpoint, entry.SessionId, ct);
        if (onHold == false)
        {
            // The Director itself reports the session is no longer held - it came back on its own
            // (issue #470) or a prior nudge took. The snooze is done; clear it - but only if the entry is
            // unchanged since this pass snapshotted it, so a re-snooze that landed in between is never
            // clobbered (compare-and-clear; the fresh snooze wins).
            if (_registry.ClearIfUnchanged(entry.SessionId, entry.SnoozeUntilUtc))
                FileLog.Write($"[SnoozeExpirySweep] sid={entry.SessionId}: director reports not-held -> cleared");
            return;
        }

        // Re-check expiry against the LIVE registry (not the snapshot): if the user re-snoozed this
        // session since the pass began, its time has moved into the future and it must NOT be nudged off
        // hold. This keeps a fresh snooze from being cancelled by a stale expiry decision.
        if (onHold == true && _registry.IsExpired(entry.SessionId, now))
        {
            // Expired and still held on a live Director: nudge it off hold so its own state and voice
            // rotation resume. Keep the entry; the overlay already reads the session as "needs you",
            // and the next sweep clears the entry once the Director reports OnHold=false.
            FileLog.Write($"[SnoozeExpirySweep] sid={entry.SessionId}: expired (untilUtc={entry.SnoozeUntilUtc:O}) -> nudging director off hold");
            await _forwardUnhold(endpoint, entry.SessionId, ct);
        }

        // onHold == null (session absent from a reachable Director, or the read did not land): keep the
        // entry. A permanently-exited session is pruned authoritatively by the aggregation, not guessed
        // at here.
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
