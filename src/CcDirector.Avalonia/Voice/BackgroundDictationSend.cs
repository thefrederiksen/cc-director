using CcDirector.Core.Dictation;
using CcDirector.Core.Sessions;
using CcDirector.Core.Transcription;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Voice;

/// <summary>
/// The DURABLE fire-and-forget desktop dictation send (issue #1130), the desktop twin of the mobile
/// PWA's server-owned durable upload (#1006). The Speak dialog handed us the still-capturing recorder
/// the instant Send was pressed and closed itself, releasing the screen; this runs the rest OFF the UI
/// thread.
///
/// The guarantee: the recorded audio is written to the durable
/// <see cref="PendingDictationStore"/> on disk BEFORE any transcription call, and is deleted ONLY once
/// the words are transcribed and delivered into the session. So a failed or slow transcription (the 504
/// upstream_timeout that lost the user's audio), an unreachable transcription server, or an application
/// crash can never lose a recorded utterance - it is retried in the background and re-driven on the next
/// launch. Nothing short of the disk itself being lost can drop it.
///
/// The immediate attempt runs here; if it does not deliver, the clip stays saved and the
/// <see cref="PendingDictationSweeper"/> (a background timer + launch scan the MainWindow owns) keeps
/// retrying it. The session shows orange "Transcribing..." only for this immediate attempt; a held clip
/// is surfaced by the persistent notice instead, so the user is never blocked from carrying on.
/// </summary>
public static class BackgroundDictationSend
{
    /// <param name="recorder">The detached, still-capturing recorder. Owned and disposed here.</param>
    /// <param name="prefix">Already-transcribed text from earlier Pause/Resume segments, joined ahead of
    /// this segment's transcript; may be empty.</param>
    /// <param name="target">The session the message is being dictated into (marked orange meanwhile).</param>
    /// <param name="store">The durable pending-dictation store the audio is saved to before transcription.</param>
    /// <param name="sweeper">The delivery driver; its in-flight guard prevents this immediate attempt and
    /// a concurrent background sweep from double-delivering the same clip.</param>
    /// <param name="transcriber">Used only for the rare best-effort send when the durable save itself
    /// fails (disk unavailable) - the one path where a failure really can lose the clip, so it is loud.</param>
    /// <param name="before">Text the user had typed before the caret when Send was pressed (usually empty
    /// for Send); persisted so a background retry reproduces the composed turn faithfully.</param>
    /// <param name="after">Text the user had typed after the caret when Send was pressed.</param>
    /// <param name="submit">Submits the fully-composed turn text into the session (the delivery layer has
    /// already inserted the dictation at the caret inside the typed text).</param>
    /// <param name="onResult">Called on the UI thread with the immediate delivery result, so the caller
    /// can refresh the held notice and surface credits/configuration prompts. Null for a fully-lost clip
    /// (durable save failed AND the best-effort send failed) - that is reported via <paramref name="onLost"/>.</param>
    /// <param name="onLost">Called on the UI thread only in the single lossy case: the audio could not be
    /// saved to disk AND the immediate transcription failed. Never silent.</param>
    public static async Task RunAsync(
        BatchDictationRecorder recorder,
        string prefix,
        Session target,
        PendingDictationStore store,
        PendingDictationSweeper sweeper,
        IDictationTranscriber transcriber,
        Func<string, Task> submit,
        string before = "",
        string after = "",
        Action<DictationDeliveryResult>? onResult = null,
        Action<string>? onLost = null)
    {
        FileLog.Write($"[BackgroundDictationSend] start: session={target.Id}");
        target.IsTranscribing = true;
        try
        {
            // 1. Stop the mic and get the whole clip as a WAV. An interrupted turn with no audio throws
            //    the completeness gate - there is nothing to save or send, and nothing was lost.
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

            // 2. Persist DURABLY before any network call. This is the whole guarantee.
            PendingDictation pending;
            try
            {
                pending = store.Save(target.Id.ToString(), prefix, captured.Wav, before, after);
            }
            catch (Exception saveEx)
            {
                // The one path that can still lose audio: we could not write to disk at all. Do a
                // best-effort in-memory send and, if that also fails, tell the user loudly - silence
                // would be worse (mobile's "not persisted" branch).
                FileLog.Write($"[BackgroundDictationSend] DURABLE SAVE FAILED for session {target.Id}: {saveEx.Message}");
                await BestEffortEphemeralSendAsync(prefix, before, after, captured.Wav, transcriber, submit, onLost);
                return;
            }

            // 3. Deliver the saved clip once now. If it does not deliver, it stays saved and the sweeper
            //    retries it automatically - so this attempt failing is a HELD clip, never a lost one.
            var result = await sweeper.TryDeliverAsync(pending, submit);
            if (result is not null)
            {
                FileLog.Write($"[BackgroundDictationSend] immediate attempt for session {target.Id}: {result.Outcome}");
                onResult?.Invoke(result);
            }
        }
        catch (Exception ex)
        {
            // Root of a detached task: never let it fault unobserved. Even here the audio is already on
            // disk (saved at step 2 before anything that could throw), so the sweeper will still recover it.
            FileLog.Write($"[BackgroundDictationSend] unexpected error for session {target.Id}: {ex.Message}");
        }
        finally
        {
            target.IsTranscribing = false;
            try { await recorder.DisposeAsync(); }
            catch (Exception ex) { FileLog.Write($"[BackgroundDictationSend] recorder dispose error: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Last resort when the durable save itself failed: transcribe the in-memory clip once and submit it.
    /// On failure the words really are lost, so it is reported loudly via <paramref name="onLost"/> -
    /// never silent. This is the ONLY lossy path, and it needs the local disk to be unwritable to reach.
    /// </summary>
    private static async Task BestEffortEphemeralSendAsync(
        string prefix, string before, string after, byte[] wav,
        IDictationTranscriber transcriber, Func<string, Task> submit, Action<string>? onLost)
    {
        try
        {
            var transcript = await transcriber.TranscribeAsync(wav);
            var dictation = DictationText.Join(prefix, transcript.CleanedTranscript);
            var text = DictationText.InsertAt(before + after, before.Length, dictation).Trim();
            await submit(text);
            FileLog.Write("[BackgroundDictationSend] best-effort ephemeral send delivered (no durable copy)");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[BackgroundDictationSend] best-effort ephemeral send FAILED (words lost): {ex.Message}");
            onLost?.Invoke(ex.Message);
        }
    }
}
