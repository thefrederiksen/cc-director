using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Snooze;

/// <summary>
/// The Gateway-owned watchdog that makes a snooze always come back (Snooze Length mission,
/// docs/architecture/snooze-length-mission-2026-07-11.md). On a fixed cadence it walks the
/// <see cref="SnoozeRegistry"/> and, for each pending snooze, decides what to do from the OWNING
/// DIRECTOR'S RAW HOLD STATE (read straight from the Director, NOT from the overlaid /sessions roster,
/// so the aggregation overlay can never mask the Director's own signal):
///
///   * <see cref="HoldState.None"/> - the session is genuinely NOT held: it came back on its own (a new
///     turn or a keystroke cleared the hold, issue #470) OR a prior expiry nudge has now taken. The
///     snooze has served its purpose, so the entry is CLEARED.
///   * <see cref="HoldState.DeferredHold"/> - the hold has been asked for and has NOT landed. The sweep
///     does NOTHING: there is no clock to expire yet, and there is nothing to clear. See the warning
///     below - this case is defect 20 and it is the reason this seam reads a tri-state.
///   * <see cref="HoldState.Held"/> - the hold is landed. If the entry is still DEFERRED in the
///     registry, the landing was missed on the push seam, so the sweep LANDS it here (the backstop) and
///     the clock starts now. If the snooze has then EXPIRED (now &gt;= SnoozeUntilUtc) - the session went
///     quiet and never came back on its own, the exact stuck/dead population the watchdog exists to
///     surface - the sweep NUDGES the live Director off hold (so its own state and voice rotation agree)
///     and KEEPS the entry; the next sweep sees the Director report None and clears it. The entry is
///     kept across the nudge on purpose: the aggregation overlay pins the session to "needs you"
///     CONTINUOUSLY from the instant of expiry, so there is never a beat where the roster flashes back
///     to "Snoozed".
///   * The owning Director is UNREACHABLE (dead/offline). The sweep does NOTHING and KEEPS the entry -
///     this is the dead-man's-switch: the aggregation overlay surfaces the session from the cached
///     roster as "needs you" without the Director's help. The entry is dropped only when that Director
///     is removed from the fleet (SnoozeRegistry.ClearForDirector via Registry.OnDirectorRemoved).
///
/// WHY THIS READS A TRI-STATE AND NOT A BOOLEAN - defect 20, fixed 14 July 2026. This seam used to read
/// <c>SessionDto.OnHold</c>, a single boolean. A DeferredHold reports <c>OnHold=false</c> - correctly, it
/// is not parked yet - so it was indistinguishable from None. The sweep runs every 15 seconds: an agent
/// snoozed its own session (which by definition happens while it is working, so the hold deferred), the
/// sweep asked "is it held?", heard "no", concluded the snooze was over, and DELETED the 12-hour timer.
/// The turn then ended, the deferral landed, and the session was held with no clock at all - an
/// agent-requested snooze NEVER EXPIRED. If you are about to make this read a boolean again, or clear an
/// entry because something reads "not held": don't. That is the bug, exactly, and it is the case the
/// feature exists to serve rather than an edge case.
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
    private readonly Func<string, bool> _isDirectorReachable;
    private readonly Func<string, string, CancellationToken, Task<HoldState?>> _readHoldState;
    private readonly Func<string, string, CancellationToken, Task> _forwardUnhold;
    private readonly Func<DateTime> _utcNow;

    private System.Threading.Timer? _timer;
    private int _busy; // 0 = idle, 1 = a tick is running (reentrancy guard)

    /// <param name="registry">The pending-snooze registry to sweep.</param>
    /// <param name="isDirectorReachable">
    /// Whether the owning Director (by id) is reachable at all - over the tunnel (stream-connected) OR over
    /// HTTP (an advertised endpoint). False when it is offline/dead: the dead-man's-switch case, which the
    /// sweep leaves untouched. (Gateway Cleanup mission, Phase 2 PR E-B3: this replaced the earlier
    /// resolve-to-an-endpoint gate so the sweep no longer depends on a dialable HTTP URL - the read/forward
    /// seams reach the Director by id, tunnel-first.)
    /// </param>
    /// <param name="readHoldState">
    /// Reads the owning Director's RAW hold state for a session, addressed by DIRECTOR ID: the full
    /// tri-state (<see cref="HoldState.None"/> / <see cref="HoldState.Held"/> /
    /// <see cref="HoldState.DeferredHold"/>), or null when the session is absent from that Director or the
    /// read did not land. Reads the Director directly (its own pushed/snapshotted state), never the
    /// overlaid roster. NOT a boolean - see the type-level warning: a boolean here is defect 20.
    /// </param>
    /// <param name="forwardUnhold">Forwards a hold=false to the owning Director (by id) - the expiry nudge.</param>
    /// <param name="utcNow">The clock; injected so the expiry boundary is deterministic in tests.</param>
    public SnoozeExpirySweep(
        SnoozeRegistry registry,
        Func<string, bool> isDirectorReachable,
        Func<string, string, CancellationToken, Task<HoldState?>> readHoldState,
        Func<string, string, CancellationToken, Task> forwardUnhold,
        Func<DateTime> utcNow)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _isDirectorReachable = isDirectorReachable ?? throw new ArgumentNullException(nameof(isDirectorReachable));
        _readHoldState = readHoldState ?? throw new ArgumentNullException(nameof(readHoldState));
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
        if (!_isDirectorReachable(entry.DirectorId))
        {
            // Owning Director unreachable/dead: leave the entry alone. The aggregation overlay
            // surfaces the session as "needs you" from the cached roster (the dead-man's-switch);
            // the entry is dropped only when the Director is removed from the fleet.
            return;
        }

        var holdState = await _readHoldState(entry.DirectorId, entry.SessionId, ct);

        if (holdState == HoldState.DeferredHold)
        {
            // DEFECT 20, THE WHOLE POINT OF THIS BRANCH. The hold has been asked for and has NOT landed:
            // the agent is still working. There is no clock running, so nothing can have expired; and the
            // snooze is very much still wanted, so there is nothing to clear. Do nothing and wait.
            //
            // This is where the old boolean seam destroyed the snooze: DeferredHold reports OnHold=false,
            // which the sweep read as "not held" and treated as the CLEAR case below - 15 seconds after
            // the snooze was asked for. Never merge these two branches.
            return;
        }

        if (holdState == HoldState.None)
        {
            // The Director itself reports the session is genuinely not held - it came back on its own
            // (issue #470) or a prior nudge took. The snooze is done; clear it - but only if the entry is
            // unchanged since this pass snapshotted it, so a re-snooze that landed in between is never
            // clobbered (compare-and-clear; the fresh snooze wins).
            if (_registry.ClearIfUnchanged(entry.SessionId, entry.SnoozeUntilUtc, entry.PendingMinutes))
                FileLog.Write($"[SnoozeExpirySweep] sid={entry.SessionId}: director reports not-held -> cleared");
            return;
        }

        if (holdState == HoldState.Held)
        {
            // The hold is landed. If the registry still has this entry DEFERRED, the landing was missed on
            // the push seam (the Director's hold-state delta), so land it here: this is the backstop, and
            // it is what starts the clock - the owner's ruling is that the clock starts when the work
            // ENDS, and a landed hold is exactly that moment. Land is idempotent, so an entry the push
            // seam already landed is untouched and its running clock is never restarted.
            if (_registry.Land(entry.SessionId, now))
                FileLog.Write($"[SnoozeExpirySweep] sid={entry.SessionId}: deferred hold seen landed -> clock started (sweep backstop)");

            // Re-check expiry against the LIVE registry (not the snapshot): if the user re-snoozed this
            // session since the pass began, or it just landed above, its time has moved into the future
            // and it must NOT be nudged off hold. This keeps a fresh snooze from being cancelled by a
            // stale expiry decision.
            if (_registry.IsExpired(entry.SessionId, now))
            {
                // Expired and still held on a live Director: nudge it off hold so its own state and voice
                // rotation resume. Keep the entry; the overlay already reads the session as "needs you",
                // and the next sweep clears the entry once the Director reports None.
                FileLog.Write($"[SnoozeExpirySweep] sid={entry.SessionId}: expired (untilUtc={entry.SnoozeUntilUtc:O}) -> nudging director off hold");
                await _forwardUnhold(entry.DirectorId, entry.SessionId, ct);
            }
            return;
        }

        // holdState == null (session absent from a reachable Director, or the read did not land): keep the
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
