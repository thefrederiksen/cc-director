import { useMemo, useSyncExternalStore } from "react";

// The single live-status source for in-flight dictations (owner rule after #1139: a dictation Send
// must NEVER be silent). Every dictation surface reads from here - the on-screen status strip on the
// Terminal/Chat/Voice screens AND the session-list badge on the roster - so the same send shows the
// same live state whether the user stays on the session or walks back to the roster.
//
// The store is in-memory and keyed by uploadId. It intentionally survives route changes within the
// single-page PWA (leaving the Terminal for Home keeps the status), which is exactly why the roster
// can carry a dictation that started on the session screen. A full app reload clears it, but the
// durable pendingStore record plus resumePendingDictations re-drive the send and re-publish status on
// the next load, so nothing is silently dropped.
//
// Publishing lives in the send pipeline: uploadDictationToSession publishes the mechanical steps
// (uploading, transcribing) and the terminal outcome (done / failed), and backgroundTranscribeAndSend
// publishes the very first "saving" step before any network work. Consumers only read.

export type DictationPhase = "saving" | "uploading" | "transcribing" | "done" | "failed";

export interface DictationStatus {
  /** The session the dictation is being sent into. */
  sessionId: string;
  /** The client-generated upload id (also the durable record id and the server Idempotency-Key). */
  uploadId: string;
  phase: DictationPhase;
  /** Chunks uploaded so far (uploading phase only). */
  uploaded?: number;
  /** Total chunks for this clip (uploading phase only). */
  total?: number;
  /** Human-readable failure reason (failed phase only). */
  error?: string;
  /** True when the clip is saved durably and will retry on the next app load - "held", not lost.
   *  False for a hard failure (for example out of credits) that a plain retry will not fix. */
  retryable?: boolean;
  /** Epoch milliseconds of the last update (newest-first ordering, and the done auto-clear timer). */
  updatedAt: number;
}

type Listener = () => void;

const _byId = new Map<string, DictationStatus>();
const _listeners = new Set<Listener>();

// A frozen array snapshot recomputed only when the data changes, so useSyncExternalStore's getSnapshot
// returns a stable reference between renders (returning a fresh array every call would loop forever).
let _snapshot: DictationStatus[] = [];

function rebuildAndEmit(): void {
  _snapshot = Array.from(_byId.values());
  for (const l of _listeners) l();
}

function subscribe(listener: Listener): () => void {
  _listeners.add(listener);
  return () => {
    _listeners.delete(listener);
  };
}

/** Publish or replace the live status for one dictation (keyed by uploadId). */
export function publishDictationStatus(status: Omit<DictationStatus, "updatedAt">): void {
  _byId.set(status.uploadId, { ...status, updatedAt: Date.now() });
  rebuildAndEmit();
}

/** Drop one dictation's status - after a success has been acknowledged on screen, or the user
 *  dismisses a failure. Idempotent: clearing an unknown id is a no-op. */
export function clearDictationStatus(uploadId: string): void {
  if (_byId.delete(uploadId)) rebuildAndEmit();
}

/** Every current dictation status (in-flight, just-done, or failed-and-waiting). */
export function allDictationStatuses(): DictationStatus[] {
  return _snapshot;
}

/** React hook: the full live status list, re-rendering the caller whenever any dictation changes. */
export function useDictationStatuses(): DictationStatus[] {
  return useSyncExternalStore(subscribe, () => _snapshot, () => _snapshot);
}

/** React hook: the one status that matters for a session - an active (non-done) send if there is one,
 *  otherwise the most recent. Null when the session has no dictation activity. */
export function useDictationStatusFor(sessionId: string | undefined): DictationStatus | null {
  const all = useDictationStatuses();
  return useMemo(() => {
    if (!sessionId) return null;
    const mine = all.filter((s) => s.sessionId === sessionId).sort((a, b) => b.updatedAt - a.updatedAt);
    if (mine.length === 0) return null;
    return mine.find((s) => s.phase !== "done") ?? mine[0];
  }, [all, sessionId]);
}
