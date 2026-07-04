using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Voice;

/// <summary>
/// The fire-and-forget desktop dictation send (docs/architecture/dictation/DICTATION_UX_SPEC.md
/// section 10), the desktop twin of the mobile PWA's background send. The Speak dialog handed us the
/// still-capturing recorder the instant Send was pressed and closed itself, releasing the screen;
/// this runs the rest - transcribe the clip, join it onto any earlier paused text, and submit it into
/// the session - OFF the UI thread, while the session shows orange "Transcribing..." so nobody else
/// starts typing into it mid-dictation.
///
/// It marks <see cref="Session.IsTranscribing"/> for the duration (the SessionStatusWingman paints
/// the badge orange), and clears it in a finally so a transcription or submit failure never leaves a
/// session stuck orange. It owns the recorder and disposes it when done.
/// </summary>
public static class BackgroundDictationSend
{
    /// <summary>
    /// Transcribe <paramref name="recorder"/>'s clip, prepend <paramref name="prefix"/> (earlier
    /// paused segments), and hand the joined text to <paramref name="submit"/> - which the caller
    /// wires to its normal per-session submit path (it is responsible for marshaling to the UI thread
    /// if its submit touches UI). The session shows orange until this returns.
    /// </summary>
    /// <param name="recorder">The detached, still-capturing recorder. Owned and disposed here.</param>
    /// <param name="prefix">Already-transcribed text from earlier Pause/Resume segments; may be empty.</param>
    /// <param name="target">The session the message is being dictated into (marked orange meanwhile).</param>
    /// <param name="submit">Submits the finished text into the session. Not called for an empty clip.</param>
    public static async Task RunAsync(BatchDictationRecorder recorder, string prefix, Session target, Func<string, Task> submit)
    {
        // This is the root of a detached background task (the dialog is already gone), so it is an
        // entry point: catch here so a transcription/submit failure is logged and the orange mark is
        // cleared, never left as an unobserved exception.
        FileLog.Write($"[BackgroundDictationSend] start: session={target.Id}");
        target.IsTranscribing = true;
        try
        {
            var result = await recorder.TranscribeAsync();
            var text = DictationText.Join(prefix, result.CleanedTranscript).Trim();
            if (!string.IsNullOrEmpty(text))
            {
                await submit(text);
                FileLog.Write($"[BackgroundDictationSend] submitted {text.Length} chars to session {target.Id}");
            }
            else
            {
                FileLog.Write($"[BackgroundDictationSend] empty transcript for session {target.Id} - nothing submitted");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BackgroundDictationSend] FAILED for session {target.Id}: {ex.Message}");
        }
        finally
        {
            target.IsTranscribing = false;
            try { await recorder.DisposeAsync(); }
            catch (Exception ex) { FileLog.Write($"[BackgroundDictationSend] recorder dispose error: {ex.Message}"); }
        }
    }
}
