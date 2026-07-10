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
/// The behaviour is fail-loud, NO queue: the clip is transcribed once and, if the target session is idle
/// at its prompt, submitted. If transcription fails, or the session is not accepting input, the words are
/// NOT held or retried - <paramref name="onFailed"/> fires so the caller can restore the typed text and
/// tell the user loudly with a modal. This deliberately replaced the old silent hold-and-retry store,
/// whose reassurance banner everyone missed; a dictation now either goes in at once or blows up at once.
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
    /// <param name="isSessionReady">True when the target session is idle at its prompt and can accept the
    /// dictation. Typing into a busy, streaming composer is what piled up duplicate copies of the sentence
    /// (issue #1135), so a not-ready session fails loudly instead of being typed into or held.</param>
    /// <param name="before">Text the user had typed before the caret when Send was pressed.</param>
    /// <param name="after">Text the user had typed after the caret when Send was pressed.</param>
    /// <param name="onFailed">Called on the UI thread when the dictation could not be delivered
    /// (transcription failed, or the session was not accepting input). Never silent.</param>
    public static async Task RunAsync(
        BatchDictationRecorder recorder,
        string prefix,
        Session target,
        IDictationTranscriber transcriber,
        Func<string, Task> submit,
        Func<bool> isSessionReady,
        string before = "",
        string after = "",
        Action<string>? onFailed = null)
    {
        FileLog.Write($"[BackgroundDictationSend] start: session={target.Id}");
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

            // 2. Transcribe once. On failure the words are lost - report loudly, do not queue or retry.
            string transcript;
            try
            {
                transcript = (await transcriber.TranscribeAsync(captured.Wav)).CleanedTranscript;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[BackgroundDictationSend] transcription FAILED for session {target.Id}: {ex.Message}");
                onFailed?.Invoke(ex.Message);
                return;
            }

            // 3. The session must be idle at its prompt to accept the dictation. If it is busy or still
            //    starting up we fail loudly rather than type into a streaming composer (issue #1135) or
            //    silently hold the clip.
            if (!isSessionReady())
            {
                FileLog.Write($"[BackgroundDictationSend] session {target.Id} not accepting input; dictation dropped");
                onFailed?.Invoke("the session was not accepting input (it is busy or still starting up). Wait until it is idle at its prompt, then dictate again.");
                return;
            }

            // 4. Compose the turn: drop the dictation at the caret inside any typed text, then submit.
            var dictation = DictationText.Join(prefix, transcript);
            var text = DictationText.InsertAt(before + after, before.Length, dictation).Trim();
            await submit(text);
            FileLog.Write($"[BackgroundDictationSend] delivered to session {target.Id}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BackgroundDictationSend] unexpected error for session {target.Id}: {ex.Message}");
            onFailed?.Invoke(ex.Message);
        }
        finally
        {
            target.IsTranscribing = false;
            try { await recorder.DisposeAsync(); }
            catch (Exception ex) { FileLog.Write($"[BackgroundDictationSend] recorder dispose error: {ex.Message}"); }
        }
    }
}
