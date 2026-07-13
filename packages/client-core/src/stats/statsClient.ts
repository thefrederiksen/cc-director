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
  /** Plain-English caveats about what the numbers do and do not include. */
  notCaptured: string[];
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
    notCaptured?: unknown;
  } | null;
  const buckets = Array.isArray(body?.buckets) ? body!.buckets.map(normalizeBucket) : [];
  const hourlyTurns = Array.isArray(body?.hourlyTurns) ? body!.hourlyTurns.map(normalizeInputHour) : [];
  const notCaptured = Array.isArray(body?.notCaptured)
    ? body!.notCaptured.filter((x): x is string => typeof x === "string")
    : [];
  return {
    generatedAtUtc: typeof body?.generatedAtUtc === "string" ? body.generatedAtUtc : "",
    timeZone: safeTimeZone(typeof body?.timeZone === "string" ? body.timeZone : null),
    buckets,
    hourlyTurns,
    concurrency: normalizeConcurrency(body?.concurrency),
    notCaptured,
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
