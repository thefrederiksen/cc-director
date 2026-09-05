// The DevThrottle Stats "Your Throttle" surface: the typed, same-origin client both the Cockpit and the
// mobile app read to render the throttle dashboard natively.
//
// The Gateway serves the figure at GET /stats/data (see StatsPageEndpoint). EVERY COUNT OF TURNS ON IT
// COMES FROM ONE DEFINITION OVER THE SUBMISSION LEDGER (mission "Clean up Your Throttle", 2026-09-05,
// ruling R9): the Gateway computes the figure, states the window it covers and the definition it used,
// and discloses what it left out. This client is DUMB on purpose (CLAUDE.md rule 7): it normalizes the
// wire shape and does share arithmetic over the counts the Gateway sent. It never decides what a state
// means - a self-hosted Gateway answers with a sentence, and the pages render that sentence verbatim.
//
// No character volume anywhere here (ruling R16): the ledger carries none, and a number the page cannot
// vouch for is not shown with an apology attached. Read-only; every request is root-relative to the
// Gateway and carries the same Bearer via authHeaders().
import { authHeaders, GatewayError } from "../api/client";

/** How a unit of input was produced. */
export type Modality = "typed" | "voice";

/** Which surface the operator drove from. */
export type Surface = "desktop" | "cockpit" | "phone" | "unknown";

/** One (modality, surface) count of submitted turns, mirroring the Gateway ThrottleBucketDto. */
export interface ThrottleBucket {
  modality: Modality;
  surface: Surface;
  /** Submitted turns through this bucket (never synthesized from raw keystrokes). */
  turns: number;
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
 * yet, or when its statistics store has not published. */
export interface ConcurrencyStats {
  live: ConcurrencySeries;
  working: ConcurrencySeries;
  /** Per-hour activity log, oldest hour first. */
  hourly: ConcurrencyHour[];
}

/** One hour of the "working day" log: turns (total + by modality) submitted that UTC hour (hour key
 * "yyyy-MM-ddTHH"). */
export interface InputHour {
  hour: string;
  turns: number;
  voiceTurns: number;
  typedTurns: number;
}

/** One repository's count of submitted turns over the window, through the Gateway's session-history join.
 * Mirrors the Gateway ThrottleRepoDto. Counts only - never any message text. */
export interface RepoStat {
  /** Grouping key: the "owner/repo" name the session's checkout belongs to (e.g. "thefrederiksen/devthrottle"),
   * so worktrees and per-machine clones are one row; or the checkout's folder name when history holds a path
   * and no name. */
  repo: string;
  /** Display leaf of the key (e.g. "devthrottle"). */
  repoName: string;
  /** Submitted turns into this repo (voice + typed). */
  turns: number;
  voiceTurns: number;
  typedTurns: number;
  /** Distinct sessions the counted turns went into in this repo. */
  sessions: number;
  /** The checkout paths those sessions ran in, sorted. */
  checkouts: string[];
}

/** One agent CLI's count of submitted turns over the window. Mirrors the Gateway ThrottleAgentDto. */
export interface AgentStat {
  /** The agent token the ledger recorded ("ClaudeCode", "Codex", ...), or "" when none (the key). */
  agent: string;
  /** Display name of the agent, e.g. "Claude Code"; "(unknown)" when the session carried no agent. */
  agentName: string;
  /** Submitted turns you drove through this agent (voice + typed). */
  turns: number;
  voiceTurns: number;
  typedTurns: number;
  /** Distinct sessions the counted turns went into under this agent. */
  sessions: number;
  /** Turns OTHER sessions drove into the sessions running this agent (issue #1636) - not you. NOT part of
   *  `turns`, which stays your own driving: the two answer different questions and adding them would corrupt
   *  the voice share. */
  agentDrivenTurns: number;
}

/** The window the figure describes, as the Gateway stated it. */
export interface ThrottleWindow {
  /** Inclusive start (ISO 8601 UTC). */
  fromUtc: string;
  /** Exclusive end (ISO 8601 UTC). */
  toUtc: string;
  /** True when no window was asked for and the Gateway answered its default. */
  isDefault: boolean;
  /** The Gateway's own plain-English name for the window ("Last 30 days"). Rendered verbatim. */
  label: string;
}

/** What the submission ledger holds, so a page can say where the record begins. */
export interface ThrottleLedger {
  retentionDays: number;
  /** The oldest submission the ledger holds for this account (ISO 8601 UTC), or null when it holds none. */
  earliestUtc: string | null;
}

/** The population the definition left out, disclosed beside the share (rulings R7 and R17). */
export interface ThrottleExcluded {
  /** Every submission in the window with no input origin. */
  noInputOrigin: number;
  /** Of those, the ones another session sent into this one - the fleet driving itself. */
  agentDriven: number;
  /** Of those, text the product wrote itself (a seed prompt, a handover). Nobody's turn. */
  framework: number;
  /** The remainder: a submission of yours the product could not place on a surface. Outside every number. */
  unresolved: number;
}

/** THE FIGURE: every count of turns the pages show, from one definition over one substrate. Mirrors the
 * Gateway ThrottleFigureDto. */
export interface ThrottleFigure {
  /** The definition, verbatim, so a reader can check the numbers against the sentence. */
  definition: string;
  /** The unit of every share ("submitted turns"). */
  unit: string;
  window: ThrottleWindow;
  ledger: ThrottleLedger;
  /** Turns the definition counted. */
  turns: number;
  voiceTurns: number;
  typedTurns: number;
  /** Distinct sessions the counted turns went into. */
  sessions: number;
  buckets: ThrottleBucket[];
  /** Turns per UTC hour, oldest first, hours with none omitted. */
  hourlyTurns: InputHour[];
  /** Per-agent split, most-driven first. */
  agents: AgentStat[];
  /** Per-repository split, most-driven first. */
  repos: RepoStat[];
  /** Counted turns whose session has no repository on record - disclosed, never folded into a row. */
  reposUnattributedTurns: number;
  excluded: ThrottleExcluded;
  /** Turns the fleet drove into ITSELF over the window: one session prompting another. Beside your own
   *  turns, never inside them. The ratio of this to your own turns is your leverage. */
  agentDrivenTurns: number;
}

/** GET /stats/data when the Gateway serves the figure. */
export interface ThrottleServed {
  available: true;
  /** When the Gateway generated this snapshot (ISO 8601 UTC), or "" when absent. */
  generatedAtUtc: string;
  /** The display time zone (IANA id) the hourly charts render local clock hours in. */
  timeZone: string;
  throttle: ThrottleFigure;
  /** Fleet concurrency (both series), or null when nothing is tracked or the statistics store is not up. */
  concurrency: ConcurrencyStats | null;
  /** The Gateway's reason the statistics-store blocks are null, or null when they are served. */
  statisticsUnavailableReason: string | null;
  /** Plain-English caveats about what the numbers do and do not include. */
  notCaptured: string[];
}

/** GET /stats/data when this Gateway has no figure to show - a self-hosted Gateway (owner's ruling R1).
 * The Gateway says why in one sentence and the pages render it verbatim (ruling R6). */
export interface ThrottleUnavailable {
  available: false;
  reason: string;
}

/** The GET /stats/data body. */
export type ThrottleData = ThrottleServed | ThrottleUnavailable;

/** A derived, presentation-ready summary of the figure. Shares are fractions in [0,1], or null when
 * there are no counted turns (an honest empty state - never a fabricated 0%). */
export interface ThrottleSummary {
  totalTurns: number;
  voiceTurns: number;
  typedTurns: number;
  /** Voice share of TURNS, or null when no turns are counted. */
  voiceShare: number | null;
  /** Turns per surface. */
  turnsBySurface: Record<Surface, number>;
  /** Phone share of TURNS, or null when no turns are counted. */
  phoneShare: number | null;
  hasData: boolean;
}

const SURFACES: readonly Surface[] = ["desktop", "cockpit", "phone", "unknown"];

function normalizeBucket(raw: unknown): ThrottleBucket {
  const b = (raw ?? {}) as Partial<Record<keyof ThrottleBucket, unknown>>;
  const modality: Modality = String(b.modality).toLowerCase() === "voice" ? "voice" : "typed";
  const s = String(b.surface).toLowerCase();
  const surface: Surface = s === "desktop" || s === "cockpit" || s === "phone" ? s : "unknown";
  return { modality, surface, turns: num(b.turns) };
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

/** The optional window to ask the Gateway for. Absent, the Gateway answers its default and says so. */
export interface ThrottleWindowRequest {
  /** Inclusive start (ISO 8601 UTC). */
  fromUtc: string;
  /** Exclusive end (ISO 8601 UTC). */
  toUtc: string;
}

// GET /stats/data - the "Your Throttle" figure. Throws on transport failure so the page can show an explicit
// error banner (the no-fallback rule). A 200 carrying available=false is NOT a failure: it is the Gateway
// saying, in a sentence, that there is no figure here, and the page shows that sentence.
export async function getThrottle(signal?: AbortSignal, window?: ThrottleWindowRequest): Promise<ThrottleData> {
  const query = window === undefined
    ? ""
    : `?from=${encodeURIComponent(window.fromUtc)}&to=${encodeURIComponent(window.toUtc)}`;
  const res = await fetch(`/stats/data${query}`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "GET /stats/data");
  const body = (await res.json()) as {
    available?: unknown;
    reason?: unknown;
    generatedAtUtc?: unknown;
    timeZone?: unknown;
    throttle?: unknown;
    concurrency?: unknown;
    statisticsUnavailableReason?: unknown;
    notCaptured?: unknown;
  } | null;
  if (body?.available === false) {
    return { available: false, reason: typeof body.reason === "string" ? body.reason : "" };
  }
  const notCaptured = Array.isArray(body?.notCaptured)
    ? body!.notCaptured.filter((x): x is string => typeof x === "string")
    : [];
  return {
    available: true,
    generatedAtUtc: typeof body?.generatedAtUtc === "string" ? body.generatedAtUtc : "",
    timeZone: safeTimeZone(typeof body?.timeZone === "string" ? body.timeZone : null),
    throttle: normalizeFigure(body?.throttle),
    concurrency: normalizeConcurrency(body?.concurrency),
    statisticsUnavailableReason:
      typeof body?.statisticsUnavailableReason === "string" ? body.statisticsUnavailableReason : null,
    notCaptured,
  };
}

function normalizeFigure(raw: unknown): ThrottleFigure {
  const f = (raw ?? {}) as Partial<Record<keyof ThrottleFigure, unknown>>;
  const w = (f.window ?? {}) as Partial<Record<keyof ThrottleWindow, unknown>>;
  const l = (f.ledger ?? {}) as Partial<Record<keyof ThrottleLedger, unknown>>;
  const x = (f.excluded ?? {}) as Partial<Record<keyof ThrottleExcluded, unknown>>;
  return {
    definition: typeof f.definition === "string" ? f.definition : "",
    unit: typeof f.unit === "string" ? f.unit : "",
    window: {
      fromUtc: typeof w.fromUtc === "string" ? w.fromUtc : "",
      toUtc: typeof w.toUtc === "string" ? w.toUtc : "",
      isDefault: w.isDefault === true,
      label: typeof w.label === "string" ? w.label : "",
    },
    ledger: {
      retentionDays: num(l.retentionDays),
      earliestUtc: typeof l.earliestUtc === "string" ? l.earliestUtc : null,
    },
    turns: num(f.turns),
    voiceTurns: num(f.voiceTurns),
    typedTurns: num(f.typedTurns),
    sessions: num(f.sessions),
    buckets: Array.isArray(f.buckets) ? f.buckets.map(normalizeBucket) : [],
    hourlyTurns: Array.isArray(f.hourlyTurns) ? f.hourlyTurns.map(normalizeInputHour) : [],
    agents: Array.isArray(f.agents) ? f.agents.map(normalizeAgent) : [],
    repos: Array.isArray(f.repos) ? f.repos.map(normalizeRepo) : [],
    reposUnattributedTurns: num(f.reposUnattributedTurns),
    excluded: {
      noInputOrigin: num(x.noInputOrigin),
      agentDriven: num(x.agentDriven),
      framework: num(x.framework),
      unresolved: num(x.unresolved),
    },
    agentDrivenTurns: num(f.agentDrivenTurns),
  };
}

function normalizeAgent(raw: unknown): AgentStat {
  const a = (raw ?? {}) as Partial<Record<keyof AgentStat, unknown>>;
  return {
    agent: String(a.agent ?? ""),
    agentName: String(a.agentName ?? ""),
    turns: num(a.turns),
    voiceTurns: num(a.voiceTurns),
    typedTurns: num(a.typedTurns),
    sessions: num(a.sessions),
    agentDrivenTurns: num(a.agentDrivenTurns),
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

/** Derive the honest headline summary from the figure. Turn shares are over counted turns only; with zero
 * turns the shares are null so the caller renders an empty state rather than "0%". */
export function summarizeThrottle(figure: ThrottleFigure): ThrottleSummary {
  const turnsBySurface: Record<Surface, number> = { desktop: 0, cockpit: 0, phone: 0, unknown: 0 };
  let totalTurns = 0;
  let voiceTurns = 0;
  let typedTurns = 0;

  for (const b of figure.buckets) {
    totalTurns += b.turns;
    if (b.modality === "voice") voiceTurns += b.turns;
    else typedTurns += b.turns;
    turnsBySurface[b.surface] += b.turns;
  }

  const share = (part: number): number | null => (totalTurns > 0 ? part / totalTurns : null);

  return {
    totalTurns,
    voiceTurns,
    typedTurns,
    voiceShare: share(voiceTurns),
    turnsBySurface,
    phoneShare: share(turnsBySurface.phone),
    hasData: totalTurns > 0,
  };
}

/** A derived, presentation-ready summary of the per-repo split for the Repos page headline cards. */
export interface RepoSummary {
  /** How many repos have any counted turn. */
  repoCount: number;
  /** Total submitted turns across every repo. */
  totalTurns: number;
  /** Total distinct sessions across every repo (a session belongs to exactly one repo, so summing the
   * per-repo distinct counts is itself a distinct total). */
  totalSessions: number;
  /** Total voice-driven turns across every repo. */
  voiceTurns: number;
  /** The most-driven repo's share of all turns, or null when no turns are counted. */
  topShare: number | null;
  /** The most-driven repo's display name, or null when there is no data. */
  topRepoName: string | null;
  hasData: boolean;
}

/** The Agents-page headline summary: how much you drive each agent CLI. */
export interface AgentSummary {
  /** How many agents have any counted turn. */
  agentCount: number;
  /** Total submitted turns across every agent. */
  totalTurns: number;
  /** Total distinct sessions across every agent (a session drives exactly one agent, so summing the
   * per-agent distinct counts is itself a distinct total). */
  totalSessions: number;
  /** Total voice-driven turns across every agent. */
  voiceTurns: number;
  /** The most-driven agent's share of all turns, or null when no turns are counted. */
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

/** Derive the Agents-page headline summary from the per-agent split. Shares are null (never a fabricated
 * 0%) when there are no counted turns. */
export function summarizeAgents(agents: AgentStat[]): AgentSummary {
  let totalTurns = 0;
  let totalSessions = 0;
  let voiceTurns = 0;
  let agentDrivenTurns = 0;
  let top: AgentStat | null = null;

  for (const a of agents) {
    totalTurns += a.turns;
    totalSessions += a.sessions;
    voiceTurns += a.voiceTurns;
    agentDrivenTurns += a.agentDrivenTurns;
    if (top === null || a.turns > top.turns) top = a;
  }

  return {
    agentCount: agents.filter((a) => a.turns > 0).length,
    totalTurns,
    totalSessions,
    voiceTurns,
    topShare: totalTurns > 0 && top !== null ? top.turns / totalTurns : null,
    topAgentName: top !== null && top.turns > 0 ? top.agentName : null,
    agentDrivenTurns,
    leverage: totalTurns > 0 ? agentDrivenTurns / totalTurns : null,
    // Agent-driven turns alone are data worth showing: a fleet driving itself while the owner has driven
    // nothing this window is a real state, not an empty one.
    hasData: totalTurns > 0 || agentDrivenTurns > 0,
  };
}

/** Derive the Repos-page headline summary from the per-repo split. Shares are null (never a fabricated
 * 0%) when there are no counted turns. */
export function summarizeRepos(repos: RepoStat[]): RepoSummary {
  let totalTurns = 0;
  let totalSessions = 0;
  let voiceTurns = 0;
  let top: RepoStat | null = null;

  for (const r of repos) {
    totalTurns += r.turns;
    totalSessions += r.sessions;
    voiceTurns += r.voiceTurns;
    if (top === null || r.turns > top.turns) top = r;
  }

  return {
    repoCount: repos.length,
    totalTurns,
    totalSessions,
    voiceTurns,
    topShare: totalTurns > 0 && top !== null ? top.turns / totalTurns : null,
    topRepoName: top !== null ? top.repoName : null,
    hasData: totalTurns > 0,
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
  return { hour, turns: 0, voiceTurns: 0, typedTurns: 0 };
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
