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
  // The armed-snooze deadline (Gateway-owned snooze clock), so a client can render the hold time
  // ("wakes in 3h 48m"). Null when there is no running clock: not snoozed, or a deferred snooze that has
  // not landed yet. The generated schema does not carry it, so it is augmented here like the fields above.
  snoozeUntil?: string | null;
  // The session is flagged for deletion and awaiting the reaper - drives the "winding down" badge. A BADGE,
  // NEVER A COLOUR: the fold does not read it, so the dot keeps telling the truth about the work while this
  // rides beside it (mirrors the desktop rail's IsPendingDeletion). Optional; false for most sessions.
  pendingDeletion?: boolean | null;
  deletionReason?: string | null;
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

// One cc-director's group in the roster's "My order" view: the sessions of a single Director, under a
// "computer:port" header. sortOrder is the OWNING Director's per-Director drag order, so it only orders
// sessions WITHIN a Director - which is exactly why the roster groups by Director and sorts each group
// by inDesktopOrder, rather than trying to interleave sortOrders that mean different things on different
// machines.
export interface DirectorGroup {
  /** The owning Director's id - the group key, unique per running cc-director. */
  directorId: string;
  /** The machine the Director runs on (from the sessions' machineName), for the header. */
  machineName: string;
  /** The Director's Control API port, or "" when unknown - the header's disambiguator. */
  port: string;
  /** This Director's sessions, in desktop (drag) order. */
  sessions: SessionDto[];
}

// Group sessions into their owning cc-director, ordered for the roster's "My order" view: each group's
// sessions are in desktop order, and the groups themselves are ordered by machine name then port so the
// list is stable (sortOrder cannot order ACROSS Directors, since it is per-Director). `portByDirector`
// maps a directorId to its Control API port (from GET /directors); a missing entry yields an empty port
// and the header degrades to the bare machine name.
export function groupByDirector(
  sessions: SessionDto[],
  portByDirector: Map<string, string>,
): DirectorGroup[] {
  const byDirector = new Map<string, DirectorGroup>();
  for (const s of sessions) {
    const directorId = (s.directorId ?? "").trim();
    // A session with no directorId cannot be attributed to a cc-director; bucket it under a stable empty
    // key so it still shows (rather than vanishing) instead of guessing an owner.
    const group = byDirector.get(directorId);
    if (group) {
      group.sessions.push(s);
    } else {
      byDirector.set(directorId, {
        directorId,
        machineName: (s.machineName ?? "").trim(),
        port: portByDirector.get(directorId) ?? "",
        sessions: [s],
      });
    }
  }
  const groups = [...byDirector.values()];
  for (const g of groups) g.sessions = inDesktopOrder(g.sessions);
  groups.sort((a, b) => {
    const byMachine = a.machineName.localeCompare(b.machineName);
    if (byMachine !== 0) return byMachine;
    return a.port.localeCompare(b.port, undefined, { numeric: true });
  });
  return groups;
}

// The ONE rule for reading a Gateway-stamped presentation field: it is there, or we fail loudly. A
// client that cannot get a stamped answer must never guess one - a guessed colour is indistinguishable
// from a real one on screen, so it does not degrade gracefully, it lies quietly.
//
// Exported because not every stamped surface projects a SessionDto: /exes/list has its own narrower
// row type carrying the same stamped fields, and it must fail by the same rule with the same message
// rather than growing a second, drifting copy of this check.
export function requireGatewayField(value: string | null | undefined, field: string, sid: string | undefined): string {
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

// "wakes in 3h 48m" - how long until an armed snooze returns this session to needs-you, read from the
// Gateway-owned snooze clock (snoozeUntil). Returns null when there is no running clock: not snoozed, or a
// deferred snooze that has not landed (no deadline yet). The Director never owns the clock; the client only
// renders the countdown. The wording matches the desktop rail's HoldTimeLabel exactly, so the hold time
// reads identically on the rail, the Cockpit and the phone. `nowMs` is injectable for deterministic tests.
export function snoozeCountdown(s: SessionDto, nowMs: number = Date.now()): string | null {
  const raw = (s as GatewayStampedSession).snoozeUntil?.trim();
  if (!raw) return null;
  const until = Date.parse(raw);
  if (Number.isNaN(until)) return null;
  const remainingMs = until - nowMs;
  if (remainingMs <= 0) return "waking up";
  const totalMin = Math.floor(remainingMs / 60000);
  if (totalMin < 1) return "wakes in <1m";
  if (totalMin < 60) return `wakes in ${totalMin}m`;
  const h = Math.floor(totalMin / 60);
  const m = totalMin % 60;
  return m > 0 ? `wakes in ${h}h ${m}m` : `wakes in ${h}h`;
}

// True when this session is flagged for deletion and awaiting the reaper - drives the "winding down"
// badge. A BADGE, NEVER A COLOUR (owner's ruling): pending deletion says nothing about what the agent is
// DOING - a flagged session may still be working - so the dot keeps telling the truth and this rides
// beside it. Non-throwing (optional field); mirrors the desktop rail's IsPendingDeletion.
export function pendingDeletion(s: SessionDto): boolean {
  return Boolean((s as GatewayStampedSession).pendingDeletion);
}

// The human reason captured when a session was flagged for deletion (e.g. "jobs-auto: nothing to report"),
// or null when none was given - surfaced as the winding-down badge's tooltip.
export function deletionReason(s: SessionDto): string | null {
  const r = (s as GatewayStampedSession).deletionReason?.trim();
  return r ? r : null;
}

// True while the agent is actively running a turn - the "working" state. Blue is the authoritative
// working color (blue = agent working / a turn is in progress). Used to retire a now-stale Wingman
// voice cue: the roster play-triangle is shown only while a session is red / parked and is removed the
// instant it starts working again (you no longer want to hear the finished-turn narration).
//
// THE LAW (2026-07-14): a working session is BLUE, always - so blue IS working, and nothing else gets
// a vote. This used to open with `if (s.onHold) return false`, a client-side override that made a
// snoozed session report NOT working even while the Gateway said blue. That is the client re-deriving
// state, which is exactly what this module exists to prevent: the Gateway owns the fold, clients render
// it. The Gateway now applies the working check at the top of its own ladder, so a snoozed session that
// starts working arrives here already stamped blue and must be reported as working.
export function isWorking(s: SessionDto): boolean {
  return effectiveColor(s).toLowerCase() === "blue";
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

// Map an effective Gateway colour name to its dot hex. THE CANONICAL PALETTE: one name, one hex,
// every surface. These exact values are also the desktop rail's brushes (SessionViewModel.cs), so a
// session renders the same pixel on the phone, the Cockpit and the rail - which is the whole point of
// law 7 ("every device shows the same thing, always"). A name that resolves to two hexes, or two
// surfaces that resolve one name differently, IS the violation - comparing fold answers ("red" ===
// "red") cannot see it, so keep this table and the rail's in step by hand when either moves.
//
// This comment used to say the table "mirrors the m.js palette so the mobile roster's dots match the
// existing prototype and the desktop rail". Both halves were false: m.js does not exist anywhere in
// the repository, and the table disagreed with the rail on red (#F14C4C here vs #EF4444 there) and
// yellow (#F59E0B, which is amber-500, vs #EAB308). It named a deleted file as the source of truth
// for a claim that was not true. Every name below is now Tailwind-500 for its own ramp - the two
// strays were the two that disagreed - except `error`, which is deliberately red-700 so a crashed
// session reads darker than a needs-you red.
const COLORS: Record<string, string> = {
  red: "#EF4444", // needs you
  yellow: "#EAB308", // wingman narrating / preparing voice
  orange: "#F97316", // dictation in flight, or a deep dive running
  green: "#22C55E", // ready - brand-new, parked at its prompt with nothing needed
  blue: "#3B82F6", // working - always
  purple: "#A855F7", // parked on its own background task
  supporting: "#64748B", // issue #815: controlled sub-agent, recessive slate
  error: "#B91C1C", // issue #959: the agent process crashed - deep red, distinct from needs-you red
  // Parked: on hold or exited. ONE grey, on purpose. The Gateway folds both to "grey" and draws no
  // distinction between them, so no client may invent one: the snoozed/exited difference is lifecycle,
  // and lifecycle travels on the stamped label ("Snoozed" / "Exited") and a badge, never on the dot.
  // The desktop used to split this one name into two hexes by re-reading the raw OnHold flag - a
  // distinction the Gateway never made - and that re-derive is gone.
  grey: "#6B7280",
  unknown: "#6B7280", // indeterminate activity state (e.g. an unrecognized state) - rendered gray like grey
};

export function dotColor(color: string): string {
  const value = COLORS[color];
  if (!value) throw new Error(`Unknown Gateway effectiveColor '${color}'.`);
  return value;
}

// One short context line per row: the Gateway's stamped label, else the latest status reason.
// Never empty so every row reads cleanly.
//
// THE LAW (2026-07-14): a working session is BLUE and reads "Working". This used to open with
// `if (s.onHold) return "Snoozed"` followed by its own dictation/transcribing ladder - a THIRD fold
// of the same question, in a third place, in a different order from the Gateway's. That is how a row
// ended up with a blue dot and the word "Snoozed" beside it. The Gateway already stamps stateLabel
// from the same inputs as the dot, so the row simply renders it: one fold, one answer, every screen.
// It FAILS LOUDLY when the Gateway did not stamp a label, exactly like stateLabel() - it does not fall
// back to raw fields. An earlier version fell back to lastStatusReason / assessedState / activityState /
// status when stateLabel was missing, which reintroduced the whole problem in miniature: against a
// mixed-version Gateway the dot and bucket came from the Gateway while the WORDS came from a local guess,
// which is the "blue dot labelled Snoozed" class of bug this module exists to prevent. Both callers
// (the Cockpit roster and the mobile home list) render Gateway /sessions rows, so a missing stamp is a
// real defect and must be seen, not painted over.
export function contextLine(s: SessionDto): string {
  return stateLabel(s);
}

// The leaf repo name for a row's secondary label.
export function repoLeaf(s: SessionDto): string {
  const path = (s.repoPath ?? "").trim();
  if (!path) return "";
  const parts = path.split(/[\\/]/).filter(Boolean);
  return parts.length ? parts[parts.length - 1] : path;
}
