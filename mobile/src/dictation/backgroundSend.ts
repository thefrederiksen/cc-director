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
  /** Already-transcribed text from earlier Pause/Resume segments to prepend to this final segment.
   *  Empty in the common "just talk and Send" case (no pause). */
  prefixText: string;
}

/** Callbacks so the host can surface a failure. Success is silent - the submitted turn IS the proof
 *  and the roster's orange flag clearing is the visible completion signal. */
export interface BackgroundSendHooks {
  onError?: (message: string) => void;
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
    const text = joinText(captured.prefixText, segment).trim();
    // A silent clip (no speech captured) transcribes to nothing - submit nothing rather than an
    // empty line, so a mis-tapped Send does not fire a blank turn into the session.
    if (text.length > 0) {
      await sendPrompt(sessionId, text, true);
    }
  } catch (err) {
    hooks.onError?.(err instanceof Error ? err.message : "Transcription failed");
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
