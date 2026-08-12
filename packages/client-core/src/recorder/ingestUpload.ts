// The durable upload driver for long-form recordings (issue #958): drives one locally stored
// recording through the Gateway's /ingest contract - register, per-segment PUT (idempotent by index +
// SHA-256), then the complete call that delivers the manifest and NOTES and queues transcription.
//
// Durability discipline (the same bar as the dictation backgroundSend pipeline, issue #1006, and the
// Android recorder's upload queue):
//   - the audio is ALREADY durable before this file is ever involved (segments are persisted to
//     IndexedDB as the recorder rotates past them);
//   - every confirmed segment is marked `uploaded` in the store IMMEDIATELY, so a dropped connection
//     resumes at the first unsent segment, never from zero and never re-sending bytes the server has;
//   - COMPLETED means the server ACKed the complete call (HTTP 202) - that is the ONLY terminal
//     condition. All-segments-sent is not done: the notes ride only on the complete call, so a kill
//     between upload and complete leaves the recording queued and the complete call is retried;
//   - the server's completeness gate answering 409 names exactly the segment indices to re-send;
//     those are re-armed and the pass re-runs (never a blind retry against a gate it cannot pass);
//   - any transport or server failure leaves the recording in "retry" with a plain-English reason:
//     saved and retryable, never discarded. Retries fire on app load, on connectivity returning, and
//     from the recorder screen. The web platform cannot continue an upload after the tab is killed
//     (no WorkManager equivalent); what it CAN do - resume on next open from the durable store - is
//     exactly what this module does.
//
// Every request is root-relative to the Gateway front door and carries the device Bearer via
// authHeaders(), like every other client-core Gateway call.

import { authHeaders } from "../api/client";
import { getInstallId } from "../auth/deviceKey";
import { listAccounts } from "../auth/accountStore";
import {
  getRecording,
  listChunks,
  listRecordings,
  markChunkUploaded,
  saveRecording,
  deleteRecording,
  type LocalChunk,
  type LocalRecording,
} from "./recordingStore";
import { needsUpload, requeueIndicesForResend, shouldUploadAudio } from "./uploadGate";

/** Lowercase hex SHA-256 of a segment's bytes - the exact form the server computes and compares. */
export async function sha256Hex(blob: Blob): Promise<string> {
  const buf = await blob.arrayBuffer();
  const digest = await crypto.subtle.digest("SHA-256", buf);
  return Array.from(new Uint8Array(digest))
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}

/** The server's per-segment file extension for a codec label (mirror of CodecToExt). */
export function extForCodec(codec: string): string {
  const c = codec.toLowerCase();
  if (c.includes("m4a") || c.includes("aac") || c.includes("mp4")) return "m4a";
  if (c.includes("mp3") || c.includes("mpeg")) return "mp3";
  if (c.includes("wav")) return "wav";
  if (c.includes("webm")) return "webm";
  if (c.includes("ogg") || c.includes("opus")) return "ogg";
  return "m4a";
}

/** The manifest JSON for register/complete, built from the stored recording + its chunks. The server
 *  deserializes case-insensitively, so camelCase field names match the C# RecordingManifest. */
export function buildManifest(rec: LocalRecording, chunks: LocalChunk[]): Record<string, unknown> {
  const ext = extForCodec(rec.codec);
  return {
    recordingId: rec.recordingId,
    title: rec.title,
    deviceId: rec.deviceId,
    startedAt: rec.startedAt,
    endedAt: rec.endedAt,
    sampleRateHz: rec.sampleRateHz,
    channels: rec.channels,
    codec: rec.codec,
    chunks: chunks.map((c) => ({
      index: c.index,
      file: `${String(c.index).padStart(4, "0")}.${ext}`,
      startMs: c.startMs,
      durationMs: c.durationMs,
      bytes: c.bytes,
      sha256: c.sha256,
    })),
    notes: rec.notes.map((n) => ({ tMs: n.tMs, text: n.text })),
  };
}

/** The server's status answer (camelCase mirror of RecordingStatusDto). */
export interface ServerRecordingStatus {
  recordingId: string;
  title: string;
  /** One of: receiving, incomplete, queued, transcribing, cleaning, transcribed, error. */
  state: string;
  chunksReceived: number;
  chunksTotal: number;
  chunksTranscribed: number;
  vaultDocId?: string | null;
  error?: string | null;
  transcript?: string | null;
  attempts?: number;
  nextRetryAtUtc?: string | null;
  missingOrBadIndices?: number[] | null;
}

/** Poll one recording's server-side status (upload gate + transcription progress). Throws on non-2xx. */
export async function getServerStatus(recordingId: string, signal?: AbortSignal): Promise<ServerRecordingStatus> {
  const res = await fetch(`/ingest/recording/${encodeURIComponent(recordingId)}/status`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw new Error(`GET /ingest/recording/${recordingId}/status failed: ${res.status}`);
  return (await res.json()) as ServerRecordingStatus;
}

async function bodyError(res: Response): Promise<string> {
  try {
    const text = await res.text();
    if (text.length > 0) {
      try {
        const parsed = JSON.parse(text) as { error?: string };
        if (typeof parsed.error === "string" && parsed.error.length > 0) return parsed.error;
      } catch {
        return text.slice(0, 300);
      }
      return text.slice(0, 300);
    }
  } catch {
    /* body unreadable */
  }
  return `HTTP ${res.status}`;
}

// One drive per recording at a time, across every trigger (screen retry, online event, app load):
// a second trigger while a pass is in flight is a no-op, exactly like backgroundSend's guard.
const inFlight = new Set<string>();

/** How the upload pass ended. "delivered" is the ONLY terminal success (complete ACKed). */
export type UploadOutcome = "delivered" | "retry" | "not-found" | "already-driving";

/**
 * Drive one recording's upload to its terminal condition: every segment confirmed on the server AND
 * the complete call acknowledged (202). On success the local copy is deleted - the server has
 * verified it holds every byte plus the manifest and notes. On any failure the recording is left in
 * "retry" with the reason on the record, its audio untouched, ready for the next pass.
 *
 * `onProgress` fires after every persisted state change so the screen re-reads the store and the
 * library row reflects the pass live.
 */
export async function driveRecordingUpload(
  recordingId: string,
  onProgress?: () => void,
): Promise<UploadOutcome> {
  if (inFlight.has(recordingId)) return "already-driving";
  inFlight.add(recordingId);
  try {
    return await drivePass(recordingId, onProgress);
  } finally {
    inFlight.delete(recordingId);
  }
}

async function drivePass(recordingId: string, onProgress?: () => void): Promise<UploadOutcome> {
  const rec = await getRecording(recordingId);
  if (rec === null) return "not-found";
  if (!needsUpload(rec.state, rec.completed)) return rec.completed ? "delivered" : "not-found";

  const chunks = await listChunks(recordingId);
  if (chunks.length === 0) {
    // Nothing captured - the server's completeness gate would refuse it. Surface, do not loop.
    rec.state = "retry";
    rec.lastError = "This recording has no audio segments, so it cannot be sent.";
    await saveRecording(rec);
    onProgress?.();
    return "retry";
  }

  try {
    if (shouldUploadAudio(rec.state)) {
      rec.state = "uploading";
      rec.uploadPhase = "sending";
      rec.uploadTotal = chunks.length;
      rec.uploadCurrent = chunks.filter((c) => c.uploaded).length;
      rec.lastError = undefined;
      await saveRecording(rec);
      onProgress?.();

      // Register (idempotent on the recording id).
      const regRes = await fetch("/ingest/recording", {
        method: "POST",
        headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
        body: JSON.stringify({
          recordingId: rec.recordingId,
          title: rec.title,
          deviceId: rec.deviceId,
          startedAt: rec.startedAt,
          codec: rec.codec,
          sampleRateHz: rec.sampleRateHz,
          channels: rec.channels,
        }),
      });
      if (!regRes.ok) throw new Error(`register failed: ${await bodyError(regRes)}`);

      // Push every not-yet-confirmed segment; mark each win in the store IMMEDIATELY so a later
      // failure resumes after it (the per-segment resume point, Android principle #2).
      for (const chunk of chunks) {
        if (chunk.uploaded) continue;
        const putRes = await fetch(
          `/ingest/recording/${encodeURIComponent(rec.recordingId)}/chunk/${chunk.index}`,
          {
            method: "PUT",
            headers: {
              "Content-Type": "application/octet-stream",
              "X-Chunk-Sha256": chunk.sha256,
              ...authHeaders(),
            },
            body: chunk.blob,
          },
        );
        if (!putRes.ok) throw new Error(`segment ${chunk.index} failed: ${await bodyError(putRes)}`);
        chunk.uploaded = true;
        await markChunkUploaded(rec.recordingId, chunk.index, true);
        rec.uploadCurrent = chunks.filter((c) => c.uploaded).length;
        await saveRecording(rec);
        onProgress?.();
      }

      rec.state = "uploaded";
      rec.uploadPhase = null;
      await saveRecording(rec);
      onProgress?.();
    }

    // The complete call: delivers the manifest and NOTES, queues transcription, answers 202. This is
    // the recording's real terminal condition - retried until acknowledged.
    const compRes = await fetch(`/ingest/recording/${encodeURIComponent(rec.recordingId)}/complete`, {
      method: "POST",
      headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
      body: JSON.stringify(buildManifest(rec, chunks)),
    });

    if (compRes.status === 409) {
      // The completeness gate named segments that are missing or bad ON THE SERVER even though the
      // phone believed them uploaded. Re-arm exactly those and leave the recording queued for the
      // next pass (issue #591's rule: never retry complete against a gate it cannot pass).
      const status = (await compRes.json()) as ServerRecordingStatus;
      const resend = requeueIndicesForResend(
        status.missingOrBadIndices,
        chunks.map((c) => c.index),
      );
      for (const index of resend) {
        await markChunkUploaded(rec.recordingId, index, false);
      }
      rec.state = "retry";
      rec.lastError = `The server is missing segment${resend.length === 1 ? "" : "s"} ${resend.join(", ")}; they will be re-sent.`;
      await saveRecording(rec);
      onProgress?.();
      return "retry";
    }

    if (!compRes.ok) throw new Error(`complete failed: ${await bodyError(compRes)}`);

    // 202: the server holds every verified byte plus the manifest and notes, and transcription is
    // queued. Fully delivered - the local copy's job is done.
    rec.state = "uploaded";
    rec.completed = true;
    await saveRecording(rec);
    await deleteRecording(rec.recordingId);
    onProgress?.();
    return "delivered";
  } catch (err) {
    const message = err instanceof Error ? err.message : String(err);
    const fresh = await getRecording(recordingId);
    if (fresh !== null) {
      fresh.state = "retry";
      fresh.uploadPhase = null;
      fresh.lastError = `Send failed: ${message}. The recording is saved on this phone and will be retried.`;
      await saveRecording(fresh);
      onProgress?.();
    }
    return "retry";
  }
}

/**
 * Re-drive every recording with upload work left (ready/queued/retry/uploading/uploaded-not-completed)
 * from the durable store. Called at app load (the resume-on-open pattern of resumePendingDictations)
 * and when connectivity returns. Uploading is automatic - a stored recording never waits for a Send
 * press, including "ready" rows a pre-auto-send build left behind (issue devthrottle_internal#966).
 */
export async function resumePendingRecordingUploads(onProgress?: () => void): Promise<void> {
  const all = await listRecordings();
  // A RECORDING BELONGS TO THE ACCOUNT THAT MADE IT (devthrottle_internal #1509). This store is one
  // IndexedDB database per ORIGIN, so two accounts on one browser share it, while the upload
  // authenticates as whichever account is ACTIVE. Without this filter, recording something with no
  // connection on one account and then switching would upload that audio into the OTHER account's
  // tenant on the next load - the person's own voice, filed under the wrong identity, and nothing on
  // screen would say so.
  //
  // The owner is already on the row: deviceId is the install id, and every account now carries its own.
  // A recording belonging to another account is LEFT ALONE rather than dropped - it goes up when that
  // account is active again, which is what the durable store promises. A row with no deviceId predates
  // this and is driven as before.
  // Inert while this browser holds ONE account (the overwhelming case, and every case before #1509):
  // there is no other account a recording could belong to, so nothing is withheld. The filter only
  // starts excluding once a second account exists and the question becomes answerable.
  const single = listAccounts().length <= 1;
  const mine = getInstallId();
  for (const rec of all) {
    if (!needsUpload(rec.state, rec.completed)) continue;
    if (!single && rec.deviceId && rec.deviceId !== mine) continue;
    await driveRecordingUpload(rec.recordingId, onProgress);
  }
}
