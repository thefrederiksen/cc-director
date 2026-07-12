// Client-side ordering helpers for the roster. Presentation state is Gateway-owned: /sessions must
// provide effectiveColor and triageBucket. The browser shell is deliberately dumb and fails loudly
// if that contract is broken instead of reconstructing the Gateway state machine in TypeScript.
import type { SessionDto } from "../api/client";

export type TriageBucket = "needsYou" | "active" | "onHold";
type GatewayStampedSession = SessionDto & {
  effectiveColor?: string | null;
  stateLabel?: string | null;
  triageBucket?: TriageBucket | string | null;
  // Snooze Length mission: display-only marker that this session just returned from an EXPIRED snooze
  // (its Gateway-owned timer elapsed and the fold put it back into "needs you" on its own). The Gateway
  // stamps it; clients render a distinct "Snooze ended" badge. Optional (absent/false for most sessions).
  snoozeExpired?: boolean | null;
};

// The stable "desktop order": honor the owning Director's SortOrder (the user-controlled,
// drag-to-reorder, persisted order), then CreatedAt as a deterministic tie-break.
export function inDesktopOrder(sessions: SessionDto[]): SessionDto[] {
  return [...sessions].sort((a, b) => {
    // sortOrder is typed number|string in the generated schema (the serializer may emit a
    // numeric string); coerce so the comparison is always numeric.
    const so = Number(a.sortOrder ?? 0) - Number(b.sortOrder ?? 0);
    if (so !== 0) return so;
    return String(a.createdAt ?? "").localeCompare(String(b.createdAt ?? ""));
  });
}

function requireGatewayField(value: string | null | undefined, field: string, sid: string | undefined): string {
  const text = value?.trim();
  if (!text) {
    throw new Error(`Gateway /sessions missing ${field} for session ${sid ?? "(unknown)"}. Redeploy Gateway and mobile together.`);
  }
  return text;
}

// The ONE effective status color every client renders and triages on. It is stamped by the Gateway
// after folding on-hold, transcribing, explaining, briefing, and voice-generation state.
export function effectiveColor(s: SessionDto): string {
  return requireGatewayField((s as GatewayStampedSession).effectiveColor, "effectiveColor", s.sessionId);
}

// The ONE human-readable state label every client renders, stamped by the Gateway from the same fold
// as effectiveColor (so the dot color and its label never disagree). Clients render this instead of
// re-deriving a label from the raw color or activity state.
export function stateLabel(s: SessionDto): string {
  return requireGatewayField((s as GatewayStampedSession).stateLabel, "stateLabel", s.sessionId);
}

// Snooze Length mission: true when this session just RETURNED from an expired snooze (its Gateway-owned
// timer fired and put it back into "needs you" on its own). Clients render a distinct "Snooze ended"
// badge so the owner knows it is a "go investigate why it went quiet" item, not a fresh turn-end.
// Non-throwing (optional field): a session without the marker is simply not returned-from-snooze.
export function snoozeExpired(s: SessionDto): boolean {
  return Boolean((s as GatewayStampedSession).snoozeExpired);
}

// True while the agent is actively running a turn - the "working" state. Blue is the authoritative
// working color (blue = agent working / a turn is in progress); the raw activity / assessed state is
// checked too so a session mid-turn still counts before its color settles. A deferred (on-hold) session
// is never "working" - the user parked it. Used to retire a now-stale Wingman voice cue: the roster
// play-triangle is shown only while a session is red / parked and is removed the instant it starts
// working again (you no longer want to hear the finished-turn narration). Issue #1177 (Phase 2.3): reads
// the Gateway-owned effectiveColor, not the raw Director statusColor, so the client never re-derives.
export function isWorking(s: SessionDto): boolean {
  if (s.onHold) return false;
  if (effectiveColor(s).toLowerCase() === "blue") return true;
  const state = s.assessedState ?? s.activityState ?? "";
  return state === "Working";
}

// Classify a session for triage. The Gateway owns this fold; the client consumes the stamped bucket.
export function classify(s: SessionDto): TriageBucket {
  const stamped = requireGatewayField((s as GatewayStampedSession).triageBucket, "triageBucket", s.sessionId);
  if (stamped === "needsYou" || stamped === "active" || stamped === "onHold") return stamped;
  throw new Error(`Gateway /sessions returned invalid triageBucket '${stamped}' for session ${s.sessionId ?? "(unknown)"}.`);
}

export function inBucket(sessions: SessionDto[], bucket: TriageBucket): SessionDto[] {
  return inDesktopOrder(sessions.filter((s) => classify(s) === bucket));
}

// The "waiting line" order for the needs-you group (the mobile roster's top group). The session that
// has been asking for you the LONGEST sits at the top; a session that only just started needing you
// drops in at the BOTTOM. This keeps the list from reshuffling under you as new work arrives and
// makes it a natural first-in, first-handled queue when you work from the top down. Ordered by
// needsYouSince ascending - the earliest stamp is the oldest wait. A session with no parseable stamp
// sorts to the bottom, and createdAt then sessionId break ties so equal waits never jitter between
// polls. This intentionally ignores the drag-to-reorder desktop SortOrder: the needs-you group is a
// queue by wait time, not the user's manual arrangement.
export function inWaitingOrder(sessions: SessionDto[]): SessionDto[] {
  return sessions
    .filter((s) => classify(s) === "needsYou")
    .sort((a, b) => {
      const wa = waitingSinceMs(a);
      const wb = waitingSinceMs(b);
      if (wa !== wb) return wa - wb;
      const created = String(a.createdAt ?? "").localeCompare(String(b.createdAt ?? ""));
      if (created !== 0) return created;
      return String(a.sessionId ?? "").localeCompare(String(b.sessionId ?? ""));
    });
}

// The needsYouSince stamp parsed to epoch milliseconds for the waiting-line sort. A missing or
// unparseable stamp returns positive infinity so that session sorts to the bottom of the queue
// (we cannot place it in the line, so it never jumps ahead of a session with a real wait time).
function waitingSinceMs(s: SessionDto): number {
  const raw = String(s.needsYouSince ?? "").trim();
  if (raw.length === 0) return Number.POSITIVE_INFINITY;
  const parsed = Date.parse(raw);
  return Number.isNaN(parsed) ? Number.POSITIVE_INFINITY : parsed;
}

// Map an effective color to its dot hex. Mirrors the m.js palette so the mobile roster's dots
// match the existing prototype and the desktop rail.
const COLORS: Record<string, string> = {
  red: "#F14C4C",
  yellow: "#F59E0B",
  orange: "#F97316",
  green: "#22C55E",
  blue: "#3B82F6",
  purple: "#A855F7",
  supporting: "#64748B", // issue #815: controlled sub-agent, recessive slate
  error: "#B91C1C", // issue #959: the agent process crashed - deep red, distinct from needs-you red
  grey: "#6B7280",
  unknown: "#6B7280", // indeterminate activity state (e.g. an unrecognized state) - rendered gray like grey
};

export function dotColor(color: string): string {
  const value = COLORS[color];
  if (!value) throw new Error(`Unknown Gateway effectiveColor '${color}'.`);
  return value;
}

// One short context line per row: the on-hold note, else the latest status reason, else the
// activity state. Never empty so every row reads cleanly.
export function contextLine(s: SessionDto): string {
  if (s.onHold) return "Snoozed";
  // Issue #1181, Task 4: the honest phase - "Uploading from phone" while the phone is still sending the
  // audio up, "Transcribing" while the server turns it into text. Falls back to the old blanket flag.
  if (s.dictationStatus) return s.dictationStatus;
  if (s.transcribing) return "Transcribing...";
  if (s.lastStatusReason) return s.lastStatusReason;
  return s.assessedState ?? s.activityState ?? s.status ?? "";
}

// The leaf repo name for a row's secondary label.
export function repoLeaf(s: SessionDto): string {
  const path = (s.repoPath ?? "").trim();
  if (!path) return "";
  const parts = path.split(/[\\/]/).filter(Boolean);
  return parts.length ? parts[parts.length - 1] : path;
}
