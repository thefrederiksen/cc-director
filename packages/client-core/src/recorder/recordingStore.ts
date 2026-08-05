// Durable local store for long-form voice recordings (issue #958 - the mobile PWA Voice Recorder).
//
// This is the recorder's twin of the dictation pendingStore (issue #1006), carrying over the retired
// Android recorder's manifest concepts (phone/CcRecorder/Recording/LocalManifest.cs): the same
// never-lose-audio discipline, applied to recordings that can be half an hour long. The recorder
// captures into rolling one-minute segments, and EVERY finalized segment is written here (IndexedDB,
// which holds Blobs on disk) the moment it exists - during recording, long before any network work.
// So an app close, a tab crash, a phone reboot, or a dropped connection loses at most the
// currently-open segment; everything already finalized survives and is queued for automatic upload
// on the next visit.
//
// Two deliberately separate kinds of state, exactly as on the Android recorder:
//
//   - UPLOAD state (`state` + per-chunk `uploaded` flags): purely about getting the audio bytes onto
//     the server. "uploaded" means every segment is confirmed on the server; it says nothing about
//     transcription.
//   - COMPLETED (`completed`): the server ACKNOWLEDGED the complete call (HTTP 202). The notes ride
//     ONLY on the complete call, so a recording is NOT done at state "uploaded" - it stays in the
//     retry queue until the complete call is acknowledged. A kill between uploading the audio and
//     completing can never strand the notes. Transcription state is the server's concern and is read
//     live from the server, never stored here - a transcription failure can never regress upload state.
//
// The on-device copy is the single source of truth until `completed`: a recording is deleted here ONLY
// once the server has acknowledged it holds everything (the completeness gate verified every segment
// by index, byte count, and SHA-256), or when the user explicitly discards it. There is deliberately
// no time-based prune: an unsent recording is kept until it is delivered or discarded (issue #1182).
//
// IndexedDB (not localStorage) because the audio is binary and can be half an hour long. Absence of
// IndexedDB (rare - some private modes) is capability-detected, and the recorder screen refuses to
// record without it instead of silently capturing into memory that a refresh would lose.

/** The upload lifecycle of a locally stored recording. Uploading is AUTOMATIC: stopping a recording
 *  queues it immediately (the Android recorder's "uploaded automatically" bar, issue
 *  devthrottle_internal#966) - there is no Send step on the happy path. */
export type LocalRecordingState =
  /** Actively capturing. A recording found in this state at load was interrupted (the app closed
   *  while recording); recovery flips it to "queued" with `recovered` set, so the audio is never
   *  stranded just because the app died (the Android RecordingUploadGate recovery rule). */
  | "recording"
  /** LEGACY - only a pre-auto-send build wrote this state (stopped, waiting for a Send press).
   *  Treated exactly like "queued": the next upload pass picks it up automatically. */
  | "ready"
  /** Finalized; waiting for an upload pass to pick it up. Auto-retried. */
  | "queued"
  /** An upload pass is actively pushing segments right now. */
  | "uploading"
  /** The last upload pass failed (network drop, server error). Saved and auto-retried - never lost. */
  | "retry"
  /** Every segment is confirmed on the server. Terminal ONLY together with `completed` - until the
   *  complete call is acknowledged the recording still owes the server its manifest and notes. */
  | "uploaded";

/** A note typed during recording, stamped with its millisecond offset from recording start. */
export interface LocalNote {
  tMs: number;
  text: string;
}

/** The per-recording header row. Mirrors the Android LocalManifest / server RecordingManifest shape. */
export interface LocalRecording {
  /** Client-generated GUID; also the server-side recording id (register is idempotent on it). */
  recordingId: string;
  title: string;
  /** The phone's stable install id (deviceKey.getInstallId()), so the server can say which device recorded it. */
  deviceId: string;
  /** ISO-8601 instant recording started. */
  startedAt: string;
  /** ISO-8601 instant recording stopped; null while still recording. */
  endedAt: string | null;
  /** Codec label the server maps to a file extension ("webm-opus", "aac-m4a", "ogg-opus"). */
  codec: string;
  sampleRateHz: number;
  channels: number;
  state: LocalRecordingState;
  /** True once the server ACKed the complete call (202) - the real terminal condition. */
  completed: boolean;
  /** Finalized segment count, kept on the header row as segments land so the library row can render
   *  without loading the (large) chunk rows. */
  segments: number;
  /** Total captured milliseconds across finalized segments (excludes paused time). */
  durationMs: number;
  /** Notes typed while recording, persisted as they are added; delivered on the complete call. */
  notes: LocalNote[];
  /** Epoch milliseconds the recording was created (list ordering). */
  createdAt: number;
  /** Epoch milliseconds of the last header write while capturing - the live capture's HEARTBEAT.
   *  Recovery treats a recently-touched "recording" row as live (possibly in another tab of this
   *  origin) rather than as an orphan to seize. Optional: rows from older builds lack it and are
   *  recovered as before. */
  updatedAt?: number;
  /** Set when this recording was recovered after an interrupted capture (the app closed while
   *  recording), so the library row can say so honestly. */
  recovered?: boolean;
  /** Set when capture was stopped by the SYSTEM rather than the user (the microphone was suspended
   *  by a screen lock, another app, or the browser pausing the page). Holds the plain-English
   *  reason. The library row says so - a cut-off recording must never look complete
   *  (recorder-unlimited-capture mission). */
  interrupted?: string;
  /** The last upload failure, in plain English, so the saved-and-retryable row can say why. */
  lastError?: string;
  /** Determinate progress for the upload pass in flight, persisted on the record itself so the
   *  library row reads the same no matter what is driving the pass (Android principle #8).
   *  Phase "sending" while segments are being pushed; cleared on a terminal state. */
  uploadPhase?: "sending" | null;
  uploadCurrent?: number;
  uploadTotal?: number;
}

/** One finalized audio segment, stored the moment the recorder rotates past it. */
export interface LocalChunk {
  recordingId: string;
  index: number;
  /** A complete, independently decodable audio file (each segment has its own container header). */
  blob: Blob;
  /** Millisecond offset of this segment from the start of the recording. */
  startMs: number;
  durationMs: number;
  bytes: number;
  /** Lowercase hex SHA-256 of the segment bytes - the server's completeness gate verifies it. */
  sha256: string;
  /** True once the server confirmed this segment. Persisted so a retry after a connection drop
   *  resumes at the first unsent segment instead of re-sending bytes the server already has. */
  uploaded: boolean;
}

const DB_NAME = "dt-recorder";
const RECORDINGS = "recordings";
const CHUNKS = "chunks";
const DB_VERSION = 1;

function hasIndexedDb(): boolean {
  try {
    return typeof indexedDB !== "undefined";
  } catch {
    return false;
  }
}

/** True where durable storage is available. The recorder screen refuses to record without it. */
export function recordingStoreAvailable(): boolean {
  return hasIndexedDb();
}

function openDb(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    const req = indexedDB.open(DB_NAME, DB_VERSION);
    req.onupgradeneeded = () => {
      const db = req.result;
      if (!db.objectStoreNames.contains(RECORDINGS)) db.createObjectStore(RECORDINGS, { keyPath: "recordingId" });
      if (!db.objectStoreNames.contains(CHUNKS)) {
        const chunks = db.createObjectStore(CHUNKS, { keyPath: ["recordingId", "index"] });
        chunks.createIndex("byRecording", "recordingId", { unique: false });
      }
    };
    req.onsuccess = () => resolve(req.result);
    req.onerror = () => reject(req.error ?? new Error("indexedDB open failed"));
  });
}

function tx<T>(store: string, mode: IDBTransactionMode, run: (s: IDBObjectStore) => IDBRequest<T>): Promise<T> {
  return openDb().then(
    (db) =>
      new Promise<T>((resolve, reject) => {
        const t = db.transaction(store, mode);
        const req = run(t.objectStore(store));
        req.onsuccess = () => resolve(req.result);
        req.onerror = () => reject(req.error ?? new Error("indexedDB request failed"));
        t.oncomplete = () => db.close();
      }),
  );
}

/** Create or update a recording header row. */
export async function saveRecording(rec: LocalRecording): Promise<void> {
  if (!hasIndexedDb()) throw new Error("durable recording store unavailable");
  await tx(RECORDINGS, "readwrite", (s) => s.put(rec));
}

/** One recording by id, or null when it is gone (delivered, discarded, or no durable store). */
export async function getRecording(recordingId: string): Promise<LocalRecording | null> {
  if (!hasIndexedDb()) return null;
  const rec = await tx<LocalRecording | undefined>(
    RECORDINGS,
    "readonly",
    (s) => s.get(recordingId) as IDBRequest<LocalRecording | undefined>,
  );
  return rec ?? null;
}

/** Every locally stored recording, newest first. Empty when the store is unavailable. */
export async function listRecordings(): Promise<LocalRecording[]> {
  if (!hasIndexedDb()) return [];
  const all = await tx<LocalRecording[]>(RECORDINGS, "readonly", (s) => s.getAll() as IDBRequest<LocalRecording[]>);
  return all.sort((a, b) => b.createdAt - a.createdAt);
}

/** Persist one finalized segment - called the moment the recorder rotates past it, never later. */
export async function saveChunk(chunk: LocalChunk): Promise<void> {
  if (!hasIndexedDb()) throw new Error("durable recording store unavailable");
  await tx(CHUNKS, "readwrite", (s) => s.put(chunk));
}

/** Every stored segment of one recording, in index order. */
export async function listChunks(recordingId: string): Promise<LocalChunk[]> {
  if (!hasIndexedDb()) return [];
  const all = await tx<LocalChunk[]>(CHUNKS, "readonly", (s) => {
    const idx = s.index("byRecording");
    return idx.getAll(recordingId) as IDBRequest<LocalChunk[]>;
  });
  return all.sort((a, b) => a.index - b.index);
}

/** Set one segment's server-confirmed flag (the upload resume point). No-op if the chunk is gone. */
export async function markChunkUploaded(recordingId: string, index: number, uploaded: boolean): Promise<void> {
  if (!hasIndexedDb()) return;
  const chunk = await tx<LocalChunk | undefined>(
    CHUNKS,
    "readonly",
    (s) => s.get([recordingId, index]) as IDBRequest<LocalChunk | undefined>,
  );
  if (chunk === undefined) return;
  chunk.uploaded = uploaded;
  await tx(CHUNKS, "readwrite", (s) => s.put(chunk));
}

/** Remove one recording and all its segments - only after the server acknowledged the complete call
 *  (202: it holds every verified byte plus the manifest and notes), or on an explicit user discard. */
export async function deleteRecording(recordingId: string): Promise<void> {
  if (!hasIndexedDb()) return;
  const chunks = await listChunks(recordingId);
  for (const c of chunks) {
    await tx(CHUNKS, "readwrite", (s) => s.delete([recordingId, c.index]));
  }
  await tx(RECORDINGS, "readwrite", (s) => s.delete(recordingId));
}

/**
 * Recover interrupted captures at app load (the Android NeedsRecovery rule): any recording still
 * marked "recording" was cut off by an app close / crash. A recording WITH finalized segments on disk
 * is finalized into the normal automatic upload path - flipped to "queued" with `recovered` set, so
 * the audio is never stranded just because the app died - and the row says it was recovered. An
 * empty shell (no segments captured) is deleted - the server's completeness gate refuses a zero-segment
 * recording, so there is nothing it could ever become. Returns the recovered recordings.
 *
 * The capture that is LIVE right now must be excluded: the recording session survives navigation
 * (it lives above the router), so a "recording" row is no longer proof of a dead capture - it may be
 * the one currently running. Two guards, because IndexedDB is origin-wide while the session
 * singleton is per tab:
 *   - excludeRecordingId skips THIS tab's live capture;
 *   - the updatedAt heartbeat skips a capture live in ANOTHER tab - its segment rotation touches
 *     the header every minute, so a "recording" row with a fresh heartbeat is running somewhere,
 *     and seizing it would start uploading a recording that is still being written.
 * A genuinely dead capture stops heartbeating and is recovered once the heartbeat is stale.
 */
const RECOVERY_HEARTBEAT_STALE_MS = 3 * 60_000;

export async function recoverInterrupted(excludeRecordingId?: string): Promise<LocalRecording[]> {
  if (!hasIndexedDb()) return [];
  const all = await listRecordings();
  const recovered: LocalRecording[] = [];
  for (const rec of all) {
    if (rec.state !== "recording") continue;
    if (rec.recordingId === excludeRecordingId) continue;
    if (rec.updatedAt !== undefined && Date.now() - rec.updatedAt < RECOVERY_HEARTBEAT_STALE_MS) continue;
    const chunks = await listChunks(rec.recordingId);
    if (chunks.length === 0) {
      await deleteRecording(rec.recordingId);
      continue;
    }
    rec.state = "queued";
    rec.recovered = true;
    if (rec.endedAt === null) rec.endedAt = new Date().toISOString();
    await saveRecording(rec);
    recovered.push(rec);
  }
  return recovered;
}
