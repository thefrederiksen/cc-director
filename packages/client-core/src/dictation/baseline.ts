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
// An unknown baseline is a DEFINED state of the pipeline's contract (BackgroundSendHooks
// .baselineBufferBytes: omit when unknown, and the guard is then skipped for safety), not a softened
// failure: the user's words must never be blocked - or arrive late - because a roster read failed,
// so a failure here yields "unknown" and the clip still delivers; it just cannot be moved-on-guarded.
export async function snapshotBaselineBufferBytes(sessionId: string): Promise<number | undefined> {
  try {
    const all = await listSessions();
    // totalBufferBytes is a 64-bit integer on the wire, so the schema admits number | string.
    const bytes = Number(all.find((s) => s.sessionId === sessionId)?.totalBufferBytes);
    return Number.isFinite(bytes) ? bytes : undefined;
  } catch {
    return undefined;
  }
}

export interface DictationBaseline {
  /** Call when Speak is pressed (recording starts): starts the roster read that snapshots the
   *  session's terminal-byte position, and forgets any previous snapshot immediately so a stale
   *  number can never describe a new recording. */
  snapshot: () => void;
  /** The snapshotted baseline for the recording in progress, or undefined while it is unknown
   *  (roster read still pending, failed, or no session selected). */
  read: () => number | undefined;
}

// The per-recording snapshot both Speak flows share. Recording takes seconds, so the roster read
// started at Speak press has long resolved by the time Send hands the clip to the background
// pipeline; if Send somehow wins the race, read() returns undefined and the guard is skipped for
// that clip - exactly the pre-snapshot behavior, never anything worse.
export function useDictationBaseline(sessionId: string | undefined): DictationBaseline {
  const valueRef = useRef<number | undefined>(undefined);
  // Distinguishes the CURRENT snapshot's roster read from an earlier one still in flight, so two
  // Speak presses in a row can never let the first press's late answer stamp the second recording.
  const tokenRef = useRef(0);

  const snapshot = useCallback(() => {
    const token = ++tokenRef.current;
    valueRef.current = undefined;
    if (!sessionId) return;
    void snapshotBaselineBufferBytes(sessionId).then((bytes) => {
      if (tokenRef.current === token) valueRef.current = bytes;
    });
  }, [sessionId]);

  const read = useCallback(() => valueRef.current, []);

  return useMemo(() => ({ snapshot, read }), [snapshot, read]);
}
