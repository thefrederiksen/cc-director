// Durable local store for recorded dictation audio (issue #1006).
//
// The instant Send is pressed, the raw recorded audio is written here (IndexedDB, which holds Blobs
// on disk) BEFORE any network work. So a page refresh, a tab crash, a phone reboot, or a dropped
// connection can no longer lose a just-recorded utterance: the audio survives, and the app re-drives
// the upload+submit on the next load. A record is deleted only when the server confirms it owns the
// turn (submitted or deliberately dropped as stale); records older than the TTL are pruned unsent.
//
// IndexedDB (not localStorage) because the audio is binary and can be minutes long; localStorage is
// string-only and tiny. Absence of IndexedDB (rare, e.g. some private modes) is capability-detected -
// the caller falls back to a non-durable send for that one utterance, never a crash.

export interface PendingDictation {
  /** Client-generated GUID; doubles as the server upload id and Idempotency-Key. */
  id: string;
  sessionId: string;
  /** The raw recorded audio exactly as the microphone produced it (WebM/Opus etc.). */
  blob: Blob;
  recordedMs: number;
  /** Typed text before the caret (Terminal Speak compose); empty for the voice case. */
  before: string;
  /** Typed text after the caret; empty for the voice case. */
  after: string;
  /** Earlier paused dictation segments already turned to text, joined ahead of this clip. */
  prefix: string;
  /** The session's TotalBufferBytes when the clip was recorded (server moved-on guard). */
  baselineBufferBytes: number;
  /** Epoch milliseconds the clip was recorded, for the TTL. */
  createdAt: number;
}

const DB_NAME = "dt-dictation";
const STORE = "pending";
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

/** True where durable storage is available (a microphone-capable PWA normally is). */
export function pendingStoreAvailable(): boolean {
  return hasIndexedDb();
}

/** Persist a recorded dictation the instant Send is pressed. Rejects only if IndexedDB is unusable. */
export async function savePending(rec: PendingDictation): Promise<void> {
  if (!hasIndexedDb()) throw new Error("durable dictation store unavailable");
  await tx("readwrite", (s) => s.put(rec));
}

/** Every pending dictation still on disk (oldest first). Empty when the store is unavailable. */
export async function listPending(): Promise<PendingDictation[]> {
  if (!hasIndexedDb()) return [];
  const all = await tx<PendingDictation[]>("readonly", (s) => s.getAll() as IDBRequest<PendingDictation[]>);
  return all.sort((a, b) => a.createdAt - b.createdAt);
}

/** One pending record by id, or null when it is gone (already sent, pruned, or no durable store).
 *  Used by the Retry action on a failed dictation to re-drive that exact clip. */
export async function getPending(id: string): Promise<PendingDictation | null> {
  if (!hasIndexedDb()) return null;
  const rec = await tx<PendingDictation | undefined>(
    "readonly",
    (s) => s.get(id) as IDBRequest<PendingDictation | undefined>,
  );
  return rec ?? null;
}

/** Remove a record once the server has confirmed the turn (submitted or dropped as stale). */
export async function deletePending(id: string): Promise<void> {
  if (!hasIndexedDb()) return;
  await tx("readwrite", (s) => s.delete(id));
}

/** Delete records older than maxAgeMs (unsent, now stale) and return the survivors to resume. */
export async function prunePending(maxAgeMs: number): Promise<PendingDictation[]> {
  const all = await listPending();
  const cutoff = Date.now() - maxAgeMs;
  const survivors: PendingDictation[] = [];
  for (const rec of all) {
    if (rec.createdAt < cutoff) await deletePending(rec.id);
    else survivors.push(rec);
  }
  return survivors;
}
