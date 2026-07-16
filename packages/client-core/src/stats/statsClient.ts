// The DevThrottle Stats "Your Throttle" surface (devthrottle-stats mission, Phase 1 in-app port): the
// typed, same-origin client both the Cockpit and the mobile app read to render the throttle dashboard
// natively, instead of sending the user to the standalone Gateway /stats HTML page.
//
// The Gateway already serves the aggregated tally at GET /stats/data (see StatsPageEndpoint). This is
// the shared read client + the honest summary math (turn shares by modality and surface), so both
// shells present one identical, single-source-of-truth view. Read-only; every request is root-relative
// to the Gateway and carries the same Bearer via authHeaders().
import { authHeaders, GatewayError } from "../api/client";

/** How a unit of input was produced. */
export type Modality = "typed" | "voice";

/** Which surface the operator drove from. */
export type Surface = "desktop" | "cockpit" | "phone" | "unknown";

/** One (modality, surface) tally bucket, mirroring the Gateway InputStatBucketDto. */
export interface ThrottleBucket {
  modality: Modality;
  surface: Surface;
  /** Submitted turns through this bucket (never synthesized from raw keystrokes). */
  turns: number;
  /** Total character volume through this bucket. */
  characters: number;
}

/** One tracked concurrency dimension (live loaded/running, or actively working). */
export interface ConcurrencySeries {
  /** Most recently observed count. */
  current: number;
  /** Highest count ever observed. */
  allTimeMax: number;
  /** When the all-time peak was observed (ISO 8601 UTC), or null if never. */
  allTimeMaxAtUtc: string | null;
  /** Highest count in the last 7 days, derived from the hourly history. */
  weeklyMax: number;
}

/** One hour of the fleet activity log (hour key "yyyy-MM-ddTHH" UTC): the max concurrent live and
 * working counts, and how many distinct sessions, machines, and repositories ran that hour. */
export interface ConcurrencyHour {
  hour: string;
  maxLive: number;
  maxWorking: number;
  sessions: number;
  machines: number;
  repos: number;
}

/** Fleet concurrency: how many sessions are loaded/running at once (live) and how many are actively
 * working at once (working), plus the per-hour activity log. Null when the Gateway has not tracked any
 * yet. */
export interface ConcurrencyStats {
  live: ConcurrencySeries;
  working: ConcurrencySeries;
  /** Per-hour activity log, oldest hour first. */
  hourly: ConcurrencyHour[];
}

/** One hour of the "working day" log: turns (total + by modality) and characters submitted that UTC hour
 * (hour key "yyyy-MM-ddTHH"). */
export interface InputHour {
  hour: string;
  turns: number;
  voiceTurns: number;
  typedTurns: number;
  characters: number;
}

/** One repository's all-time input tally for the private Repos page: how much development landed in this
 * codebase, in submitted turns (total + voice/typed split), character volume, and distinct sessions.
 * Mirrors the Gateway RepoStatBucketDto. Counts only - never any message text. */
export interface RepoStat {
  /** Grouping key: the GitHub "owner/repo" slug the sessions' checkouts belong to
   * (e.g. "thefrederiksen/devthrottle"), so worktrees and per-machine clones are one row. Falls back to the
   * local working-directory path for a checkout with no github.com origin. */
  repo: string;
  /** Display leaf of the key: the repository name for a slug (e.g. "devthrottle"), or the folder name for a
   * fallback path. */
  repoName: string;
  /** Total submitted turns into this repo (voice + typed). */
  turns: number;
  /** Submitted turns driven by voice. */
  voiceTurns: number;
  /** Submitted turns driven by typing. */
  typedTurns: number;
  /** Total character volume of input into this repo. */
  characters: number;
  /** Distinct sessions that drove counted input into this repo. */
  sessions: number;
  /** Local checkout paths (worktrees, per-machine clones) whose turns rolled up into this repo, sorted.
   * Retained so the page can show which working directories the work came from. */
  checkouts: string[];
}

/** One agent CLI's all-time input tally for the private Agents page: how much development you drive
 * through this agent, in submitted turns (total + voice/typed split), character volume, and distinct
 * sessions. Mirrors the Gateway AgentStatBucketDto. Counts only - never any message text. */
export interface AgentStat {
  /** The agent token the Director reported ("ClaudeCode", "Codex", ...), or "" when none (the key). */
  agent: string;
  /** Display name of the agent, e.g. "Claude Code"; "(unknown)" when the session carried no agent. */
  agentName: string;
  /** Total submitted turns driven through this agent (voice + typed). */
  turns: number;
  /** Submitted turns driven by voice. */
  voiceTurns: number;
  /** Submitted turns driven by typing. */
  typedTurns: number;
  /** Total character volume of input driven through this agent. */
  characters: number;
  /** Distinct sessions that drove counted input through this agent. */
  sessions: number;
  /** Turns OTHER AGENTS drove into the sessions running this agent (issue #1636) - not the human.
   *  NOT part of `turns`, which stays the human's own driving: the two answer different questions and
   *  adding them would corrupt the voice share. Zero from a Gateway that predates the lane. */
  agentDrivenTurns: number;
  /** Character volume of the `agentDrivenTurns`. */
  agentDrivenCharacters: number;
}

/** The GET /stats/data body: the fleet-wide aggregated tally plus the honesty caveats. */
export interface ThrottleData {
  /** When the Gateway generated this snapshot (ISO 8601 UTC), or "" when absent. */
  generatedAtUtc: string;
  /** The display time zone (IANA id) the hourly charts render local clock hours in. Defaults to the
   *  Gateway machine's own zone; a browser zone fills in for an older Gateway that does not send one. */
  timeZone: string;
  buckets: ThrottleBucket[];
  /** Turns per UTC hour (the "working day" series), oldest hour first. */
  hourlyTurns: InputHour[];
  /** Fleet concurrency (both series), or null when nothing tracked yet. */
  concurrency: ConcurrencyStats | null;
  /** Wingman usage (a session "uses the wingman" when it has voice mode on). */
  wingman: WingmanUsage;
  /** Per-repository all-time tally, ranked most-driven first (the private Repos page). */
  repos: RepoStat[];
  /** Per-agent all-time tally, ranked most-driven first (the private Agents page). */
  agents: AgentStat[];
  /** When the per-agent tally started counting (ISO 8601 UTC), or "" when it has never been stamped.
   *  The other series predate it, so the Agents page states this window instead of implying the earlier
   *  turns ran under no agent. */
  agentsSinceUtc: string;
  /** Turns the fleet drove into ITSELF: one agent prompting another (issue #1636). Reported alongside the
   *  human tally, never inside it. The ratio of this to your own turns is your leverage - what the fleet
   *  did off the back of each turn you spent. Zero from a Gateway that predates the lane. */
  agentDrivenTurns: number;
  /** Character volume of the fleet's `agentDrivenTurns`. */
  agentDrivenCharacters: number;
  /** Plain-English caveats about what the numbers do and do not include. */
  notCaptured: string[];
}

/** All-time wingman usage: turns submitted while a session had voice mode on, and the count of distinct
 *  sessions ever seen with voice mode on. */
export interface WingmanUsage {
  turns: number;
  sessions: number;
}

/** A derived, presentation-ready summary of the tally. Shares are fractions in [0,1], or null when
 * there are no counted turns yet (an honest empty state - never a fabricated 0%). */
export interface ThrottleSummary {
  totalTurns: number;
  totalCharacters: number;
  voiceTurns: number;
  typedTurns: number;
  /** Voice share of TURNS, or null when no turns are counted yet. */
  voiceShare: number | null;
  /** Turns per surface. */
  turnsBySurface: Record<Surface, number>;
  /** Phone share of TURNS, or null when no turns are counted yet. */
  phoneShare: number | null;
  hasData: boolean;
}

const SURFACES: readonly Surface[] = ["desktop", "cockpit", "phone", "unknown"];

function normalizeBucket(raw: unknown): ThrottleBucket {
  const b = (raw ?? {}) as Partial<Record<keyof ThrottleBucket, unknown>>;
  const modality: Modality = String(b.modality).toLowerCase() === "voice" ? "voice" : "typed";
  const s = String(b.surface).toLowerCase();
  const surface: Surface = s === "desktop" || s === "cockpit" || s === "phone" ? s : "unknown";
  const turns = Number(b.turns);
  const characters = Number(b.characters);
  return {
    modality,
    surface,
    turns: Number.isFinite(turns) ? turns : 0,
    characters: Number.isFinite(characters) ? characters : 0,
  };
}

async function gatewayErrorFrom(res: Response, label: string): Promise<GatewayError> {
  let detail = `${res.status}`;
  try {
    const text = await res.text();
    if (text.length > 0) {
      try {
        const body = JSON.parse(text) as { error?: string; detail?: string };
        detail = body.error ?? body.detail ?? text;
      } catch {
        detail = text;
      }
    }
  } catch {
    /* body unreadable - keep the status code */
  }
  return new GatewayError(res.status, `${label} failed: ${detail}`);
}

// GET /stats/data - the fleet-wide "Your Throttle" tally. Throws on transport failure so the page can
// show an explicit error banner (the no-fallback rule).
export async function getThrottle(signal?: AbortSignal): Promise<ThrottleData> {
  const res = await fetch("/stats/data", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "GET /stats/data");
  const body = (await res.json()) as {
    generatedAtUtc?: unknown;
    timeZone?: unknown;
    buckets?: unknown;
    hourlyTurns?: unknown;
    concurrency?: unknown;
    wingman?: unknown;
    repos?: unknown;
    agents?: unknown;
    agentsSinceUtc?: unknown;
    agentDrivenTurns?: unknown;
    agentDrivenCharacters?: unknown;
    notCaptured?: unknown;
  } | null;
  const buckets = Array.isArray(body?.buckets) ? body!.buckets.map(normalizeBucket) : [];
  const hourlyTurns = Array.isArray(body?.hourlyTurns) ? body!.hourlyTurns.map(normalizeInputHour) : [];
  const repos = Array.isArray(body?.repos) ? body!.repos.map(normalizeRepo) : [];
  const agents = Array.isArray(body?.agents) ? body!.agents.map(normalizeAgent) : [];
  const notCaptured = Array.isArray(body?.notCaptured)
    ? body!.notCaptured.filter((x): x is string => typeof x === "string")
    : [];
  return {
    generatedAtUtc: typeof body?.generatedAtUtc === "string" ? body.generatedAtUtc : "",
    timeZone: safeTimeZone(typeof body?.timeZone === "string" ? body.timeZone : null),
    buckets,
    hourlyTurns,
    concurrency: normalizeConcurrency(body?.concurrency),
    wingman: normalizeWingman(body?.wingman),
    repos,
    agents,
    agentsSinceUtc: typeof body?.agentsSinceUtc === "string" ? body.agentsSinceUtc : "",
    agentDrivenTurns: num(body?.agentDrivenTurns),
    agentDrivenCharacters: num(body?.agentDrivenCharacters),
    notCaptured,
  };
}

function normalizeWingman(raw: unknown): WingmanUsage {
  const w = (raw ?? {}) as { turns?: unknown; sessions?: unknown };
  return { turns: num(w.turns), sessions: num(w.sessions) };
}

function normalizeAgent(raw: unknown): AgentStat {
  const a = (raw ?? {}) as Partial<Record<keyof AgentStat, unknown>>;
  return {
    agent: String(a.agent ?? ""),
    agentName: String(a.agentName ?? ""),
    turns: num(a.turns),
    voiceTurns: num(a.voiceTurns),
    typedTurns: num(a.typedTurns),
    characters: num(a.characters),
    sessions: num(a.sessions),
    agentDrivenTurns: num(a.agentDrivenTurns),
    agentDrivenCharacters: num(a.agentDrivenCharacters),
  };
}

function normalizeRepo(raw: unknown): RepoStat {
  const r = (raw ?? {}) as Partial<Record<keyof RepoStat, unknown>>;
  return {
    repo: String(r.repo ?? ""),
    repoName: String(r.repoName ?? ""),
    turns: num(r.turns),
    voiceTurns: num(r.voiceTurns),
    typedTurns: num(r.typedTurns),
    characters: num(r.characters),
    sessions: num(r.sessions),
    checkouts: Array.isArray(r.checkouts) ? r.checkouts.map((c) => String(c)) : [],
  };
}

function normalizeInputHour(raw: unknown): InputHour {
  const h = (raw ?? {}) as Partial<Record<keyof InputHour, unknown>>;
  return {
    hour: String(h.hour ?? ""),
    turns: num(h.turns),
    voiceTurns: num(h.voiceTurns),
    typedTurns: num(h.typedTurns),
    characters: num(h.characters),
  };
}

function num(v: unknown): number {
  const n = Number(v);
  return Number.isFinite(n) ? n : 0;
}

function normalizeSeries(raw: unknown): ConcurrencySeries {
  const s = (raw ?? {}) as Partial<Record<keyof ConcurrencySeries, unknown>>;
  return {
    current: num(s.current),
    allTimeMax: num(s.allTimeMax),
    allTimeMaxAtUtc: typeof s.allTimeMaxAtUtc === "string" ? s.allTimeMaxAtUtc : null,
    weeklyMax: num(s.weeklyMax),
  };
}

function normalizeHour(raw: unknown): ConcurrencyHour {
  const h = (raw ?? {}) as Partial<Record<keyof ConcurrencyHour, unknown>>;
  return {
    hour: String(h.hour ?? ""),
    maxLive: num(h.maxLive),
    maxWorking: num(h.maxWorking),
    sessions: num(h.sessions),
    machines: num(h.machines),
    repos: num(h.repos),
  };
}

function normalizeConcurrency(raw: unknown): ConcurrencyStats | null {
  if (raw === null || typeof raw !== "object") return null;
  const c = raw as { live?: unknown; working?: unknown; hourly?: unknown };
  return {
    live: normalizeSeries(c.live),
    working: normalizeSeries(c.working),
    hourly: Array.isArray(c.hourly) ? c.hourly.map(normalizeHour) : [],
  };
}

/** Derive the honest headline summary from a tally snapshot. Turn shares are over counted turns only;
 * with zero turns the shares are null so the caller renders an empty state rather than "0%". */
export function summarizeThrottle(data: ThrottleData): ThrottleSummary {
  const turnsBySurface: Record<Surface, number> = { desktop: 0, cockpit: 0, phone: 0, unknown: 0 };
  let totalTurns = 0;
  let totalCharacters = 0;
  let voiceTurns = 0;
  let typedTurns = 0;

  for (const b of data.buckets) {
    totalTurns += b.turns;
    totalCharacters += b.characters;
    if (b.modality === "voice") voiceTurns += b.turns;
    else typedTurns += b.turns;
    turnsBySurface[b.surface] += b.turns;
  }

  const share = (part: number): number | null => (totalTurns > 0 ? part / totalTurns : null);

  return {
    totalTurns,
    totalCharacters,
    voiceTurns,
    typedTurns,
    voiceShare: share(voiceTurns),
    turnsBySurface,
    phoneShare: share(turnsBySurface.phone),
    hasData: totalTurns > 0 || totalCharacters > 0,
  };
}

/** A derived, presentation-ready summary of the per-repo tally for the Repos page headline cards. */
export interface RepoSummary {
  /** How many repos have any counted input. */
  repoCount: number;
  /** Total submitted turns across every repo. */
  totalTurns: number;
  /** Total character volume across every repo. */
  totalCharacters: number;
  /** Total distinct sessions across every repo (a session belongs to exactly one repo, so summing the
   * per-repo distinct counts is itself a distinct total). */
  totalSessions: number;
  /** Total voice-driven turns across every repo. */
  voiceTurns: number;
  /** The most-driven repo's share of all turns, or null when no turns are counted yet. */
  topShare: number | null;
  /** The most-driven repo's display name, or null when there is no data. */
  topRepoName: string | null;
  hasData: boolean;
}

/** The Agents-page headline summary: how much you drive each agent CLI. */
export interface AgentSummary {
  /** How many agents have any counted input. */
  agentCount: number;
  /** Total submitted turns across every agent. */
  totalTurns: number;
  /** Total character volume across every agent. */
  totalCharacters: number;
  /** Total distinct sessions across every agent (a session drives exactly one agent, so summing the
   * per-agent distinct counts is itself a distinct total). */
  totalSessions: number;
  /** Total voice-driven turns across every agent. */
  voiceTurns: number;
  /** The most-driven agent's share of all turns, or null when no turns are counted yet. */
  topShare: number | null;
  /** The most-driven agent's display name, or null when there is no data. */
  topAgentName: string | null;
  /** Turns the fleet drove into itself - one agent prompting another (issue #1636). */
  agentDrivenTurns: number;
  /** Leverage: agent-driven turns per turn YOU drove. 3 means the fleet spent three turns off the back of
   *  each one of yours. Null when you have driven no turns - a ratio with nothing underneath it would be
   *  a fabricated number, not a big one. */
  leverage: number | null;
  hasData: boolean;
}

/** Derive the Agents-page headline summary from the per-agent tally. Shares are null (never a fabricated
 * 0%) when there are no counted turns yet - the tally starts when the breakdown shipped, so an empty
 * page is the honest state until the fleet has driven a turn under it. */
export function summarizeAgents(agents: AgentStat[]): AgentSummary {
  let totalTurns = 0;
  let totalCharacters = 0;
  let totalSessions = 0;
  let voiceTurns = 0;
  let agentDrivenTurns = 0;
  let top: AgentStat | null = null;

  for (const a of agents) {
    totalTurns += a.turns;
    totalCharacters += a.characters;
    totalSessions += a.sessions;
    voiceTurns += a.voiceTurns;
    agentDrivenTurns += a.agentDrivenTurns;
    if (top === null || a.turns > top.turns) top = a;
  }

  return {
    agentCount: agents.length,
    totalTurns,
    totalCharacters,
    totalSessions,
    voiceTurns,
    topShare: totalTurns > 0 && top !== null ? top.turns / totalTurns : null,
    topAgentName: top !== null ? top.agentName : null,
    agentDrivenTurns,
    leverage: totalTurns > 0 ? agentDrivenTurns / totalTurns : null,
    // Agent-driven turns alone are data worth showing: a fleet driving itself while the owner has driven
    // nothing this window is a real state, not an empty one.
    hasData: totalTurns > 0 || totalCharacters > 0 || agentDrivenTurns > 0,
  };
}

/** Derive the Repos-page headline summary from the per-repo tally. Shares are null (never a fabricated
 * 0%) when there are no counted turns yet. */
export function summarizeRepos(repos: RepoStat[]): RepoSummary {
  let totalTurns = 0;
  let totalCharacters = 0;
  let totalSessions = 0;
  let voiceTurns = 0;
  let top: RepoStat | null = null;

  for (const r of repos) {
    totalTurns += r.turns;
    totalCharacters += r.characters;
    totalSessions += r.sessions;
    voiceTurns += r.voiceTurns;
    if (top === null || r.turns > top.turns) top = r;
  }

  return {
    repoCount: repos.length,
    totalTurns,
    totalCharacters,
    totalSessions,
    voiceTurns,
    topShare: totalTurns > 0 && top !== null ? top.turns / totalTurns : null,
    topRepoName: top !== null ? top.repoName : null,
    hasData: totalTurns > 0 || totalCharacters > 0,
  };
}

/** Format a fraction share as a whole-number percent, or "n/a" when there is no data yet. ASCII only. */
export function formatShare(fraction: number | null): string {
  if (fraction === null) return "n/a";
  return `${Math.round(fraction * 100)}%`;
}

/** Human labels for the wire tokens (ASCII only). */
export const MODALITY_LABEL: Record<Modality, string> = { voice: "Voice", typed: "Typed" };
export const SURFACE_LABEL: Record<Surface, string> = {
  desktop: "Desktop",
  cockpit: "Cockpit",
  phone: "Phone",
  unknown: "Unknown",
};

/** The surfaces in a stable presentation order. */
export const SURFACE_ORDER = SURFACES;

// ---- Hourly-chart time window + time zone --------------------------------------------------------
//
// The two hourly series (turns, concurrency) only carry the hours that had activity, so slicing each to
// its own "last 24" produced two DIFFERENT windows that did not line up. Instead, both charts render one
// canonical window: the 24 consecutive UTC clock hours ending at "now", with absent hours zero-filled.
// Labels are then formatted in the configured display time zone, so the axis reads in local time.

function padHourKey(d: Date): string {
  const p = (n: number) => String(n).padStart(2, "0");
  return `${d.getUTCFullYear()}-${p(d.getUTCMonth() + 1)}-${p(d.getUTCDate())}T${p(d.getUTCHours())}`;
}

/** The 24 consecutive UTC hour keys ("yyyy-MM-ddTHH") ending at the hour containing `nowUtc`, oldest
 *  first. Both hourly charts render this SAME window so they line up exactly. */
export function last24HourKeys(nowUtc: Date): string[] {
  const topOfHour = Date.UTC(
    nowUtc.getUTCFullYear(),
    nowUtc.getUTCMonth(),
    nowUtc.getUTCDate(),
    nowUtc.getUTCHours(),
  );
  const keys: string[] = [];
  for (let i = 23; i >= 0; i--) keys.push(padHourKey(new Date(topOfHour - i * 3_600_000)));
  return keys;
}

/** Map an hourly series (keyed by UTC hour) onto a fixed window of hour keys, filling any absent hour with
 *  a zero entry, so a chart renders a continuous, aligned axis. */
export function windowSeries<T extends { hour: string }>(
  series: readonly T[],
  keys: readonly string[],
  zero: (hour: string) => T,
): T[] {
  const byHour = new Map(series.map((s) => [s.hour, s]));
  return keys.map((k) => byHour.get(k) ?? zero(k));
}

/** A zero-filled turn-hour for an absent hour in the window. */
export function emptyInputHour(hour: string): InputHour {
  return { hour, turns: 0, voiceTurns: 0, typedTurns: 0, characters: 0 };
}

/** A zero-filled concurrency-hour for an absent hour in the window. */
export function emptyConcurrencyHour(hour: string): ConcurrencyHour {
  return { hour, maxLive: 0, maxWorking: 0, sessions: 0, machines: 0, repos: 0 };
}

function canFormatZone(tz: string): boolean {
  try {
    new Intl.DateTimeFormat("en-US", { timeZone: tz });
    return true;
  } catch {
    return false;
  }
}

function browserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone ?? "";
  } catch {
    return "";
  }
}

/** A time zone id we can format with: the given id when usable, else the browser's zone, else "UTC" - so
 *  one bad setting never throws inside a render. */
export function safeTimeZone(timeZone: string | null | undefined): string {
  const candidate = (timeZone ?? "").trim();
  if (candidate.length > 0 && canFormatZone(candidate)) return candidate;
  const browser = browserTimeZone();
  if (browser.length > 0 && canFormatZone(browser)) return browser;
  return "UTC";
}

/** Format one UTC hour key ("yyyy-MM-ddTHH") as its 2-digit local hour ("00".."23") in the given IANA
 *  zone. Assumes `timeZone` is already a usable id (see {@link safeTimeZone}). */
export function localHourLabel(hourKeyUtc: string, timeZone: string): string {
  const d = new Date(`${hourKeyUtc}:00:00Z`);
  if (Number.isNaN(d.getTime())) return hourKeyUtc.slice(-2);
  return new Intl.DateTimeFormat("en-US", { hour: "2-digit", hourCycle: "h23", timeZone }).format(d);
}
