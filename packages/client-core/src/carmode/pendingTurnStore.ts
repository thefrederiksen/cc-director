// Durable local store for Car Mode command audio (Car Mode mission, offline-resilience Phase 4a,
// issue #1427). It mirrors the mobile dictation durable store (dictation/pendingStore.ts): the raw
// recorded COMMAND audio for a turn is written here (IndexedDB, which holds Blobs on disk) BEFORE any
// network work, so a dead zone, a page refresh, a tab crash, or a phone reboot can no longer lose the
// owner's spoken request. The background driver (turnRetry.ts + the useCarMode hook) re-drives the held
// audio through transcribe -> brain -> speak when connectivity returns.
//
// Why AUDIO and not text: transcription runs on the Gateway, so an offline client cannot turn speech
// into text. The durable unit for a whole turn is therefore the raw command audio; the transcript (once
// obtained) is cached on the record so a later re-drive need not transcribe twice.
//
// The on-device copy is the single source of truth. A record is deleted ONLY when the brain call returns
// a definitive success (the turn is owned server-side), or when the owner explicitly discards it.
// Undelivered audio is NEVER aged out automatically - a request the owner could not send yet is kept
// until it is delivered or discarded.
//
// The `brainSent` flag is the safety boundary (Architect decision, 2026-07-13). A fully-offline turn
// fails at transcribe and NEVER reaches the brain, so re-driving it is a FIRST brain call = no
// double-action = safe to auto-retry. The ONLY unsafe case is a turn whose brain call was already SENT
// and whose result is unknown (transcribe briefly succeeded, then the connection dropped): that could
// have acted, so it is HELD for the owner rather than auto-fired (Phase 4b makes even that safe with a
// server idempotency key). `brainSent` records exactly which side of that line a held turn is on.

export interface PendingCarModeTurn {
  /** Client-generated GUID. Doubles as the future Phase 4b server Idempotency-Key for the brain call. */
  id: string;
  /** The raw recorded command audio exactly as the microphone produced it (WebM/Opus etc.). */
  audio: Blob;
  /** The owner's sign-off phrase to strip from the transcript, captured at record time so a later
   *  re-drive strips the phrase the owner actually used, not whatever is configured at re-drive time. */
  endPhrase: string;
  /** Epoch milliseconds the command was captured. Drives the retry cadence (hard for the first hour,
   *  then throttled) and the staleness cap (a turn older than ~30 minutes is not auto-fired). NOT a
   *  prune deadline: undelivered audio is never aged out. */
  createdAt: number;
  /** False until the brain call for this turn has been DISPATCHED. While false the turn provably never
   *  reached the brain, so it is safe to auto-retry. Set true immediately BEFORE the /carmode/turn fetch;
   *  a true value means the turn may have acted and its result is unknown, so it is held for the owner. */
  brainSent: boolean;
  /** The command transcript (sign-off phrase already stripped), cached once transcription succeeded so a
   *  re-drive does not transcribe the same audio twice. Absent until the first successful transcription. */
  transcript?: string;
}

const DB_NAME = "dt-carmode";
const STORE = "pending-turns";
const DB_VERSION = 1;

function hasIndexedDb(): boolean {
  try {
    return typeof indexedDB !== "undefined";
  } catch {
    return false;
  }
}

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, DB_VERSION);
    req.onupgradeneeded = () => {
      const db = req.result;
      if (!db.objectStoreNames.contains(STORE)) db.createObjectStore(STORE, { keyPath: "id" });
    };
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error ?? new Error("indexedDB open failed"));
  });
}

function tx<T>(mode: IDBTransactionMode, run: (store: IDBObjectStore) => IDBRequest<T>): Promise<T> {
  return openDb().then(
    (db) =>
      new Promise<T>((resolve, reject) => {
        const t = db.transaction(STORE, mode);
        const req = run(t.objectStore(STORE));
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => reject(req.error ?? new Error("indexedDB request failed"));
        t.oncomplete = () => db.close();
      }),
  );
}

/** True where durable storage is available (a microphone-capable PWA normally is). Car Mode is
 *  Chromium-first, where IndexedDB is always present; this guards the rare private-mode tab. */
export function pendingTurnStoreAvailable(): boolean {
  return hasIndexedDb();
}

/** Persist (or update) a Car Mode command-audio record. Rejects only if IndexedDB is unusable, which
 *  the caller surfaces loudly rather than silently dropping the audio. */
export async function savePendingTurn(rec: PendingCarModeTurn): Promise<void> {
  if (!hasIndexedDb()) throw new Error("durable Car Mode turn store unavailable");
  await tx("readwrite", (s) => s.put(rec));
}

/** Every held command-audio record still on disk, oldest first (FIFO). Empty when the store is
 *  unavailable. */
export async function listPendingTurns(): Promise<PendingCarModeTurn[]> {
  if (!hasIndexedDb()) return [];
  const all = await tx<PendingCarModeTurn[]>("readonly", (s) => s.getAll() as IDBRequest<PendingCarModeTurn[]>);
  return all.sort((a, b) => a.createdAt - b.createdAt);
}

/** One held record by id, or null when it is gone (already delivered, discarded, or no durable store). */
export async function getPendingTurn(id: string): Promise<PendingCarModeTurn | null> {
  if (!hasIndexedDb()) return null;
  const rec = await tx<PendingCarModeTurn | undefined>(
    "readonly",
    (s) => s.get(id) as IDBRequest<PendingCarModeTurn | undefined>,
  );
  return rec ?? null;
}

/** Remove a record once the brain has owned the turn (definitive success), or the owner discards it.
 *  There is deliberately no time-based prune: undelivered audio is kept until delivered or discarded. */
export async function deletePendingTurn(id: string): Promise<void> {
  if (!hasIndexedDb()) return;
  await tx("readwrite", (s) => s.delete(id));
}
