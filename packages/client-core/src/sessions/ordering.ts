// Client-side triage + ordering for the roster. A faithful port of the C# SessionOrdering
// policy (src/CcDirector.Gateway.Contracts/SessionOrdering.cs) so the mobile roster groups and
// orders sessions exactly like the desktop Cockpit Mobile page does. Kept as pure functions so
// it is unit-testable and shared by the Home page.
import type { SessionDto } from "../api/client";

export type TriageBucket = "needsYou" | "active" | "onHold";
type GatewayStampedSession = SessionDto & {
  effectiveColor?: string | null;
  triageBucket?: TriageBucket | string | null;
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

function isExplaining(s: SessionDto): boolean {
  return s.briefingState === "Explaining";
}

function isBriefing(s: SessionDto): boolean {
  return s.briefingState === "Briefing" && (s.statusColor ?? "").toLowerCase() === "red";
}

// Hold yellow ("preparing voice") only while the wingman is ACTIVELY generating this turn's
// spoken summary, so the roster does not flash red mid-generation; once generation ends the
// session shows its real color (red/needs-you, with the play triangle when audio is ready).
// This used to also hold yellow whenever audio was not yet ready (|| !voiceAudioReady), but a
// text-to-speech failure (a DevThrottle/DeepInfra 504 or timeout) produces no audio, which left
// the session stuck yellow/orange forever while it actually needed the user (the "stuck orange,
// says needs you" report, 2026-07-08). Gating on voiceGenerating alone gives a terminal exit.
function isVoicePreparing(s: SessionDto): boolean {
  if (!s.voiceMode) return false;
  if ((s.statusColor ?? "").toLowerCase() !== "red") return false;
  const state = s.assessedState ?? s.activityState ?? "";
  const waiting = state === "WaitingForInput" || state === "WaitingForPerm";
  if (!waiting) return false;
  return Boolean(s.voiceGenerating);
}

// The ONE effective status color every client renders and triages on (issue #196): on-hold
// greys out, a session whose dictated utterance is being transcribed in the background is orange,
// a user-requested explain is orange, the wingman reading a finished turn is yellow,
// a voice-mode session preparing audio is yellow - otherwise the raw Director status color.
export function effectiveColor(s: SessionDto): string {
  const stamped = (s as GatewayStampedSession).effectiveColor;
  if (stamped && stamped.trim().length > 0) return stamped;
  if (s.onHold) return "grey";
  if (s.transcribing) return "orange";
  if (isExplaining(s)) return "orange";
  if (isBriefing(s)) return "yellow";
  if (isVoicePreparing(s)) return "yellow";
  return s.statusColor ?? "unknown";
}

// True while the agent is actively running a turn - the "working" state. Blue is the authoritative
// working color (StatusColor.cs: blue = agent working / a turn is in progress); the raw activity /
// assessed state is checked too so a session mid-turn still counts before its color settles. A
// deferred (on-hold) session is never "working" - the user parked it. Used to retire a now-stale
// Wingman voice cue: the roster play-triangle is shown only while a session is red / parked and is
// removed the instant it starts working again (you no longer want to hear the finished-turn
// narration).
export function isWorking(s: SessionDto): boolean {
  if (s.onHold) return false;
  if ((s.statusColor ?? "").toLowerCase() === "blue") return true;
  const state = s.assessedState ?? s.activityState ?? "";
  return state === "Working";
}

// Classify a session for triage. On-hold takes precedence over color (the user deferred it);
// otherwise an effective-red session "needs you", everything else is active.
export function classify(s: SessionDto): TriageBucket {
  const stamped = (s as GatewayStampedSession).triageBucket;
  if (stamped === "needsYou" || stamped === "active" || stamped === "onHold") return stamped;
  if (s.onHold) return "onHold";
  return effectiveColor(s) === "red" ? "needsYou" : "active";
}

export function inBucket(sessions: SessionDto[], bucket: TriageBucket): SessionDto[] {
  return inDesktopOrder(sessions.filter((s) => classify(s) === bucket));
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
};

export function dotColor(color: string): string {
  return COLORS[color] ?? "#6B7280";
}

// One short context line per row: the on-hold note, else the latest status reason, else the
// activity state. Never empty so every row reads cleanly.
export function contextLine(s: SessionDto): string {
  if (s.onHold) return "On hold";
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
