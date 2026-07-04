using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Voice;

/// <summary>
/// The fire-and-forget desktop dictation send (docs/architecture/dictation/DICTATION_UX_SPEC.md
/// section 10), the desktop twin of the mobile PWA's background send. The Speak dialog handed us the
/// still-capturing recorder the instant Send was pressed and closed itself, releasing the screen;
/// this runs the rest - transcribe the clip, join it onto the leading text, and submit it into the
/// session - OFF the UI thread, while the session shows orange "Transcribing..." so nobody else
/// starts typing into it mid-dictation.
///
/// It marks <see cref="Session.IsTranscribing"/> for the duration (the SessionStatusWingman paints
/// the badge orange), and clears it in a finally so a transcription or submit failure never leaves a
/// session stuck orange. It owns the recorder and disposes it when done.
/// </summary>
public static class BackgroundDictationSend
{
    /// <summary>
    /// Transcribe <paramref name="recorder"/>'s clip, prepend <paramref name="prefix"/>, and hand the
    /// joined text to <paramref name="submit"/> - which the caller wires to its normal per-session
    /// submit path (it is responsible for marshaling to the UI thread if its submit touches UI). The
    /// session shows orange until this returns.
    /// </summary>
    /// <param name="recorder">The detached, still-capturing recorder. Owned and disposed here.</param>
    /// <param name="prefix">Already-transcribed text from earlier Pause/Resume segments, joined ahead
    /// of this segment's transcript to form the full dictation; may be empty.</param>
    /// <param name="target">The session the message is being dictated into (marked orange meanwhile).</param>
    /// <param name="submit">Places the dictated words (the caller inserts them at the caret inside any
    /// typed text) and submits the result. Called even for an empty dictation, because the caller may
    /// still have typed text to submit; the caller guards a fully-empty message itself.</param>
    /// <param name="onFailed">Invoked when transcription throws, so the caller can restore any typed
    /// text it cleared at dialog-close time (the send it was folded into never happened).</param>
    public static async Task RunAsync(BatchDictationRecorder recorder, string prefix, Session target, Func<string, Task> submit, Func<Task>? onFailed = null)
    {
        // This is the root of a detached background task (the dialog is already gone), so it is an
        // entry point: catch here so a transcription/submit failure is logged and the orange mark is
        // cleared, never left as an unobserved exception.
        FileLog.Write($"[BackgroundDictationSend] start: session={target.Id}");
        target.IsTranscribing = true;
        try
        {
            var result = await recorder.TranscribeAsync();
            var dictation = DictationText.Join(prefix, result.CleanedTranscript).Trim();
            // Hand the dictated words to the caller to place at the caret and submit. Always call it -
            // even for an empty dictation - because the caller may still have typed text to send; it
            // guards a fully-empty message itself, so a mis-tapped silent Send with an empty box sends
            // nothing.
            FileLog.Write($"[BackgroundDictationSend] transcribed {dictation.Length} chars for session {target.Id}");
            await submit(dictation);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BackgroundDictationSend] FAILED for session {target.Id}: {ex.Message}");
            if (onFailed is not null)
            {
                try { await onFailed(); }
                catch (Exception restoreEx) { FileLog.Write($"[BackgroundDictationSend] onFailed restore error: {restoreEx.Message}"); }
            }
        }
        finally
        {
            target.IsTranscribing = false;
            try { await recorder.DisposeAsync(); }
            catch (Exception ex) { FileLog.Write($"[BackgroundDictationSend] recorder dispose error: {ex.Message}"); }
        }
    }
}
