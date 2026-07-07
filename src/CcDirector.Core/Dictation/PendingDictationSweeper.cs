using CcDirector.Core.Utilities;

namespace CcDirector.Core.Dictation;

/// <summary>
/// Drives the durable pending-dictation store (issue #1130): it prunes truly stale clips, promotes
/// parked clips back for another try at launch, and re-attempts delivery for every saved clip whose
/// session is currently present. The desktop layer owns the cadence (a timer + a launch scan + a
/// post-Send nudge) and calls in here; all the "which clips, deliver once each, do not double-deliver,
/// count what remains" logic lives here so it can be unit-tested with no UI and no timer.
///
/// Concurrency: a clip already being delivered (by an overlapping sweep or the immediate post-Send
/// attempt) is skipped, so the same audio is never transcribed or submitted twice at once.
/// </summary>
public sealed class PendingDictationSweeper
{
    /// <summary>
    /// How old a saved clip may get before it is pruned undelivered. Generous by design - on the desktop
    /// the saved audio is the only copy, so a clip is discarded only when it is so old its session is
    /// certainly gone. (The mobile store uses one hour; the desktop keeps days.)
    /// </summary>
    public static readonly TimeSpan DefaultStaleAfter = TimeSpan.FromDays(7);

    private readonly PendingDictationStore _store;
    private readonly DictationDeliveryService _delivery;
    private readonly TimeSpan _staleAfter;

    private readonly object _gate = new();
    private readonly HashSet<string> _inFlight = new(StringComparer.Ordinal);

    public PendingDictationSweeper(PendingDictationStore store, DictationDeliveryService delivery, TimeSpan? staleAfter = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        _staleAfter = staleAfter ?? DefaultStaleAfter;
    }

    /// <summary>
    /// Promote every parked (<see cref="PendingDictationStatus.NeedsAttention"/>) clip back to
    /// <see cref="PendingDictationStatus.Pending"/> so the next sweep retries it. Called once at launch:
    /// a clip parked for "out of credits" or "no key" gets a fresh chance in case the user has since
    /// fixed it. Returns how many were promoted.
    /// </summary>
    public int PromoteParkedToPending()
    {
        var promoted = 0;
        foreach (var record in _store.LoadAll())
        {
            if (record.Status == PendingDictationStatus.NeedsAttention)
            {
                _store.WriteSidecar(record with { Status = PendingDictationStatus.Pending });
                promoted++;
            }
        }
        if (promoted > 0) FileLog.Write($"[PendingDictationSweeper] promoted {promoted} parked clip(s) to pending");
        return promoted;
    }

    /// <summary>
    /// Deliver one specific clip (the immediate post-Send attempt, where the target session is known),
    /// honoring the in-flight guard. Returns the delivery result, or null if the clip was already being
    /// delivered by another caller.
    /// </summary>
    public async Task<DictationDeliveryResult?> TryDeliverAsync(
        PendingDictation pending, Func<string, Task> submit, Func<bool>? isSessionReady = null, CancellationToken ct = default)
    {
        if (pending is null) throw new ArgumentNullException(nameof(pending));
        if (!Claim(pending.Id)) return null;
        try
        {
            return await _delivery.DeliverAsync(pending, submit, isSessionReady, ct);
        }
        finally
        {
            Release(pending.Id);
        }
    }

    /// <summary>
    /// One full sweep: prune stale clips, then attempt delivery for every Pending clip whose session is
    /// currently present. <paramref name="resolveSubmit"/> maps a saved clip's SessionId to a submit
    /// delegate, or null when that session is not currently loaded (the clip is left for a later sweep).
    /// <paramref name="isSessionReady"/> reports whether that session's composer is idle at the prompt
    /// right now; a loaded-but-busy session defers WITHOUT being typed into (issue #1135). When null,
    /// every loaded session is treated as ready (the pre-#1135 behavior).
    /// Returns a tally the caller turns into the held notice.
    /// </summary>
    public async Task<SweepReport> SweepAsync(
        Func<string, Func<string, Task>?> resolveSubmit,
        Func<string, bool>? isSessionReady = null,
        CancellationToken ct = default)
    {
        if (resolveSubmit is null) throw new ArgumentNullException(nameof(resolveSubmit));

        var pruned = _store.PruneOlderThan(_staleAfter);
        var report = new SweepReport { Pruned = pruned };

        foreach (var record in _store.LoadAll())
        {
            ct.ThrowIfCancellationRequested();

            if (record.Status != PendingDictationStatus.Pending)
            {
                report.ParkedNeedingAttention++;
                continue;
            }

            var submit = resolveSubmit(record.SessionId);
            if (submit is null)
            {
                // The session is not loaded right now (a fresh launch before its workspace opened, or a
                // closed session). Keep the clip and try again on a later sweep.
                report.WaitingForSession++;
                continue;
            }

            var ready = isSessionReady is null ? (Func<bool>?)null : () => isSessionReady(record.SessionId);
            var result = await TryDeliverAsync(record, submit, ready, ct);
            if (result is null)
            {
                report.AlreadyInFlight++;
                continue;
            }

            report.Tally(result.Outcome);
        }

        FileLog.Write($"[PendingDictationSweeper] sweep: delivered={report.Delivered}, willRetry={report.HeldWillRetry}, "
            + $"needsCredits={report.NeedsCredits}, needsConfig={report.NeedsConfiguration}, permanent={report.PermanentError}, "
            + $"deferredBusy={report.DeferredSessionBusy}, waitingForSession={report.WaitingForSession}, "
            + $"parked={report.ParkedNeedingAttention}, pruned={report.Pruned}");
        return report;
    }

    private bool Claim(string id)
    {
        lock (_gate) return _inFlight.Add(id);
    }

    private void Release(string id)
    {
        lock (_gate) _inFlight.Remove(id);
    }
}

/// <summary>The tally of one sweep - what happened to each saved clip, for the held notice and the log.</summary>
public sealed class SweepReport
{
    public int Delivered { get; set; }
    public int HeldWillRetry { get; set; }
    public int NeedsCredits { get; set; }
    public int NeedsConfiguration { get; set; }
    public int PermanentError { get; set; }
    public int LostNoAudio { get; set; }
    public int WaitingForSession { get; set; }
    public int ParkedNeedingAttention { get; set; }
    public int AlreadyInFlight { get; set; }
    public int DeferredSessionBusy { get; set; }
    public int Pruned { get; set; }

    /// <summary>Clips still saved on disk after this sweep that the user should know are held - anything
    /// not delivered this pass and not a transient no-op. Drives whether the held notice is shown.</summary>
    public int StillHeld => HeldWillRetry + NeedsCredits + NeedsConfiguration + PermanentError
                            + WaitingForSession + ParkedNeedingAttention + AlreadyInFlight + DeferredSessionBusy;

    public void Tally(DictationDeliveryOutcome outcome)
    {
        switch (outcome)
        {
            case DictationDeliveryOutcome.Delivered: Delivered++; break;
            case DictationDeliveryOutcome.HeldWillRetry: HeldWillRetry++; break;
            case DictationDeliveryOutcome.NeedsCredits: NeedsCredits++; break;
            case DictationDeliveryOutcome.NeedsConfiguration: NeedsConfiguration++; break;
            case DictationDeliveryOutcome.PermanentError: PermanentError++; break;
            case DictationDeliveryOutcome.LostNoAudio: LostNoAudio++; break;
            case DictationDeliveryOutcome.DeferredSessionBusy: DeferredSessionBusy++; break;
        }
    }
}
