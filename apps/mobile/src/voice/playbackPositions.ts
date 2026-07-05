// Per-session audio playback position memory (issue #1003).
//
// Remembers how far into the current clip each session's listener has got, so flipping between
// sessions and coming back resumes exactly where you left off - the positions never bleed across
// sessions because every mark is keyed by session id. The stored generatedAt guards against
// restoring a stale position onto a newer clip (a new turn's narration starts from the beginning).
//
// localStorage is the durable backing so a mark also survives a reload; a small in-memory mirror
// avoids re-parsing on every render. The localStorage access is wrapped only to detect the rare
// contexts where it is unavailable (private-mode quota, disabled storage) - capability detection in
// the same spirit as voice/clips.ts's Cache Storage layer, NOT an error-hiding fallback: when it is
// absent the in-memory map still drives resume for the life of the page.

export interface PlaybackMark {
  /** The clip (its generatedAt stamp) this position belongs to. */
  generatedAt: string;
  /** Seconds listened into that clip. */
  pos: number;
  /** Clip duration in seconds (0 until metadata is known). */
  dur: number;
  /** Whether this clip has already auto-played once on this device (so returning does not restart it). */
  autoPlayed: boolean;
}

const KEY_PREFIX = "dt.voice.pos.";
const _mem = new Map<string, PlaybackMark>();

function storageKey(sid: string): string {
  return KEY_PREFIX + sid;
}

function readStore(sid: string): PlaybackMark | null {
  try {
    if (typeof localStorage === "undefined") return null;
    const raw = localStorage.getItem(storageKey(sid));
    if (raw === null) return null;
    return JSON.parse(raw) as PlaybackMark;
  } catch {
    // Storage unavailable/unreadable in this context; the in-memory mirror still serves this page.
    return null;
  }
}

function writeStore(sid: string, mark: PlaybackMark): void {
  try {
    if (typeof localStorage === "undefined") return;
    localStorage.setItem(storageKey(sid), JSON.stringify(mark));
  } catch {
    // Storage unavailable/full; the in-memory mirror still holds it for this page.
  }
}

/** The remembered mark for a session (in-memory first, then durable store), or null when none. */
export function getMark(sid: string): PlaybackMark | null {
  const mem = _mem.get(sid);
  if (mem !== undefined) return mem;
  const stored = readStore(sid);
  if (stored !== null) _mem.set(sid, stored);
  return stored;
}

/** Persist how far this session has listened (in memory and in the durable store). */
export function saveMark(sid: string, mark: PlaybackMark): void {
  _mem.set(sid, mark);
  writeStore(sid, mark);
}

/** The saved position for a specific clip, or 0 when there is no mark or it belongs to another clip. */
export function positionFor(sid: string, generatedAt: string): number {
  const m = getMark(sid);
  return m !== null && m.generatedAt === generatedAt ? m.pos : 0;
}

/** True once this exact clip has auto-played on this device, so returning to it will not restart it. */
export function wasAutoPlayed(sid: string, generatedAt: string): boolean {
  const m = getMark(sid);
  return m !== null && m.generatedAt === generatedAt && m.autoPlayed;
}
