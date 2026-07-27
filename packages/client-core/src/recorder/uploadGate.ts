// The pure decision logic for the recorder's upload pass (issue #958) - the PWA port of the Android
// recorder's RecordingUploadGate (phone/CcRecorder/Recording/RecordingUploadGate.cs). Given a
// recording's UPLOAD state and whether its complete/notes call has been acknowledged by the server,
// decide whether it still needs work, whether the audio-upload phase must run, and whether it is
// fully delivered.
//
// These rules are what guarantee "the audio AND the notes always upload, no matter what happens to
// transcription". The notes ride ONLY on the complete call, so a recording is NOT done at state
// "uploaded" - it is done only once the complete call is acknowledged (`completed`). Kept
// dependency-free (no browser APIs) so it is unit-tested and cannot silently regress (e.g. back to
// treating "uploaded" as terminal).

import type { LocalRecordingState } from "./recordingStore";

/**
 * The recording still has upload work to do, so a retry pass must process it. Either the audio bytes
 * are not all on the server yet (ready/queued/retry/uploading), or the audio IS uploaded but the
 * complete call - the only thing that delivers the NOTES and triggers server-side transcription - has
 * not yet been acknowledged. Uploading is automatic on stop (the Android recorder's "uploaded
 * automatically" bar, issue devthrottle_internal#966), so "ready" - which only a pre-auto-send build
 * could have written - is picked up too: a stored recording never sits waiting for a Send press.
 */
export function needsUpload(state: LocalRecordingState, completed: boolean): boolean {
  if (state === "ready" || state === "queued" || state === "retry" || state === "uploading") return true;
  if (state === "uploaded" && !completed) return true;
  return false;
}

/**
 * The audio-upload phase must run unless the audio is already fully on the server. When the audio is
 * up but the notes are not yet delivered, this is false so the pass resumes straight to the
 * complete/notes call without re-sending any bytes.
 */
export function shouldUploadAudio(state: LocalRecordingState): boolean {
  return state !== "uploaded";
}

/**
 * Terminal: the recording is fully delivered - the audio is uploaded AND the complete/notes call was
 * acknowledged. Only then may the local copy be deleted.
 */
export function isFullyDelivered(state: LocalRecordingState, completed: boolean): boolean {
  return state === "uploaded" && completed;
}

/**
 * The server's audio completeness gate (issue #586) refused the complete call because some segments
 * are missing or hash-mismatched on the server, naming their indices. This is the pure decision for
 * the gate-driven resume (issue #591): given the indices the gate reported and the segment indices
 * this recording actually has locally, return exactly the locally-present indices that must be
 * re-armed (their `uploaded` flag cleared) so the next pass re-sends them - and nothing else. A
 * segment the gate names but the phone never had is not invented here. De-duplicated and sorted.
 */
export function requeueIndicesForResend(
  missingOrBadIndices: readonly number[] | null | undefined,
  localIndices: readonly number[],
): number[] {
  if (missingOrBadIndices == null) return [];
  const local = new Set(localIndices);
  return [...new Set(missingOrBadIndices.filter((i) => local.has(i)))].sort((a, b) => a - b);
}
