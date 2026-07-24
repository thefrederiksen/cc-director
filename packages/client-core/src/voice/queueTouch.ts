// The voice queue's local "listened" marker (voice-mode queue flow, 2026-07-24).
//
// The Voice tab is worked by ear as a first-in-first-out queue: the session that has been waiting
// the longest is read first. When the owner listens to a session and then leaves WITHOUT responding
// or snoozing, that session must drop to the BOTTOM of the queue - it was heard, so everything not
// yet heard goes first, and the unhandled session comes around again later (deliberately: an
// unhandled session keeps coming back until it is dealt with).
//
// The Gateway owns needsYouSince (when the session started needing you); this store owns only the
// phone-local "when did THIS device last open it in voice mode" stamp. That is playback-side state
// in the same family as playbackPositions.ts - which device listened, and when, is a fact only the
// device knows - so it lives on the device, not on the Gateway. The queue position is then
// max(needsYouSince, local touch): untouched sessions keep their Gateway wait order, and a touched
// session re-enters the line as if it had just arrived.

import type { SessionDto } from "../api/client";

const KEY_PREFIX = "dt.voice.touch.";
const _mem = new Map<string, number>();

function storageKey(sid: string): string {
  return KEY_PREFIX + sid;
}

/** Record that this device just opened the session in voice mode (it was listened to, or at least
 *  brought to the ear). Called when leaving the voice screen; also harmless on respond/snooze,
 *  because those remove the session from the needs-you queue anyway. */
export function touchQueue(sid: string, nowMs: number = Date.now()): void {
  if (sid.length === 0) return;
  _mem.set(sid, nowMs);
  try {
    if (typeof localStorage !== "undefined") localStorage.setItem(storageKey(sid), String(nowMs));
  } catch {
    // Storage unavailable/full; the in-memory mirror still holds it for the life of the page.
  }
}

/** The last time this device opened the session in voice mode, in epoch ms, or 0 when never. */
export function queueTouchMs(sid: string): number {
  const mem = _mem.get(sid);
  if (mem !== undefined) return mem;
  try {
    if (typeof localStorage === "undefined") return 0;
    const raw = localStorage.getItem(storageKey(sid));
    if (raw === null) return 0;
    const parsed = Number(raw);
    if (!Number.isFinite(parsed)) return 0;
    _mem.set(sid, parsed);
    return parsed;
  } catch {
    return 0;
  }
}

// The queue position stamp: when this session last entered the line. The later of the Gateway's
// needsYouSince (it just started needing you) and the local touch (you just listened and moved on).
// A missing/unparseable needsYouSince with no touch sorts to the bottom - we cannot place it in the
// line, so it never jumps ahead of a session with a real wait time (same rule as inWaitingOrder).
function queuePositionMs(s: SessionDto): number {
  const raw = String(s.needsYouSince ?? "").trim();
  const since = raw.length > 0 ? Date.parse(raw) : Number.NaN;
  const sinceMs = Number.isNaN(since) ? Number.POSITIVE_INFINITY : since;
  const touched = queueTouchMs(s.sessionId ?? "");
  if (touched > 0 && touched !== Number.POSITIVE_INFINITY) {
    // A touch is a real, placeable stamp even when needsYouSince is missing.
    return sinceMs === Number.POSITIVE_INFINITY ? touched : Math.max(sinceMs, touched);
  }
  return sinceMs;
}

/** The voice queue's order: first-in-first-out by when each session last ENTERED the line - the
 *  Gateway's needsYouSince, or the device-local listened-touch when that is later. Oldest first, so
 *  the queue is worked top to bottom by ear; a listened-but-unhandled session drops to the bottom
 *  and comes around again. Ties break by createdAt then sessionId so equal stamps never jitter. */
export function inVoiceQueueOrder(sessions: SessionDto[]): SessionDto[] {
  return [...sessions].sort((a, b) => {
    const pa = queuePositionMs(a);
    const pb = queuePositionMs(b);
    if (pa !== pb) return pa - pb;
    const created = String(a.createdAt ?? "").localeCompare(String(b.createdAt ?? ""));
    if (created !== 0) return created;
    return String(a.sessionId ?? "").localeCompare(String(b.sessionId ?? ""));
  });
}

/** The auto-speak setting (device-local): when on, the roster's Voice tab jumps into the oldest
 *  waiting voice-ready session by itself and reads it aloud - the hands-free queue. */
const AUTO_SPEAK_KEY = "dt.voice.autospeak";

export function getAutoSpeak(): boolean {
  try {
    return typeof localStorage !== "undefined" && localStorage.getItem(AUTO_SPEAK_KEY) === "1";
  } catch {
    return false;
  }
}

export function setAutoSpeak(on: boolean): void {
  try {
    if (typeof localStorage === "undefined") return;
    if (on) localStorage.setItem(AUTO_SPEAK_KEY, "1");
    else localStorage.removeItem(AUTO_SPEAK_KEY);
  } catch {
    // Storage unavailable; the checkbox simply will not persist across reloads.
  }
}
