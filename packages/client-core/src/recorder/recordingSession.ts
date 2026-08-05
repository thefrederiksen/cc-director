// The app-level recording session (recorder-unlimited-capture mission). Exactly one capture can be
// live per app instance, and it must SURVIVE IN-APP NAVIGATION: the SegmentRecorder used to be
// constructed inside the Recorder PAGE component, so leaving the page - to check a session, use
// voice mode, anything - unmounted the page and stopped the recording. During an all-day conference
// that is fatal. The session lives here, at module scope, above the router: the Recorder page and
// the global recording indicator both subscribe to it, and navigation neither creates nor destroys
// it.
//
// Everything the capture lifecycle needs is owned here - the SegmentRecorder, the active
// LocalRecording header, the title as edited so far, the notes, and the finalize-and-queue-upload
// step. Pages read a snapshot via useRecordingSession() (useSyncExternalStore) and poll the live
// numbers (elapsed, level, segment count) on their own display ticks, exactly as the page previously
// polled its local recorder.
//
// What a PWA honestly cannot promise: capture through a locked screen. The app holds a screen wake
// lock while foregrounded (useScreenWakeLock), which keeps the screen - and therefore capture -
// alive; but if the phone does lock or the browser suspends the page, the microphone track ends,
// the SegmentRecorder salvages and stops, and the recording is finalized here with `interrupted`
// set so the library says plainly it was cut short. Truncation is surfaced, never hidden.

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
  /** A capture failure or system stop, in plain English. Cleared when the next capture starts. */
  error: string | null;
  /** Bumped whenever the local recording library changed (finalized, queued, upload progressed) so
   *  list screens know to re-read it without owning the lifecycle. */
  libraryVersion: number;
}

let recorder: SegmentRecorder | null = null;
let active: LocalRecording | null = null;
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

/** Finalize the active capture into the automatic upload path. interruptedReason is null for a
 *  user stop; for a system stop it is recorded on the row so a cut-off recording says so. */
async function finalizeActive(interruptedReason: string | null): Promise<void> {
  const rec = active;
  if (rec === null) return;
  let queuedId: string | null = null;
  const fresh = await getRecording(rec.recordingId);
  if (fresh !== null) {
    if (fresh.segments === 0) {
      // Nothing was captured (stopped within the first second) - an empty recording can never pass
      // the server's completeness gate, so it is removed rather than shown as sendable.
      await deleteRecording(fresh.recordingId);
    } else {
      // Stop finalizes AND queues the upload - no Send step (the Android recorder's "uploaded
      // automatically" bar, issue devthrottle_internal#966).
      fresh.state = "queued";
      fresh.endedAt = new Date().toISOString();
      fresh.title = state.title.trim() || fresh.title;
      if (interruptedReason !== null) fresh.interrupted = interruptedReason;
      await saveRecording(fresh);
      queuedId = fresh.recordingId;
    }
  }
  active = null;
  recorder = null;
  emit({ phase: "idle", recordingId: null, title: "", notes: [] });
  bumpLibrary();
  if (queuedId !== null) {
    void driveRecordingUpload(queuedId, () => bumpLibrary()).then(() => bumpLibrary());
  }
}

/** The capture died under us (persist failure or the system suspending the microphone). Surface it
 *  and finalize what exists - the recording keeps everything captured and says it was cut short. */
async function handleCaptureLoss(message: string): Promise<void> {
  emit({ error: `Recording stopped: ${message}` });
  await finalizeActive(message);
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
  isCapturing(): boolean {
    return state.phase === "recording" || state.phase === "paused";
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
    };
    try {
      // The durable shell exists BEFORE the microphone opens - from here on, every finalized
      // segment lands in IndexedDB the moment the recorder rotates past it.
      await saveRecording(rec);
      active = rec;
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
          const fresh = await getRecording(recordingId);
          if (fresh !== null) {
            fresh.segments = Math.max(fresh.segments, seg.index + 1);
            fresh.durationMs += seg.durationMs;
            fresh.title = state.title.trim() || fresh.title;
            await saveRecording(fresh);
          }
        },
        onError: (message) => {
          void handleCaptureLoss(message);
        },
      });
      recorder = r;
      await r.start();
      // Now that the browser has chosen the container, stamp the real codec + sample rate.
      rec.codec = r.codecLabel;
      rec.sampleRateHz = r.sampleRateHz;
      await saveRecording(rec);
      emit({ phase: "recording" });
    } catch (err) {
      recorder?.dispose();
      recorder = null;
      active = null;
      await deleteRecording(recordingId);
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
    await r.pause();
    emit({ phase: "paused" });
  },

  /** Resume a paused capture. A failed microphone reopen surfaces as error; capture stays paused. */
  async resume(): Promise<void> {
    const r = recorder;
    if (r === null || state.phase !== "paused") return;
    try {
      await r.resume();
      emit({ phase: "recording" });
    } catch (err) {
      emit({ error: err instanceof Error ? err.message : String(err) });
    }
  },

  /** Stop and finalize: the recording is queued for automatic upload. */
  async stop(): Promise<void> {
    const r = recorder;
    if (r === null || (state.phase !== "recording" && state.phase !== "paused")) return;
    emit({ phase: "stopping" });
    await r.stop();
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
    const fresh = await getRecording(rec.recordingId);
    if (fresh !== null) {
      fresh.title = state.title.trim() || fresh.title;
      await saveRecording(fresh);
    }
  },

  /** Attach a timestamped note to the live capture. Persisted immediately. */
  async addNote(text: string): Promise<void> {
    const trimmed = text.trim();
    const r = recorder;
    const rec = active;
    if (trimmed === "" || r === null || rec === null) return;
    const note: LocalNote = { tMs: Math.round(r.elapsedMs), text: trimmed };
    const fresh = await getRecording(rec.recordingId);
    if (fresh !== null) {
      fresh.notes = [...fresh.notes, note];
      await saveRecording(fresh);
      emit({ notes: fresh.notes });
    }
  },

  /** Clear a surfaced capture error (the library row keeps its own interrupted marker). */
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
