// The app-level recording session (recorder-unlimited-capture mission). Exactly one capture can be
// live per app instance, and it must SURVIVE IN-APP NAVIGATION: the SegmentRecorder used to be
// constructed inside the Recorder PAGE component, so leaving the page - to check a session, use
// voice mode, anything - unmounted the page and stopped the recording. During an all-day conference
// that is fatal. The session lives here, at module scope, above the router: the Recorder page and
// the global recording indicator both subscribe to it, and navigation neither creates nor destroys
// it.
//
// Concurrency discipline (the reviewer's findings, all applied):
//   - Every mutation of the durable header row goes through ONE serialized queue (headerChain), so
//     a title blur racing a stop, or a note racing a rotation, cannot overwrite each other's
//     read-modify-write.
//   - Finalization is keyed to the capture's recordingId and runs at most once per capture: a Stop
//     pressed while a microphone-loss finalize is in flight is a no-op, and a stale continuation
//     from an already-finalized capture can neither emit "recording" nor resurrect deleted rows.
//   - Failure paths land in a truthful state: a storage failure during stop still releases the
//     recorder, still surfaces the error, and never leaves the banner claiming a live capture.
//
// What a PWA honestly cannot promise: capture through a locked screen. The app holds a screen wake
// lock while foregrounded (useScreenWakeLock), which keeps the screen - and therefore capture -
// alive; but if the phone does lock or the browser suspends the page, the microphone track ends,
// the SegmentRecorder salvages and stops, and the recording is finalized here as cut short. The
// reason is written BOTH on the local row (`interrupted`) and as a timestamped NOTE on the
// recording itself - notes ride the complete call to the Gateway and into the transcript, so the
// truncation stays visible even after the local row is delivered and deleted. Truncation is
// surfaced, never hidden.

import { useSyncExternalStore } from "react";
import { SegmentRecorder } from "./segmentRecorder";
import {
  deleteRecording,
  getRecording,
  recordingStoreAvailable,
  saveChunk,
  saveRecording,
  type LocalNote,
  type LocalRecording,
} from "./recordingStore";
import { driveRecordingUpload, sha256Hex } from "./ingestUpload";
import { getInstallId } from "../auth/deviceKey";

export type RecordingPhase = "idle" | "starting" | "recording" | "paused" | "stopping";

export interface RecordingSessionState {
  phase: RecordingPhase;
  /** The live capture's recording id; null when idle. */
  recordingId: string | null;
  /** The title as edited so far (editable until stop; the value at stop wins). */
  title: string;
  /** Notes typed during the live capture, in the order they were added. */
  notes: LocalNote[];
  /** A capture failure or system stop, in plain English. Sticks until dismissed or the next
   *  capture starts, so a loss that happened on another screen is still seen. */
  error: string | null;
  /** Bumped whenever the local recording library changed (finalized, queued, upload progressed) so
   *  list screens know to re-read it without owning the lifecycle. */
  libraryVersion: number;
}

let recorder: SegmentRecorder | null = null;
let active: LocalRecording | null = null;
/** The recordingId whose finalize has begun - each capture finalizes at most once. */
let finalized: string | null = null;
let state: RecordingSessionState = {
  phase: "idle",
  recordingId: null,
  title: "",
  notes: [],
  error: null,
  libraryVersion: 0,
};
const listeners = new Set<() => void>();

function emit(patch: Partial<RecordingSessionState>): void {
  state = { ...state, ...patch };
  for (const l of listeners) l();
}

function bumpLibrary(): void {
  emit({ libraryVersion: state.libraryVersion + 1 });
}

function defaultTitle(): string {
  return `Recording ${new Date().toLocaleString([], { dateStyle: "medium", timeStyle: "short" })}`;
}

// ONE serialized queue for every durable-header mutation (title, notes, per-segment counters,
// finalize). Read-modify-write on the same IndexedDB row from concurrent async paths is how a late
// title save used to be able to erase a stop's endedAt. The chain survives a failed step; the
// failure surfaces to that step's caller only.
let headerChain: Promise<void> = Promise.resolve();
function chainHeaderWrite<T>(work: () => Promise<T>): Promise<T> {
  const next = headerChain.then(work);
  headerChain = next.then(
    () => undefined,
    () => undefined,
  );
  return next;
}

/** True while this capture (by id) is still the live one - stale continuations must not write. */
function owns(recordingId: string): boolean {
  return active !== null && active.recordingId === recordingId;
}

/** Finalize the active capture into the automatic upload path. Runs at most once per capture.
 *  interruptedReason is null for a user stop; for a system stop it is recorded on the row AND as a
 *  timestamped note, which rides the complete call into the transcript - so the truncation stays
 *  visible after the delivered local row is deleted. */
async function finalizeActive(interruptedReason: string | null): Promise<void> {
  const rec = active;
  if (rec === null || finalized === rec.recordingId) return;
  finalized = rec.recordingId;
  let queuedId: string | null = null;
  try {
    await chainHeaderWrite(async () => {
      const fresh = await getRecording(rec.recordingId);
      if (fresh === null) return;
      if (fresh.segments === 0) {
        // Nothing was captured (stopped within the first second) - an empty recording can never
        // pass the server's completeness gate, so it is removed rather than shown as sendable.
        await deleteRecording(fresh.recordingId);
        return;
      }
      // Stop finalizes AND queues the upload - no Send step (the Android recorder's "uploaded
      // automatically" bar, issue devthrottle_internal#966).
      fresh.state = "queued";
      fresh.endedAt = new Date().toISOString();
      fresh.title = state.title.trim() || fresh.title;
      if (interruptedReason !== null) {
        fresh.interrupted = interruptedReason;
        fresh.notes = [
          ...fresh.notes,
          { tMs: fresh.durationMs, text: `[capture] Recording was cut short here: ${interruptedReason}` },
        ];
      }
      await saveRecording(fresh);
      queuedId = fresh.recordingId;
    });
  } catch (err) {
    // The header write failed (storage trouble). The audio segments already on disk are untouched
    // and recovery will pick the row up on the next open; what must NOT happen is a lying UI.
    emit({ error: `The recording could not be finalized: ${err instanceof Error ? err.message : String(err)}` });
  } finally {
    active = null;
    recorder = null;
    emit({ phase: "idle", recordingId: null, title: "", notes: [] });
    bumpLibrary();
  }
  if (queuedId !== null) {
    const id = queuedId;
    void driveRecordingUpload(id, () => bumpLibrary()).then(
      () => bumpLibrary(),
      () => bumpLibrary(),
    );
  }
}

/** The capture died under us (persist failure or the system suspending the microphone). Surface it
 *  and finalize what exists - the recording keeps everything captured and says it was cut short.
 *  The phase flips to "stopping" IMMEDIATELY so the banner never pulses "Recording" over a dead
 *  microphone, and so a user Stop pressed during this finalize is a harmless no-op. */
function handleCaptureLoss(message: string): void {
  if (active === null || finalized === active.recordingId) return;
  emit({ phase: "stopping", error: `Recording stopped: ${message}` });
  void finalizeActive(message).catch(() => {
    // finalizeActive contains its own failure handling; this guard only prevents an unhandled
    // rejection from a double-failure.
  });
}

export const recordingSession = {
  subscribe(listener: () => void): () => void {
    listeners.add(listener);
    return () => listeners.delete(listener);
  },
  getState(): RecordingSessionState {
    return state;
  },

  // Live display numbers - polled by UI ticks, deliberately not part of the snapshot (they change
  // every frame and would make every subscriber re-render continuously).
  elapsedMs(): number {
    return recorder?.elapsedMs ?? 0;
  },
  segmentCount(): number {
    return recorder?.segmentCount ?? 0;
  },
  level(): number {
    return recorder?.level() ?? 0;
  },
  /** True whenever a capture exists in ANY non-idle phase - including starting (the durable shell
   *  may exist) and stopping (the final segment may still be flushing to disk). The service-worker
   *  update reload checks this: reloading during a stop is exactly the truncation it must avoid. */
  isCapturing(): boolean {
    return state.phase !== "idle";
  },

  /** Begin a new capture. No-op unless idle. Unlimited by design - no maxDurationMs is passed. */
  async start(): Promise<void> {
    if (state.phase !== "idle") return;
    if (!recordingStoreAvailable()) {
      emit({ error: "This browser cannot store recordings durably (no IndexedDB)." });
      return;
    }
    emit({ error: null, phase: "starting" });
    const recordingId = crypto.randomUUID();
    const rec: LocalRecording = {
      recordingId,
      title: state.title.trim() || defaultTitle(),
      deviceId: getInstallId(),
      startedAt: new Date().toISOString(),
      endedAt: null,
      codec: "webm-opus",
      sampleRateHz: 48000,
      channels: 1,
      state: "recording",
      completed: false,
      segments: 0,
      durationMs: 0,
      notes: [],
      createdAt: Date.now(),
      updatedAt: Date.now(),
    };
    try {
      // The durable shell exists BEFORE the microphone opens - from here on, every finalized
      // segment lands in IndexedDB the moment the recorder rotates past it.
      await saveRecording(rec);
      active = rec;
      finalized = null;
      emit({ recordingId, title: rec.title, notes: [] });

      const r = new SegmentRecorder({
        onSegment: async (seg) => {
          const sha = await sha256Hex(seg.blob);
          await saveChunk({
            recordingId,
            index: seg.index,
            blob: seg.blob,
            startMs: seg.startMs,
            durationMs: seg.durationMs,
            bytes: seg.blob.size,
            sha256: sha,
            uploaded: false,
          });
          await chainHeaderWrite(async () => {
            const fresh = await getRecording(recordingId);
            if (fresh === null) return;
            fresh.segments = Math.max(fresh.segments, seg.index + 1);
            fresh.durationMs += seg.durationMs;
            fresh.title = state.title.trim() || fresh.title;
            // The heartbeat: recovery (this tab or ANOTHER tab of the same origin) treats a
            // recently-touched "recording" row as live, never as an orphan to seize.
            fresh.updatedAt = Date.now();
            await saveRecording(fresh);
          });
        },
        onError: (message) => {
          handleCaptureLoss(message);
        },
      });
      recorder = r;
      await r.start();
      // The microphone can die during the await above (handleCaptureLoss finalizes and clears the
      // session). A stale continuation must not resurrect the row or claim to be recording.
      if (!owns(recordingId) || recorder !== r) return;
      // Now that the browser has chosen the container, stamp the real codec + sample rate.
      await chainHeaderWrite(async () => {
        const fresh = await getRecording(recordingId);
        if (fresh === null) return;
        fresh.codec = r.codecLabel;
        fresh.sampleRateHz = r.sampleRateHz;
        await saveRecording(fresh);
      });
      if (!owns(recordingId) || recorder !== r) return;
      emit({ phase: "recording" });
    } catch (err) {
      // Only unwind a capture this continuation still owns - a concurrent loss handler may have
      // already salvaged and finalized it, and deleting the row out from under that would lose it.
      if (owns(recordingId) || active === null) {
        recorder?.dispose();
        recorder = null;
        active = null;
        try {
          if (finalized !== recordingId) await deleteRecording(recordingId);
        } catch {
          /* the row stays; recovery on next open cleans an empty shell */
        }
      }
      emit({
        phase: "idle",
        recordingId: null,
        error: err instanceof Error ? err.message : String(err),
      });
    }
  },

  /** Pause capture (finalizes the open segment and releases the microphone - the honest signal). */
  async pause(): Promise<void> {
    const r = recorder;
    if (r === null || state.phase !== "recording") return;
    const id = state.recordingId;
    await r.pause();
    // A stop or loss that landed during the await owns the phase now.
    if (recorder === r && id !== null && owns(id) && state.phase === "recording") emit({ phase: "paused" });
  },

  /** Resume a paused capture. A failed microphone reopen surfaces as error; capture stays paused. */
  async resume(): Promise<void> {
    const r = recorder;
    if (r === null || state.phase !== "paused") return;
    const id = state.recordingId;
    try {
      await r.resume();
      if (recorder === r && id !== null && owns(id) && state.phase === "paused") emit({ phase: "recording" });
    } catch (err) {
      emit({ error: err instanceof Error ? err.message : String(err) });
    }
  },

  /** Stop and finalize: the recording is queued for automatic upload. A stop while a loss-finalize
   *  is already in flight is a no-op (that finalize owns the ending). */
  async stop(): Promise<void> {
    const r = recorder;
    if (r === null || (state.phase !== "recording" && state.phase !== "paused")) return;
    emit({ phase: "stopping" });
    try {
      await r.stop();
    } catch {
      // The recorder is already dead; finalize still queues everything that reached disk.
    }
    await finalizeActive(null);
  },

  /** Update the working title (editable until stop). Persisted via persistTitle / at finalize. */
  setTitle(title: string): void {
    emit({ title });
  },

  /** Write the working title through to the durable header row (e.g. on input blur). */
  async persistTitle(): Promise<void> {
    const rec = active;
    if (rec === null) return;
    const id = rec.recordingId;
    await chainHeaderWrite(async () => {
      if (finalized === id) return; // the stop already wrote the final title
      const fresh = await getRecording(id);
      if (fresh === null || fresh.state !== "recording") return;
      fresh.title = state.title.trim() || fresh.title;
      await saveRecording(fresh);
    });
  },

  /** Attach a timestamped note to the live capture. Persisted immediately. */
  async addNote(text: string): Promise<void> {
    const trimmed = text.trim();
    const r = recorder;
    const rec = active;
    if (trimmed === "" || r === null || rec === null) return;
    const id = rec.recordingId;
    const note: LocalNote = { tMs: Math.round(r.elapsedMs), text: trimmed };
    await chainHeaderWrite(async () => {
      const fresh = await getRecording(id);
      if (fresh === null) return;
      fresh.notes = [...fresh.notes, note];
      await saveRecording(fresh);
      if (owns(id)) emit({ notes: fresh.notes });
    });
  },

  /** Dismiss a surfaced capture error (the library row keeps its own interrupted marker). */
  clearError(): void {
    emit({ error: null });
  },

  /** List screens call this after their own store writes (retry, discard) so every other
   *  subscriber re-reads the library too. */
  notifyLibraryChanged(): void {
    bumpLibrary();
  },
};

/** React subscription to the session snapshot. Live numbers (elapsed, level, segment count) are
 *  polled from recordingSession on a display tick instead - see the snapshot's field docs. */
export function useRecordingSession(): RecordingSessionState {
  return useSyncExternalStore(recordingSession.subscribe, recordingSession.getState, recordingSession.getState);
}
