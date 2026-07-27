// The one place any web client turns a session's supervision facts into something to render
// (internal#625 Phase 2): when it started, how long it has been open, how much of that was spent
// waiting on the user, and how many turns the agent has completed. The Cockpit roster card and the
// mobile roster row both map over supervisionStats(), so the two surfaces cannot drift into saying
// the same fact two different ways.
//
// WHERE THE NUMBERS COME FROM. createdAt has always been on the wire. turnCount, waitingSince and
// cumulativeIdleSeconds are the Phase 1 Director facts: turns are counted at the activity flip
// (one flip to WaitingForInput == one turn), and the idle clock is the honest waiting-on-you time,
// not byte-silence. The Director pushes ABSOLUTE anchors and closed totals; the live movement
// happens here, from the caller's ticking clock, so labels move between roster polls without a
// refetch (the durationLabel precedent, issue #844).
//
// NULL MEANS UNKNOWN: an older Director sends no turnCount and no cumulativeIdleSeconds, and those
// stats are simply omitted - never rendered as zero. Zero from a live Director is a real answer
// ("no turn has finished yet") and IS rendered.
import type { SessionDto } from "../api/client";
import { durationFromMs } from "./waiting";

// The generated schema.ts does not carry the Phase 1 fields yet, so they are augmented here - the
// same way uncommittedCount is augmented in changes.ts.
type SessionWithSupervision = SessionDto & {
  turnCount?: number | string | null;
  waitingSince?: string | null;
  cumulativeIdleSeconds?: number | string | null;
};

export type SupervisionTone = "normal" | "warm" | "hot";

export interface SupervisionStat {
  key: string;
  value: string;
  tone: SupervisionTone;
  title: string;
}

// Idle thresholds: past an hour the number turns amber, past four hours red - so a neglected
// session LOOKS neglected on the card. Runtime turns amber at one day and red at two: the case
// that motivated the whole feature was a mission session nobody noticed for 55 hours.
const IDLE_WARM_SECONDS = 3600;
const IDLE_HOT_SECONDS = 4 * 3600;
const OPEN_WARM_MS = 24 * 3600 * 1000;
const OPEN_HOT_MS = 48 * 3600 * 1000;

const WEEKDAYS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
const MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

/** The agent's completed-turn count, or null when the owning Director is too old to report it. */
export function turnCount(session: SessionDto): number | null {
  const raw = (session as SessionWithSupervision).turnCount;
  if (raw === null || raw === undefined) return null;
  const n = Number(raw);
  if (!Number.isFinite(n) || n < 0) return null;
  return Math.floor(n);
}

/**
 * The LIVE total seconds this session has spent waiting on the user: the closed stretches the
 * Director summed, plus the stretch currently open (now minus waitingSince) when the session is
 * waiting right now. Null when the owning Director does not report the clock.
 */
export function totalIdleSeconds(session: SessionDto, now: number): number | null {
  const s = session as SessionWithSupervision;
  const raw = s.cumulativeIdleSeconds;
  if (raw === null || raw === undefined) return null;
  const closed = Number(raw);
  if (!Number.isFinite(closed) || closed < 0) return null;

  let open = 0;
  const sinceIso = (s.waitingSince ?? "").trim();
  if (sinceIso.length > 0) {
    const since = Date.parse(sinceIso);
    if (!Number.isNaN(since)) open = Math.max(0, (now - since) / 1000);
  }
  return closed + open;
}

// The session's start instant in epoch milliseconds, or null when absent, unparseable, or the
// impossible 0001-01-01 default - an impossible value renders as nothing, never as "open 2025y".
function createdAtMs(session: SessionDto): number | null {
  const iso = (session.createdAt ?? "").trim();
  if (iso.length === 0) return null;
  const ms = Date.parse(iso);
  if (Number.isNaN(ms)) return null;
  if (ms < Date.parse("2000-01-01T00:00:00Z")) return null;
  return ms;
}

// Wall-clock start in the viewer's local time: "09:14" earlier today, "Thu 18:02" within the past
// six days, "18 Jul" beyond that. Exported for tests.
export function startedValue(createdMs: number, now: number): string {
  const created = new Date(createdMs);
  const nowDate = new Date(now);
  const hhmm = `${String(created.getHours()).padStart(2, "0")}:${String(created.getMinutes()).padStart(2, "0")}`;

  const sameDay =
    created.getFullYear() === nowDate.getFullYear() &&
    created.getMonth() === nowDate.getMonth() &&
    created.getDate() === nowDate.getDate();
  if (sameDay) return hhmm;

  if (now - createdMs < 6 * 24 * 3600 * 1000) return `${WEEKDAYS[created.getDay()]} ${hhmm}`;
  return `${created.getDate()} ${MONTHS[created.getMonth()]}`;
}

function openTone(openMs: number): SupervisionTone {
  if (openMs >= OPEN_HOT_MS) return "hot";
  if (openMs >= OPEN_WARM_MS) return "warm";
  return "normal";
}

function idleTone(idleSeconds: number): SupervisionTone {
  if (idleSeconds >= IDLE_HOT_SECONDS) return "hot";
  if (idleSeconds >= IDLE_WARM_SECONDS) return "warm";
  return "normal";
}

/**
 * The card's supervision line: started / open / idle / turns, in that fixed order, each stat
 * omitted when its fact is unknown rather than rendered as a zero. Empty means the card shows no
 * line at all. `now` is the caller's ticking clock (the shared one-second clock), so open and
 * idle move live between roster polls.
 */
export function supervisionStats(session: SessionDto, now: number): SupervisionStat[] {
  const stats: SupervisionStat[] = [];

  const created = createdAtMs(session);
  if (created !== null) {
    const c = new Date(created);
    stats.push({
      key: "started",
      value: startedValue(created, now),
      tone: "normal",
      title: `Started ${c.getDate()} ${MONTHS[c.getMonth()]} ${c.getFullYear()}, ${String(c.getHours()).padStart(2, "0")}:${String(c.getMinutes()).padStart(2, "0")}`,
    });

    const openMs = Math.max(0, now - created);
    stats.push({
      key: "open",
      value: durationFromMs(openMs),
      tone: openTone(openMs),
      title: "How long this session has been open",
    });
  }

  const idle = totalIdleSeconds(session, now);
  if (idle !== null) {
    stats.push({
      key: "idle",
      value: durationFromMs(idle * 1000),
      tone: idleTone(idle),
      title: "Total time this session has spent waiting on you",
    });
  }

  const turns = turnCount(session);
  if (turns !== null) {
    stats.push({
      key: "turns",
      value: String(turns),
      tone: "normal",
      title: "Turns the agent has completed in this run of the session",
    });
  }

  return stats;
}
