import { sendPrompt, setTranscribing, transcribeUtterance } from "../api/client";
import { logCaptureHealth } from "./captureHealth";
import { joinText } from "./transcript";
import { blobToWav16kMono } from "./wav";

// The fire-and-forget Send pipeline for the mobile Speak dialog. The instant the user hits Send the
// dialog captures the recorded audio buffer, hands it here, and closes - the screen is released
// immediately, because everything left to do (transcode, upload, transcribe, submit) needs the
// buffer, not the user, and the result cannot be viewed on that screen anyway (Send submits straight
// into the session). This function then does that whole chain in the background while the roster
// shows the session orange ("Transcribing...") so nobody else starts using it.
//
// It deliberately lives OUTSIDE the DictationDialog component: the dialog unmounts (and disposes its
// recorder) the moment Send is pressed, so the work must not be tied to the dialog's lifecycle. The
// captured Blob is independent of the recorder, so disposing the recorder does not affect it.

/** The audio buffer + context the dialog hands up when Send is pressed while still recording. */
export interface CapturedUtterance {
  /** The raw recorded audio exactly as the microphone produced it (WebM/Opus etc.); transcoded to
   *  WAV here, off the dialog's critical path, so the screen is released before any transcode. */
  blob: Blob;
  /** Wall-clock milliseconds the segment was capturing (capture-health, issue #863). */
  recordedMs: number;
  /** Earlier Pause/Resume dictation segments, joined ahead of this final segment's transcript to form
   *  the full dictation. Empty in the common "just talk and Send" case (no pause). */
  prefixText: string;
}

/** Callbacks so the host can surface a failure. Success is silent - the submitted turn IS the proof
 *  and the roster's orange flag clearing is the visible completion signal. */
export interface BackgroundSendHooks {
  onError?: (message: string) => void;
  /** Called when the transcribe/submit chain throws, so the host can restore anything (e.g. the typed
   *  compose text) it cleared at dialog-close time for a send that never went. */
  onFailed?: () => void;
  /** Place the dictated words into the final message. The host uses this to insert them at the caret
   *  inside any typed compose text (like the Insert button), then this result is submitted. Defaults
   *  to submitting the dictation alone. Called even for an empty dictation, so a Send pressed with
   *  typed text present still submits that text; a fully-empty message submits nothing. */
  compose?: (dictation: string) => string;
}

// Mark the session transcribing (roster -> orange), transcode + upload + transcribe the captured
// audio, submit the joined transcript into the session, then clear the transcribing mark. The mark
// is cleared in a finally so a transcode/transcribe/submit failure still releases the orange state.
export async function backgroundTranscribeAndSend(
  sessionId: string,
  captured: CapturedUtterance,
  hooks: BackgroundSendHooks = {},
): Promise<void> {
  // Flip the roster to orange first, so the busy signal shows from the moment the screen is released.
  // Best-effort: a marker failure must not abort the actual transcription+send the user asked for.
  try {
    await setTranscribing(sessionId, true);
  } catch {
    /* the marker is a visual nicety; press on with the real work */
  }

  try {
    const transcoded = await blobToWav16kMono(captured.blob);
    const health = {
      recordedMs: captured.recordedMs,
      decodedSeconds: transcoded.decodedSeconds,
      sourceBytes: transcoded.sourceBytes,
    };
    logCaptureHealth("mobile", health);
    const segment = await transcribeUtterance(transcoded.wav, health);
    const dictation = joinText(captured.prefixText, segment).trim();
    // Let the host place the dictation (it inserts at the caret inside any typed text); default to the
    // dictation alone. The typed text survives an empty/silent clip because compose still returns it,
    // and a fully-empty message submits nothing so a mis-tapped Send does not fire a blank turn.
    const message = (hooks.compose ? hooks.compose(dictation) : dictation).trim();
    if (message.length > 0) {
      await sendPrompt(sessionId, message, true);
    }
  } catch (err) {
    hooks.onError?.(err instanceof Error ? err.message : "Transcription failed");
    hooks.onFailed?.();
  } finally {
    // Authoritative clear - releases the orange roster state whether the transcript submitted or the
    // attempt failed. Best-effort; the Gateway also expires an abandoned mark as a backstop.
    try {
      await setTranscribing(sessionId, false);
    } catch {
      /* the Gateway's stale-mark backstop clears it if this never lands */
    }
  }
}
