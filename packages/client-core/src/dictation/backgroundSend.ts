import { uploadDictationToSession } from "../api/client";
import { deletePending, prunePending, savePending, type PendingDictation } from "./pendingStore";

// The durable Send pipeline for the mobile Speak dialog (issue #1006). The instant the user hits Send
// the dialog hands the recorded audio here and closes; we persist the raw audio locally (IndexedDB)
// BEFORE any network work, then stream it to the Gateway in resumable chunks. The Gateway assembles,
// transcribes, and INJECTS the turn into the session itself, so once the audio is uploaded a dead tab
// or a dropped connection can no longer lose it. If anything is interrupted, the durable record
// survives and resumePendingDictations() (called on app load) re-drives the upload+submit.
//
// It deliberately lives OUTSIDE the DictationDialog component: the dialog unmounts (and disposes its
// recorder) the moment Send is pressed, so the work must not be tied to the dialog's lifecycle.

/** How long a recorded-but-unsent clip is kept before it is pruned unsent (issue #1006): an hour.
 *  This is a temporary out-of-bandwidth buffer, not a mailbox - a clip we never managed to send
 *  within the hour is dropped rather than injected into a session that has long since moved on. */
const PENDING_TTL_MS = 60 * 60 * 1000;

/** The audio buffer + context the dialog hands up when Send is pressed. */
export interface CapturedUtterance {
  /** The raw recorded audio exactly as the microphone produced it (WebM/Opus etc.). */
  blob: Blob;
  /** Wall-clock milliseconds the segment was capturing (capture-health, issue #863). */
  recordedMs: number;
  /** Earlier Pause/Resume dictation segments, already turned to text, joined ahead of this final
   *  segment. Empty in the common "just talk and Send" case. */
  prefixText: string;
}

/** Callbacks so the host can surface a failure. Success is silent - the submitted turn IS the proof. */
export interface BackgroundSendHooks {
  onError?: (message: string) => void;
  /** Called when the send does not complete (kept durably for resume), so the host can restore any
   *  typed compose text it cleared at dialog-close time. */
  onFailed?: () => void;
  /** Typed text the caret split the dictation around (Terminal Speak's Insert-then-Enter). The voice
   *  case omits this and the transcript is submitted alone. */
  composeParts?: { before: string; after: string };
  /** The session's TotalBufferBytes at record time, for the Gateway's "session moved on" guard when a
   *  clip is resumed later. Omit when unknown (the guard is then skipped for safety). */
  baselineBufferBytes?: number;
}

// Persist the recorded audio durably, then drive the server-owned upload+transcribe+inject. On a
// terminal outcome (submitted, or dropped as stale) the durable record is deleted; otherwise it is
// kept so the next app load resumes it.
export async function backgroundTranscribeAndSend(
  sessionId: string,
  captured: CapturedUtterance,
  hooks: BackgroundSendHooks = {},
): Promise<void> {
  const rec: PendingDictation = {
    id: crypto.randomUUID(),
    sessionId,
    blob: captured.blob,
    recordedMs: captured.recordedMs,
    before: hooks.composeParts?.before ?? "",
    after: hooks.composeParts?.after ?? "",
    prefix: captured.prefixText ?? "",
    baselineBufferBytes: hooks.baselineBufferBytes ?? 0,
    createdAt: Date.now(),
  };

  // Save the audio the instant Send is pressed - even if this tab dies right now, it is not lost.
  let persisted = false;
  try {
    await savePending(rec);
    persisted = true;
  } catch {
    // No durable store in this context (rare): press on with a best-effort, non-durable send.
  }

  try {
    const outcome = await uploadDictationToSession({
      sessionId,
      uploadId: rec.id,
      audio: rec.blob,
      before: rec.before,
      after: rec.after,
      prefix: rec.prefix,
      baselineBufferBytes: rec.baselineBufferBytes,
      resumed: false,
    });
    if (outcome.terminal) {
      if (persisted) await deletePending(rec.id);
    } else {
      // The server did not confirm the turn; keep the durable record so resume-on-load retries it, and
      // surface the soft error. (If there is no durable copy, this utterance is genuinely lost - the
      // onError is then the signal, exactly as before.)
      hooks.onError?.(outcome.error ?? "Dictation upload failed");
      hooks.onFailed?.();
    }
  } catch (err) {
    hooks.onError?.(err instanceof Error ? err.message : "Transcription failed");
    hooks.onFailed?.();
  }
}

// Re-drive every recorded-but-unsent dictation on app load: prune anything past the TTL, then resume
// the upload+submit for the rest. Idempotent by upload id, so a clip that actually landed before the
// tab died is de-duplicated by the Gateway rather than double-submitted. Best-effort: a clip that
// still cannot send is kept for the next load (until it ages out).
export async function resumePendingDictations(): Promise<void> {
  let survivors: PendingDictation[];
  try {
    survivors = await prunePending(PENDING_TTL_MS);
  } catch {
    return; // no durable store; nothing to resume
  }
  for (const rec of survivors) {
    try {
      const outcome = await uploadDictationToSession({
        sessionId: rec.sessionId,
        uploadId: rec.id,
        audio: rec.blob,
        before: rec.before,
        after: rec.after,
        prefix: rec.prefix,
        baselineBufferBytes: rec.baselineBufferBytes,
        resumed: true,
      });
      if (outcome.terminal) await deletePending(rec.id);
      // Non-terminal: keep it for the next load.
    } catch {
      // Credits/network still down: keep the record; the next launch retries until the TTL prunes it.
    }
  }
}
