import { useCallback, useMemo, useRef } from "react";
import { listSessions } from "../api/client";

// The record-time terminal-byte baseline for the Gateway's "session moved on" guard (issue #1006),
// armed from the Speak flows by issue #2478.
//
// The guard drops a RESUMED dictation when the session's terminal output grew materially after the
// clip was recorded - other turns happened, so injecting the stale words would answer a question that
// is no longer on screen. The Gateway can only judge that growth against the terminal position AT
// RECORD TIME, and the client is the only party that knows it: by the time a resumed clip first
// reaches the Gateway (the whole point of a resume is that earlier attempts never arrived), the
// Gateway's own current reading is already past whatever moved on. Both taught Speak flows (the
// mobile SessionControls and the Cockpit SessionComposer) used to omit the field, so it defaulted to
// zero and the guard never armed; this module is the one shared place they now snapshot it, the same
// roster reading the Voice screen has always sent (useVoiceMode).
//
// The snapshot is handed around AS A PROMISE, never as a peek at whatever has resolved so far: the
// Speak press starts the roster read, and the send pipeline AWAITS it before persisting the durable
// record. A quick Send therefore cannot outrun the read and record "unknown" for a session whose
// position was perfectly knowable - the race the first version of this file had. The wait is bounded:
// listSessions carries the poll timeout, and this module never rejects.
//
// An unknown baseline (undefined) is a DEFINED state of the pipeline's contract (BackgroundSendHooks
// .baselineBufferBytes: unknown means the field is omitted on the wire, and the guard is then skipped
// for safety), not a softened failure: the user's words must never be blocked - or lost - because a
// roster read failed. Unknown is distinct from ZERO, which is a real reading (a session whose
// terminal had produced nothing yet) and is passed through as such.
export async function snapshotBaselineBufferBytes(sessionId: string): Promise<number | undefined> {
  try {
    return await readBaselineOnce(sessionId);
  } catch {
    // One deliberate retry, for the transient roster failure only (a dropped request, a timeout). A
    // roster that ANSWERED but does not know the session gets no retry - asking again cannot change
    // that answer. Two failures in a row mean the baseline is genuinely unknowable right now.
  }
  try {
    return await readBaselineOnce(sessionId);
  } catch {
    return undefined;
  }
}

async function readBaselineOnce(sessionId: string): Promise<number | undefined> {
  const all = await listSessions();
  // totalBufferBytes is a 64-bit integer on the wire, so the schema admits number | string.
  const bytes = Number(all.find((s) => s.sessionId === sessionId)?.totalBufferBytes);
  return Number.isFinite(bytes) ? bytes : undefined;
}

export interface DictationBaseline {
  /** Call when Speak is pressed (recording starts): starts the roster read that snapshots the
   *  session's terminal-byte position, replacing any previous recording's snapshot so a stale
   *  number can never describe a new recording. */
  snapshot: () => void;
  /** The snapshot for the recording in progress, as a promise the send pipeline awaits. Resolves to
   *  the terminal-byte position, or to undefined when it is genuinely unknowable (the roster read
   *  failed even after the retry, no session is selected, or Speak was never pressed). Never rejects. */
  read: () => Promise<number | undefined>;
}

// The per-recording snapshot both Speak flows share. Each Speak press replaces the stored promise,
// so a second recording can never be stamped by the first recording's late answer, and Send always
// awaits exactly the read its own Speak press started.
export function useDictationBaseline(sessionId: string | undefined): DictationBaseline {
  const promiseRef = useRef<Promise<number | undefined> | undefined>(undefined);

  const snapshot = useCallback(() => {
    promiseRef.current = sessionId ? snapshotBaselineBufferBytes(sessionId) : Promise.resolve(undefined);
  }, [sessionId]);

  const read = useCallback(() => promiseRef.current ?? Promise.resolve(undefined), []);

  return useMemo(() => ({ snapshot, read }), [snapshot, read]);
}
