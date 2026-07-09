import { uploadDictationToSession } from "../api/client";
import { deletePending, getPending, listPending, savePending, type PendingDictation } from "./pendingStore";
import { clearDictationStatus, publishDictationStatus } from "./status";

// The durable Send pipeline + background retry driver for the mobile Speak dialog (issue #1006,
// strengthened for #1182). The instant the user hits Send the dialog hands the recorded audio here and
// closes; we persist the raw audio locally (IndexedDB) BEFORE any network work, then drive delivery to
// the Gateway. The Gateway assembles, transcribes, and INJECTS the turn into the session itself, so once
// the audio is up a dead tab or a dropped connection can no longer lose it.
//
// The on-device copy is the single source of truth. A recorded dictation is NEVER lost on a bad
// connection and is NEVER aged out: it stays in the durable queue until the server confirms it owns the
// turn (submitted, or deliberately dropped as stale), or the user explicitly abandons it (a later Task).
// Delivery keeps retrying automatically - hard for the first hour, then throttled to slow background
// attempts, forever - and resumes the instant connectivity returns (the browser `online` event and app
// foreground) and on every app load. Every attempt is idempotent by the upload id (which is also the
// server Idempotency-Key), so a retry or a resume can never inject the same dictation twice (this is the
// direction issue #1181 reverses from #1135's over-correction: dedupe by upload id, never by dropping the
// queue).
//
// It deliberately lives OUTSIDE the DictationDialog component: the dialog unmounts (and disposes its
// recorder) the moment Send is pressed, so the work must not be tied to the dialog's lifecycle.

// ---- retry cadence ---------------------------------------------------------------------------------
// Try hard for the first hour after the clip was recorded, then throttle to a slow background attempt so
// a long outage does not hammer a dead connection - but never stop, and never discard the audio.
const HARD_WINDOW_MS = 60 * 60 * 1000; // "hard" retries for the first hour since the clip was recorded
const HARD_MIN_DELAY_MS = 2_000; // first hard retry after two seconds
const HARD_MAX_DELAY_MS = 15_000; // hard exponential backoff caps at fifteen seconds
const THROTTLED_DELAY_MS = 5 * 60 * 1000; // after the hard hour (or out of credits): one slow attempt every five minutes

// The honest, plain-English held lines. A held dictation is saved and still being delivered - the copy
// never says "was not transcribed", because it is held, not lost (criterion 8).
const WAITING_FOR_CONNECTION_MESSAGE =
  "Waiting for a connection - your recording is saved and will send automatically.";
const RETRYING_MESSAGE = "Saved - still trying to send your recording...";
const THROTTLED_MESSAGE =
  "Saved - still trying to send your recording in the background. It stays saved until it is delivered.";
const NO_DURABLE_STORE_MESSAGE =
  "This browser can't save recordings for reliable delivery, so this dictation couldn't be sent. Record it again in a normal browser tab.";

// The parked (permanent-failure) messages (issue #1184). A parked clip is saved-and-retryable, never a
// permanent loss: the copy says the audio is safe on the device and the user can retry it. There is no
// auto-loop; only an explicit Retry re-drives it (it will succeed once the server transcode-and-split fix
// lands). The size wording is the exact text agreed in the issue.
const PARKED_TOO_LARGE_MESSAGE =
  "This recording is too long to transcribe right now; it is saved on your device and you can retry it.";
const PARKED_UNSUPPORTED_FORMAT_MESSAGE =
  "This recording is in a format we can't transcribe right now; it is saved on your device and you can retry it.";

// The plain saved-and-retryable line for a parked clip, chosen by the allow-listed reason on the record.
function parkMessage(reason: string | undefined): string {
  return reason === "unsupported-format" ? PARKED_UNSUPPORTED_FORMAT_MESSAGE : PARKED_TOO_LARGE_MESSAGE;
}

/** The audio buffer + context the dialog hands up when Send is pressed. */
export interface CapturedUtterance {
  /** The raw recorded audio exactly as the microphone produced it (WebM/Opus etc.). */
  blob: Blob;
  /** Wall-clock milliseconds the segment was capturing (capture-health, issue #863). */
  recordedMs: number;
  /** Earlier Pause/Resume dictation segments, already turned to text, joined ahead of this final
   *  segment. Empty in the common "just talk and Send" case. */
  prefixText: string;
}

/** Callbacks so the host can react to the rare hard failure (durable storage unavailable). A normal send
 *  is durable and its progress shows on the status strip and the roster, so success and held states need
 *  no host callback - the status store is the single source of truth. */
export interface BackgroundSendHooks {
  onError?: (message: string) => void;
  /** Called only when the clip could NOT be saved durably (so nothing is queued), so the host can restore
   *  any typed compose text it cleared at dialog-close time. It is NOT called for a held/retrying send:
   *  the typed text is part of the durable record and is delivered with the dictation. */
  onFailed?: () => void;
  /** Typed text the caret split the dictation around (Terminal Speak's Insert-then-Enter). The voice
   *  case omits this and the transcript is submitted alone. */
  composeParts?: { before: string; after: string };
  /** The session's TotalBufferBytes at record time, for the Gateway's "session moved on" guard when a
   *  clip is resumed later. Omit when unknown (the guard is then skipped for safety). */
  baselineBufferBytes?: number;
}

// ---- driver state ----------------------------------------------------------------------------------
// The in-flight guard: upload ids currently being driven. All four triggers (a fresh Send, app load, the
// browser `online` event, app foreground, and the explicit "Upload now") funnel through driveRecord,
// which no-ops if the clip is already being driven - so two triggers can never run two concurrent drivers
// for the same clip and cannot double-inject it. The server single-flights complete per upload id too,
// but we do not rely on that alone (Manager lock-in for #1182).
const _inFlight = new Set<string>();
// Scheduled next-attempt timers, keyed by upload id, so a new trigger can cancel a waiting timer and
// drive immediately.
const _timers = new Map<string, ReturnType<typeof setTimeout>>();
let _listenersInstalled = false;

// Persist the recorded audio durably the instant Send is pressed, then drive the first delivery attempt.
// If durable storage is genuinely unavailable we tell the user clearly rather than doing a silent
// one-shot send (a one-shot on a bad connection is exactly the loss this feature exists to prevent).
export async function backgroundTranscribeAndSend(
  sessionId: string,
  captured: CapturedUtterance,
  hooks: BackgroundSendHooks = {},
): Promise<void> {
  ensureRetryListeners();

  const rec: PendingDictation = {
    id: crypto.randomUUID(),
    sessionId,
    blob: captured.blob,
    recordedMs: captured.recordedMs,
    before: hooks.composeParts?.before ?? "",
    after: hooks.composeParts?.after ?? "",
    prefix: captured.prefixText ?? "",
    baselineBufferBytes: hooks.baselineBufferBytes ?? 0,
    createdAt: Date.now(),
  };

  // Show the very first step (before any network work) so the status strip appears the instant Send is
  // pressed and the screen is never quiet.
  publishDictationStatus({ sessionId, uploadId: rec.id, phase: "saving" });

  try {
    await savePending(rec);
  } catch {
    // Durable storage genuinely unavailable (rare, e.g. a private-mode tab with IndexedDB disabled): the
    // clip cannot be queued, so say so loudly and restore the typed text. We do NOT silently one-shot it.
    publishDictationStatus({
      sessionId,
      uploadId: rec.id,
      phase: "failed",
      retryable: false,
      error: NO_DURABLE_STORE_MESSAGE,
    });
    hooks.onError?.(NO_DURABLE_STORE_MESSAGE);
    hooks.onFailed?.();
    return;
  }

  // Saved durably. Drive the first delivery attempt now. resumed:false so an immediate send injects
  // without the moved-on guard; any failure becomes a held-and-retrying state the driver owns. The
  // caller does not await this (it fired and moved on), but awaiting the first attempt here keeps the
  // returned promise honest about when that attempt settled.
  await driveRecord(rec, { resumed: false, attempt: 0 });
}

// Re-drive every recorded-but-unsent dictation on app load (issue #1006/#1182): a clip whose upload was
// interrupted by a refresh, a crash, or a dropped connection is resumed from the durable copy. Idempotent
// by upload id, so a clip that actually landed before the tab died is de-duplicated by the Gateway rather
// than double-submitted. This also installs the connectivity listeners so a resume happens the moment the
// network returns, not only on the next load.
export async function resumePendingDictations(): Promise<void> {
  ensureRetryListeners();
  let all: PendingDictation[];
  try {
    all = await listPending();
  } catch {
    return; // no durable store; nothing to resume
  }
  // A parked clip (permanent failure, issue #1184) is NOT auto-driven: re-publish its saved-and-retryable
  // status so the strip and roster show it after a reopen, but never re-drive it. Everything else resumes.
  await Promise.all(
    all.map((rec) => {
      if (rec.parkedReason) {
        publishParked(rec, rec.parkedReason);
        return Promise.resolve();
      }
      return driveRecord(rec, { resumed: true, attempt: 0 });
    }),
  );
}

// The explicit "Upload now" control on the status strip: kick a waiting or throttled clip back to
// full-speed delivery immediately (resetting the backoff to the hard cadence). If the durable record is
// gone (already delivered, or abandoned) the stale status is cleared so a dead strip cannot linger.
export async function retryPendingDictation(uploadId: string): Promise<void> {
  let rec: PendingDictation | null;
  try {
    rec = await getPending(uploadId);
  } catch {
    return;
  }
  if (rec === null) {
    clearDictationStatus(uploadId);
    return;
  }
  // If it was PARKED after a permanent failure (issue #1184), this explicit Retry moves it back to active:
  // clear the parked reason on the durable record first, so the auto-triggers stop skipping it and this
  // deliberate drive re-enters the normal flow. Harmless (a no-op) for a non-parked held clip.
  if (rec.parkedReason) {
    const reactivated: PendingDictation = { ...rec, parkedReason: undefined };
    try {
      await savePending(reactivated);
    } catch {
      // Could not clear the flag durably: still drive from the in-memory reactivated record below.
    }
    rec = reactivated;
  }
  clearScheduled(uploadId);
  await driveRecord(rec, { resumed: true, attempt: 0 });
}

// ---- internals -------------------------------------------------------------------------------------

interface DriveOptions {
  /** True for any retry/resume (applies the server's moved-on guard); false only for the first immediate send. */
  resumed: boolean;
  /** Backoff step for scheduling the NEXT attempt. Reset to 0 by a fresh send, a connectivity kick, and Upload now. */
  attempt: number;
}

// Drive one durable clip through a single delivery attempt, then either delete it (the server owns the
// turn) or keep it and schedule the next attempt. Guarded so concurrent triggers cannot double-drive one
// clip.
async function driveRecord(rec: PendingDictation, opts: DriveOptions): Promise<void> {
  if (_inFlight.has(rec.id)) return; // another trigger is already driving this clip
  _inFlight.add(rec.id);
  clearScheduled(rec.id); // we are driving now; cancel any waiting timer

  try {
    if (isOffline()) {
      // No point calling out with no network: show waiting-for-connection and lean on the `online`
      // listener, with a slow fallback timer so we still recover even if that event is missed.
      publishHeld(rec, WAITING_FOR_CONNECTION_MESSAGE);
      scheduleNext(rec, opts.attempt, false);
      return;
    }

    const outcome = await uploadDictationToSession({
      sessionId: rec.sessionId,
      uploadId: rec.id,
      audio: rec.blob,
      before: rec.before,
      after: rec.after,
      prefix: rec.prefix,
      baselineBufferBytes: rec.baselineBufferBytes,
      resumed: opts.resumed,
    });

    if (outcome.terminal) {
      // The server owns the turn. This covers a fresh delivery, a server that DEDUPED a delivery it had
      // already made (a cached-delivered outcome from the durable record, issue #1183 - treated identically
      // to a fresh success), a deliberately dropped stale/empty clip, and an ABANDONED upload id. In every
      // case the on-device copy is dropped so the queue does not accumulate and the clip is never re-driven;
      // the client already acknowledged the outcome to the Gateway (in uploadDictationToSession). Publish
      // the terminal status: a brief "done" for a delivered turn, or a quiet clear when nothing was injected
      // (moved-on, empty, or abandoned - there is nothing to acknowledge on screen).
      await deletePending(rec.id);
      if (outcome.submitted) {
        publishDictationStatus({ sessionId: rec.sessionId, uploadId: rec.id, phase: "done" });
      } else {
        clearDictationStatus(rec.id);
      }
      return;
    }

    if (outcome.permanent) {
      // Genuinely permanent, non-retryable failure (issue #1184): PARK the clip. Keep the audio, stop the
      // auto-loop (cancel any timer and do NOT scheduleNext), and persist the parked reason on the record so
      // every automatic trigger skips it - including across a close and reopen. It waits for an explicit
      // user Retry; nothing here re-drives it. The reason is one of the allow-listed permanent reasons.
      const reason = outcome.permanentReason ?? "audio-too-large";
      try {
        await savePending({ ...rec, parkedReason: reason });
      } catch {
        // Persisting the parked flag failed (durable store hiccup): the in-memory return below still stops
        // THIS drive; a later trigger may re-attempt, which simply re-parks. We never re-drive in a tight loop.
      }
      publishParked(rec, reason);
      return;
    }

    // Held: keep the audio and keep trying. Publish the honest held reason and schedule the next attempt.
    publishHeld(rec, heldMessage(rec, outcome.error));
    scheduleNext(rec, opts.attempt, Boolean(outcome.outOfCredits));
  } catch {
    // uploadDictationToSession returns a held result rather than throwing, so this is a defensive net for
    // an unexpected fault: keep the audio and keep trying - never drop it.
    publishHeld(rec, heldMessage(rec, undefined));
    scheduleNext(rec, opts.attempt, false);
  } finally {
    _inFlight.delete(rec.id);
  }
}

// Re-read a record by id (it may already be delivered and gone) and drive it. Used by the scheduled
// retry timers.
async function driveById(id: string, opts: DriveOptions): Promise<void> {
  let rec: PendingDictation | null;
  try {
    rec = await getPending(id);
  } catch {
    return;
  }
  if (rec === null) {
    clearScheduled(id); // delivered or abandoned; nothing left to drive
    return;
  }
  if (rec.parkedReason) {
    // Parked between scheduling and firing (issue #1184): never auto-drive it. Defensive - a parked clip is
    // never given a timer, so this should not normally be reached.
    clearScheduled(id);
    publishParked(rec, rec.parkedReason);
    return;
  }
  await driveRecord(rec, opts);
}

// Resume every pending clip immediately at full speed - the connectivity/foreground kick and what the
// `online`/`visibilitychange` listeners call. A parked clip (permanent failure, issue #1184) is skipped:
// it never auto-drives, only an explicit user Retry re-enters the active flow.
async function kickAll(): Promise<void> {
  let all: PendingDictation[];
  try {
    all = await listPending();
  } catch {
    return;
  }
  for (const rec of all) {
    if (rec.parkedReason) continue;
    void driveRecord(rec, { resumed: true, attempt: 0 });
  }
}

// Schedule the next automatic attempt for a held clip. The delay is hard (exponential from two seconds,
// capped at fifteen) for the first hour since the clip was recorded, then throttled to five minutes - and
// out of credits is always throttled (a fast retry cannot conjure credits). Never stops.
function scheduleNext(rec: PendingDictation, attempt: number, outOfCredits: boolean): void {
  clearScheduled(rec.id);
  const delay = nextDelayMs(rec, attempt, outOfCredits);
  const t = setTimeout(() => void driveById(rec.id, { resumed: true, attempt: attempt + 1 }), delay);
  _timers.set(rec.id, t);
}

function nextDelayMs(rec: PendingDictation, attempt: number, outOfCredits: boolean): number {
  const age = Date.now() - rec.createdAt;
  if (outOfCredits || age >= HARD_WINDOW_MS) return THROTTLED_DELAY_MS;
  return Math.min(HARD_MIN_DELAY_MS * 2 ** attempt, HARD_MAX_DELAY_MS);
}

function clearScheduled(id: string): void {
  const t = _timers.get(id);
  if (t !== undefined) {
    clearTimeout(t);
    _timers.delete(id);
  }
}

// The held status line to show: waiting-for-connection when offline, the throttled line once past the hard
// hour, otherwise the specific reason the last attempt returned (or a generic retrying line).
function heldMessage(rec: PendingDictation, reason: string | undefined): string {
  if (isOffline()) return WAITING_FOR_CONNECTION_MESSAGE;
  if (Date.now() - rec.createdAt >= HARD_WINDOW_MS) return THROTTLED_MESSAGE;
  return reason ?? RETRYING_MESSAGE;
}

function publishHeld(rec: PendingDictation, message: string): void {
  publishDictationStatus({
    sessionId: rec.sessionId,
    uploadId: rec.id,
    phase: "held",
    retryable: true,
    error: message,
  });
}

// Publish the parked (permanent-failure) status: saved-and-retryable, with an explicit Retry (retryable
// true) and no auto-loop behind it (issue #1184).
function publishParked(rec: PendingDictation, reason: string): void {
  publishDictationStatus({
    sessionId: rec.sessionId,
    uploadId: rec.id,
    phase: "parked",
    retryable: true,
    error: parkMessage(reason),
  });
}

function isOffline(): boolean {
  return typeof navigator !== "undefined" && navigator.onLine === false;
}

// Install the connectivity/foreground listeners once, so a held clip resumes the instant the network
// returns or the app is brought to the foreground - not only on the next full app load.
function ensureRetryListeners(): void {
  if (_listenersInstalled) return;
  if (typeof window === "undefined") return;
  _listenersInstalled = true;
  window.addEventListener("online", () => void kickAll());
  if (typeof document !== "undefined") {
    document.addEventListener("visibilitychange", () => {
      if (!document.hidden) void kickAll();
    });
  }
}
