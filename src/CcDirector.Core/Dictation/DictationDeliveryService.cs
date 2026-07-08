using CcDirector.Core.Drivers;
using CcDirector.Core.Transcription;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Dictation;

/// <summary>
/// The single engine that turns a durably-saved dictation into a delivered turn, and decides whether to
/// delete it (delivered) or keep it for a later retry (issue #1130). Both the immediate post-Send
/// attempt and the background sweep / next-launch re-drive go through here, so the keep-vs-delete rule
/// lives in exactly one tested place.
///
/// The contract, mirroring the mobile durable-dictation contract (issue #1006/#1056):
///   * The audio is deleted ONLY on a delivered outcome. Every failure keeps the saved audio.
///   * A transient failure (slow/unavailable transcription service, a 5xx/429, a network drop) leaves
///     the clip <see cref="PendingDictationStatus.Pending"/> - the sweeper will retry it automatically.
///   * A "needs the user" failure (out of credits, no key configured) parks it
///     <see cref="PendingDictationStatus.NeedsAttention"/> so the sweeper stops hammering a call that
///     will keep failing; the next launch promotes it back to try once more.
///   * Success is silent - the delivered turn is the proof.
///
/// This engine is deliberately UI-free: it takes the submit action as a delegate and returns a
/// structured <see cref="DictationDeliveryResult"/>; the desktop layer maps that to notifications.
/// </summary>
public sealed class DictationDeliveryService
{
    private readonly IDictationTranscriber _transcriber;
    private readonly PendingDictationStore _store;

    public DictationDeliveryService(IDictationTranscriber transcriber, PendingDictationStore store)
    {
        _transcriber = transcriber ?? throw new ArgumentNullException(nameof(transcriber));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <summary>
    /// Attempt to transcribe and deliver one saved clip exactly once. Reads the audio back from the
    /// store (proving the durable copy is intact), transcribes it, joins it onto the clip's prefix, and
    /// hands the text to <paramref name="submit"/>. On success the clip is deleted; on any failure the
    /// clip is kept with its status updated, and the failure is classified into the returned result.
    /// Never throws for a transcription/submit failure - the whole point is that a failure is a held
    /// clip, not a lost one. (An empty transcript is still a delivered outcome: the audio was silence,
    /// the turn is a no-op the submit delegate guards, and keeping the clip would retry silence forever.)
    /// </summary>
    public async Task<DictationDeliveryResult> DeliverAsync(
        PendingDictation pending, Func<string, Task> submit, Func<bool>? isSessionReady = null, CancellationToken ct = default)
    {
        if (pending is null) throw new ArgumentNullException(nameof(pending));
        if (submit is null) throw new ArgumentNullException(nameof(submit));

        // Readiness gate (issue #1135): only type a dictation into a session whose composer is idle at
        // the prompt. Typing while the agent is Working (streaming output) makes the echo-verified submit
        // false-negative and throw AFTER the text has already landed in the composer; the durable retry
        // loop then re-types the same words on the next sweep, piling up duplicate copies of the one
        // sentence. Deferring here - before any transcription or attempt bump - leaves the clip Pending
        // and untouched, so a later sweep delivers it the moment the session returns to the prompt. The
        // clip is durable on disk, so deferring loses nothing.
        if (isSessionReady is not null && !isSessionReady())
        {
            FileLog.Write($"[DictationDeliveryService] deferred id={pending.Id}: session not ready for input");
            return new DictationDeliveryResult(DictationDeliveryOutcome.DeferredSessionBusy, pending, null);
        }

        byte[] audio;
        try
        {
            audio = _store.ReadAudio(pending);
        }
        catch (Exception ex)
        {
            // The saved audio is unreadable (deleted out from under us, disk error). Nothing to deliver
            // and nothing to keep - report it loudly rather than silently dropping.
            FileLog.Write($"[DictationDeliveryService] audio unreadable id={pending.Id}: {ex.Message}");
            return new DictationDeliveryResult(DictationDeliveryOutcome.LostNoAudio, pending, ex.Message);
        }

        try
        {
            var transcript = await _transcriber.TranscribeAsync(audio, ct);
            // Reproduce the exact composed turn: the dictation (this segment joined onto any earlier
            // paused segments) inserted at the caret inside whatever the user had typed. Persisting
            // Before/After means a background retry drops neither the typed text nor the dictation.
            var dictation = Join(pending.Prefix, transcript.CleanedTranscript);
            var text = InsertAtCaret(pending.Before, pending.After, dictation).Trim();
            await submit(text);
            _store.Delete(pending);
            FileLog.Write($"[DictationDeliveryService] delivered id={pending.Id}, chars={text.Length}");
            return new DictationDeliveryResult(DictationDeliveryOutcome.Delivered, pending, null);
        }
        catch (InsufficientCreditsException ex)
        {
            var kept = _store.RecordFailedAttempt(pending, ex.Message, PendingDictationStatus.NeedsAttention);
            return new DictationDeliveryResult(DictationDeliveryOutcome.NeedsCredits, kept, ex.Message);
        }
        catch (TranscriptionUnavailableException ex)
        {
            var kept = _store.RecordFailedAttempt(pending, ex.Message, PendingDictationStatus.NeedsAttention);
            return new DictationDeliveryResult(DictationDeliveryOutcome.NeedsConfiguration, kept, ex.Message);
        }
        catch (TranscriptionFailedException ex) when (!ex.IsTransient)
        {
            // A permanent provider rejection (a 4xx other than 402/408/429): retrying sends the identical
            // request for the identical rejection. Park it so the sweeper stops, but keep the audio.
            var kept = _store.RecordFailedAttempt(pending, ex.Message, PendingDictationStatus.NeedsAttention);
            return new DictationDeliveryResult(DictationDeliveryOutcome.PermanentError, kept, ex.Message);
        }
        catch (ComposerNotAcceptingInputException ex)
        {
            // The transcription succeeded but the target session's composer never echoed the typed text,
            // so the submit threw AFTER typing (issue #1135): the session is at Claude Code's startup
            // splash or otherwise wedged. Re-typing on the next sweep would stack another unsubmitted copy
            // - the exact pile-up. Park it ComposerBlocked (kept, but skipped by the sweep) so a real
            // submit-probe failure - NOT a mere brand-new session - is what stops the re-typing; the next
            // launch promotes it back to Pending for one more probe once the session has been recreated.
            // Crucially this is caught BEFORE the generic transient branch below, so a transcription
            // failure (nothing typed) still lands there and keeps auto-retrying - the durable guarantee
            // for the first dictated turn is preserved.
            var kept = _store.RecordFailedAttempt(pending, ex.Message, PendingDictationStatus.ComposerBlocked);
            return new DictationDeliveryResult(DictationDeliveryOutcome.ComposerNotReady, kept, ex.Message);
        }
        catch (Exception ex)
        {
            // Everything else - a transient 5xx/429 (the 504 upstream_timeout that started all this), a
            // network drop, a timeout - is retryable. Keep it Pending so the sweeper tries again.
            var kept = _store.RecordFailedAttempt(pending, ex.Message, PendingDictationStatus.Pending);
            return new DictationDeliveryResult(DictationDeliveryOutcome.HeldWillRetry, kept, ex.Message);
        }
    }

    /// <summary>
    /// Join an already-transcribed prefix and this segment's transcript with exactly one separating
    /// space unless a boundary space already exists - the same rule the desktop dialog uses so a
    /// re-driven clip reads identically to a live one.
    /// </summary>
    private static string Join(string left, string right)
    {
        if (string.IsNullOrEmpty(left)) return right ?? "";
        if (string.IsNullOrEmpty(right)) return left;
        var leftEndsWithSpace = char.IsWhiteSpace(left[^1]);
        var rightStartsWithSpace = char.IsWhiteSpace(right[0]);
        if (leftEndsWithSpace || rightStartsWithSpace) return left + right;
        return left + " " + right;
    }

    /// <summary>
    /// Insert <paramref name="insert"/> between the typed <paramref name="before"/> and
    /// <paramref name="after"/> halves, adding one separating space on a side only when the adjacent
    /// character is not already whitespace - the same rule the desktop Insert button uses, so a re-driven
    /// send lands the words exactly where a live one would. With both halves empty this returns the
    /// dictation unchanged (the common "just talk and Send" case).
    /// </summary>
    private static string InsertAtCaret(string before, string after, string insert)
    {
        before ??= "";
        after ??= "";
        if (string.IsNullOrEmpty(insert)) return before + after;
        var needsSpaceBefore = before.Length > 0 && !char.IsWhiteSpace(before[^1]);
        var needsSpaceAfter = after.Length > 0 && !char.IsWhiteSpace(after[0]);
        var mid = (needsSpaceBefore ? " " : "") + insert + (needsSpaceAfter ? " " : "");
        return before + mid + after;
    }
}

/// <summary>How a delivery attempt ended.</summary>
public enum DictationDeliveryOutcome
{
    /// <summary>Transcribed and submitted into the session; the saved clip was deleted.</summary>
    Delivered,

    /// <summary>A transient failure (slow/unavailable service, 5xx/429, network drop). Audio kept,
    /// status Pending - the sweeper will retry automatically.</summary>
    HeldWillRetry,

    /// <summary>Out of transcription credits. Audio kept, parked NeedsAttention until the user adds credits.</summary>
    NeedsCredits,

    /// <summary>No transcription method configured (no key, or Gateway unreachable). Audio kept, parked
    /// NeedsAttention until the user sets one.</summary>
    NeedsConfiguration,

    /// <summary>A permanent provider rejection (a non-retryable 4xx). Audio kept, parked NeedsAttention;
    /// retrying would fail identically.</summary>
    PermanentError,

    /// <summary>The saved audio could not be read back (disk error / removed). Nothing to deliver -
    /// reported, not silent.</summary>
    LostNoAudio,

    /// <summary>Transcription succeeded but the target session's composer never echoed the typed text, so
    /// the submit threw after typing (the session is at the startup splash or wedged). Audio kept, parked
    /// <see cref="PendingDictationStatus.ComposerBlocked"/> so the sweep stops re-typing and stacking
    /// copies (issue #1135); the next launch promotes it for one more probe.</summary>
    ComposerNotReady,

    /// <summary>The target session was not idle at its prompt (Working, on a permission prompt, or
    /// starting), so the clip was deferred WITHOUT transcribing, submitting, or bumping its attempt
    /// count (issue #1135). Audio kept, status left Pending; a later sweep delivers it once the session
    /// returns to the prompt. Not a failure - the words were never at risk.</summary>
    DeferredSessionBusy,
}

/// <summary>The result of one delivery attempt: how it ended, the (updated) record, and any error text.</summary>
public sealed record DictationDeliveryResult(DictationDeliveryOutcome Outcome, PendingDictation Pending, string? Error)
{
    /// <summary>True when the clip was delivered and removed from the store.</summary>
    public bool Delivered => Outcome == DictationDeliveryOutcome.Delivered;

    /// <summary>True when the clip is still saved and eligible for an automatic background retry.</summary>
    public bool WillRetryAutomatically => Outcome == DictationDeliveryOutcome.HeldWillRetry;
}
