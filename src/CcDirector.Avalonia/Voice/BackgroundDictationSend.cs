using CcDirector.Core.Dictation;
using CcDirector.Core.Sessions;
using CcDirector.Core.Transcription;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Voice;

/// <summary>
/// The fire-and-forget desktop dictation send. The Speak dialog handed us the still-capturing recorder
/// the instant Send was pressed and closed itself, releasing the screen; this runs the rest OFF the UI
/// thread while the session shows orange "Transcribing...".
///
/// The behaviour is fail-loud, NO queue and NO readiness pre-check: the clip is transcribed once and
/// submitted straight into the target session through the echo-verified terminal submit. That submit is
/// the only arbiter of whether the session took the text - it types the words and confirms the composer
/// echoed them, throwing when the composer truly refuses (a modal, a picker, still starting up). The old
/// ActivityState pre-check was removed deliberately: it lags real terminal silence by 10 seconds, so it
/// rejected dictations into sessions that were sitting idle at their prompt, and agents accept typed
/// input while working anyway. On ANY failure <paramref name="onFailed"/> fires with the composed text
/// (when one exists) so the caller can put the words back in the compose box - never silent, never lost.
/// </summary>
public static class BackgroundDictationSend
{
    /// <param name="recorder">The detached, still-capturing recorder. Owned and disposed here.</param>
    /// <param name="prefix">Already-transcribed text from earlier Pause/Resume segments, joined ahead of
    /// this segment's transcript; may be empty.</param>
    /// <param name="target">The session the message is being dictated into (marked orange meanwhile).</param>
    /// <param name="transcriber">Transcribes the recorded clip.</param>
    /// <param name="submit">Submits the fully-composed turn text into the session (the dictation has
    /// already been inserted at the caret inside the typed text).</param>
    /// <param name="before">Text the user had typed before the caret when Send was pressed.</param>
    /// <param name="after">Text the user had typed after the caret when Send was pressed.</param>
    /// <param name="onFailed">Called on the UI thread when the dictation could not be delivered. The
    /// first argument is the error; the second is the fully-composed turn text when transcription had
    /// already succeeded (so the caller can restore the words), or null when the failure happened
    /// before a transcript existed. Never silent.</param>
    public static async Task RunAsync(
        BatchDictationRecorder recorder,
        string prefix,
        Session target,
        IDictationTranscriber transcriber,
        Func<string, Task> submit,
        string before = "",
        string after = "",
        Action<string, string?>? onFailed = null)
    {
        FileLog.Write($"[BackgroundDictationSend] start: session={target.Id}, state={target.ActivityState}");
        target.IsTranscribing = true;
        try
        {
            // 1. Stop the mic and get the whole clip as a WAV. An interrupted turn with no audio throws
            //    the completeness gate - there is nothing to send, and nothing was said, so stay silent.
            CapturedAudio captured;
            try
            {
                captured = await recorder.StopAndGetWavAsync();
            }
            catch (NoAudioCapturedException ex)
            {
                FileLog.Write($"[BackgroundDictationSend] no audio captured for session {target.Id}: {ex.Message}");
                return;
            }

            // 2. Transcribe once. On failure there is no transcript to restore - report loudly with
            //    only the typed text recoverable, do not queue or retry.
            string transcript;
            try
            {
                transcript = (await transcriber.TranscribeAsync(captured.Wav)).CleanedTranscript;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[BackgroundDictationSend] transcription FAILED for session {target.Id} ({DiagnosticState(target)}): {ex.Message}");
                onFailed?.Invoke(ex.Message, null);
                return;
            }

            // 3. Compose the turn (dictation dropped at the caret inside any typed text) and submit it
            //    straight through the echo-verified terminal submit. From here on the composed text is
            //    carried into every failure report so the words can be put back in the compose box.
            var dictation = DictationText.Join(prefix, transcript);
            var text = DictationText.InsertAt(before + after, before.Length, dictation).Trim();
            try
            {
                await submit(text);
                FileLog.Write($"[BackgroundDictationSend] delivered to session {target.Id}, chars={text.Length}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[BackgroundDictationSend] submit FAILED for session {target.Id} ({DiagnosticState(target)}): {ex.Message}");
                onFailed?.Invoke(ex.Message, text);
            }
        }
        catch (Exception ex)
        {
            // Root of a detached task: never let it fault unobserved.
            FileLog.Write($"[BackgroundDictationSend] unexpected error for session {target.Id} ({DiagnosticState(target)}): {ex.Message}");
            onFailed?.Invoke(ex.Message, null);
        }
        finally
        {
            target.IsTranscribing = false;
            try { await recorder.DisposeAsync(); }
            catch (Exception ex) { FileLog.Write($"[BackgroundDictationSend] recorder dispose error: {ex.Message}"); }
        }
    }

    /// <summary>
    /// One-line diagnostic snapshot for failure logs: the session's activity state and how long its
    /// terminal has been byte-silent. Both incidents that killed the old readiness gate (issue #1308)
    /// took cross-referencing detector log lines by hand to reconstruct; this puts the answer on the
    /// failure line itself.
    /// </summary>
    private static string DiagnosticState(Session target)
    {
        var idle = target.Buffer is { } buffer
            ? $"{(DateTime.UtcNow - buffer.LastWriteAtUtc).TotalSeconds:F1}s"
            : "n/a";
        return $"state={target.ActivityState}, terminalSilentFor={idle}";
    }
}
