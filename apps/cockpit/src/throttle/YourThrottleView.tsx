import { useEffect, useMemo, useState, type CSSProperties } from "react";
import {
  getThrottle,
  summarizeThrottle,
  formatShare,
  MODALITY_LABEL,
  SURFACE_LABEL,
  SURFACE_ORDER,
  type ThrottleData,
  type ThrottleSummary,
  type ConcurrencyHour,
  type InputHour,
  type Surface,
} from "@devthrottle/client-core/stats/statsClient";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";

// The "Your Throttle" page (devthrottle-stats mission): the in-Cockpit view of how the owner drives the
// fleet - spoken vs typed, and from phone vs desktop vs cockpit. Read-only over the same GET /stats/data
// feed the standalone Gateway page uses. Responsive (CodingStyle.md): renders immediately with a loading
// state, loads asynchronously, and on a load failure shows an explicit error banner (no-fallback rule).
// Auto-refreshes so the split visibly moves as the owner keeps driving.
//
// The page is TABBED so the two questions the owner actually cares about are not buried under supporting
// tables (owner ask, 2026-07-13): Overview leads with the two headline percentages as big rings - how
// much do I speak vs type, and how much do I drive from my phone; Activity holds the (now larger) time
// charts; Breakdown holds the supporting tables and honesty caveats.

const REFRESH_MS = 10_000;

type ThrottleTab = "overview" | "activity" | "breakdown";

const TABS: ReadonlyArray<{ key: ThrottleTab; label: string }> = [
  { key: "overview", label: "Overview" },
  { key: "activity", label: "Activity" },
  { key: "breakdown", label: "Breakdown" },
];

// The tab is sticky per browser so returning to the page lands on whatever the owner last read.
const TAB_STORAGE_KEY = "cockpit.throttleTab";

function initialTab(): ThrottleTab {
  try {
    const saved = window.localStorage.getItem(TAB_STORAGE_KEY);
    if (saved === "overview" || saved === "activity" || saved === "breakdown") return saved;
  } catch {
    /* storage unavailable (private mode) - fall through to the default */
  }
  return "overview";
}

export function YourThrottleView() {
  const [data, setData] = useState<ThrottleData | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [tab, setTabState] = useState<ThrottleTab>(initialTab);

  const setTab = (next: ThrottleTab) => {
    setTabState(next);
    try {
      window.localStorage.setItem(TAB_STORAGE_KEY, next);
    } catch {
      /* storage unavailable - the selection still applies this session */
    }
  };

  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;

    const tick = async () => {
      try {
        const fresh = await getThrottle(controller.signal);
        if (controller.signal.aborted) return;
        setData(fresh);
        setError(null);
      } catch (err) {
        if (controller.signal.aborted) return;
        setError(gatewayErrorMessage(err));
      } finally {
        if (!controller.signal.aborted) timer = setTimeout(() => void tick(), REFRESH_MS);
      }
    };
    void tick();

    return () => {
      controller.abort();
      if (timer !== undefined) clearTimeout(timer);
    };
  }, []);

  const summary: ThrottleSummary | null = useMemo(
    () => (data === null ? null : summarizeThrottle(data)),
    [data],
  );

  return (
    <div className="page thr">
      <div className="page-head">
        <h1>Your Throttle</h1>
        <p className="page-sub">
          How you are driving development: spoken vs typed, and from phone vs desktop vs cockpit. A turn
          is one submitted message; one spoken utterance and one typed message each count as one turn.
        </p>
      </div>

      {error !== null && (
        <div className="thr-banner" role="alert">
          {error}
        </div>
      )}

      {summary === null && error === null && <div className="thr-loading">Loading your throttle...</div>}

      {data !== null && summary !== null && (
        <>
          <div className="thr-tabs" role="tablist" aria-label="Your Throttle sections">
            {TABS.map((t) => (
              <button
                key={t.key}
                type="button"
                role="tab"
                aria-selected={tab === t.key}
                className={tab === t.key ? "thr-tab active" : "thr-tab"}
                onClick={() => setTab(t.key)}
              >
                {t.label}
              </button>
            ))}
          </div>

          {tab === "overview" && <OverviewTab summary={summary} data={data} />}
          {tab === "activity" && <ActivityTab data={data} />}
          {tab === "breakdown" && <BreakdownTab summary={summary} data={data} />}
        </>
      )}
    </div>
  );
}

// ---- Overview: the landing dashboard - the two headline percentages first, big and clear ------------

function OverviewTab({ summary, data }: { summary: ThrottleSummary; data: ThrottleData }) {
  if (!summary.hasData) {
    return (
      <div className="thr-empty">
        No input counted yet. Send a turn from the composer, dictation, phone, or cockpit and your
        throttle will appear here.
      </div>
    );
  }

  const peakLive = data.concurrency?.live.allTimeMax ?? 0;

  return (
    <>
      {/* The two hero rings: the questions the owner actually asks - how much do I speak, and how much do
          I drive from my phone. Each ring is its own metric (not two series in one chart), so each fills
          with its own accent against a muted remainder; the center number is the headline. */}
      <div className="thr-heroes">
        <HeroRing
          title="Voice vs typing"
          share={summary.voiceShare}
          accentVar="--thr-voice"
          centerCaption="spoken"
          primary={{ label: "Voice", count: summary.voiceTurns }}
          secondary={{ label: "Typed", count: summary.typedTurns }}
        />
        <HeroRing
          title="Mobile vs desktop"
          share={summary.phoneShare}
          accentVar="--thr-mobile"
          centerCaption="from phone"
          primary={{ label: "Phone", count: summary.turnsBySurface.phone }}
          secondary={{
            label: "Desktop + Cockpit",
            count: summary.totalTurns - summary.turnsBySurface.phone,
          }}
        />
      </div>

      {/* Where the turns come from, in one glance - the supporting detail behind the mobile ring. */}
      <div className="thr-panel">
        <div className="thr-panel-head">
          <h2>Where you drive from</h2>
          <span className="thr-panel-sub">{summary.totalTurns} turns across every surface</span>
        </div>
        <SurfaceSplitBar summary={summary} />
      </div>

      {/* Supporting headline numbers. */}
      <div className="thr-stats">
        <StatTile value={summary.totalTurns.toLocaleString()} label="Total turns" />
        <StatTile value={summary.totalCharacters.toLocaleString()} label="Characters driven" />
        <StatTile value={String(peakLive)} label="Peak sessions at once" sub="all-time" />
      </div>
    </>
  );
}

// A big donut ring: the share as an arc, the headline percent in the center, the two sides named with
// their counts below. role="img" + aria-label so the number is announced; the legend carries identity so
// it is never color-alone.
function HeroRing({
  title,
  share,
  accentVar,
  centerCaption,
  primary,
  secondary,
}: {
  title: string;
  share: number | null;
  accentVar: string;
  centerCaption: string;
  primary: { label: string; count: number };
  secondary: { label: string; count: number };
}) {
  const R = 42;
  const C = 2 * Math.PI * R;
  const filled = share === null ? 0 : share * C;
  const pctText = formatShare(share);

  return (
    <section className="thr-hero">
      <div className="thr-hero-title">{title}</div>
      <div
        className="thr-ring"
        role="img"
        aria-label={`${title}: ${primary.label} ${pctText} (${primary.count} of ${primary.count + secondary.count} turns)`}
      >
        <svg
          viewBox="0 0 100 100"
          className="thr-ring-svg"
          style={{ "--thr-arc": `var(${accentVar})` } as CSSProperties}
        >
          <circle className="thr-ring-track" cx="50" cy="50" r={R} />
          <circle
            className="thr-ring-arc"
            cx="50"
            cy="50"
            r={R}
            style={{ strokeDasharray: `${filled} ${C}` }}
          />
        </svg>
        <div className="thr-ring-center">
          <div className="thr-ring-pct">{pctText}</div>
          <div className="thr-ring-cap">{centerCaption}</div>
        </div>
      </div>
      <div className="thr-hero-legend">
        <span className="thr-hero-leg">
          <span className="thr-dot" style={{ background: `var(${accentVar})` }} />
          {primary.label}
          <b>{primary.count.toLocaleString()}</b>
        </span>
        <span className="thr-hero-leg">
          <span className="thr-dot thr-dot-muted" />
          {secondary.label}
          <b>{secondary.count.toLocaleString()}</b>
        </span>
      </div>
    </section>
  );
}

// A single horizontal stacked bar of turns by surface, each present segment labeled with its share, plus
// a legend beneath. One accent hue at descending opacity per surface (a magnitude ramp, not arbitrary
// categorical colors) so the bar reads as "one measure split by where".
function SurfaceSplitBar({ summary }: { summary: ThrottleSummary }) {
  const total = summary.totalTurns;
  // Largest surface first, so the brightest accent step lands on the surface you drive from most (the
  // magnitude ramp reads big -> small in both width and color).
  const segments = SURFACE_ORDER.map((s) => ({
    surface: s,
    turns: summary.turnsBySurface[s],
  }))
    .filter((seg) => seg.turns > 0)
    .sort((a, b) => b.turns - a.turns);

  return (
    <div className="thr-split">
      <div className="thr-split-bar" role="img" aria-label="Turns by surface">
        {segments.map((seg, i) => {
          const pct = total > 0 ? (seg.turns / total) * 100 : 0;
          return (
            <div
              key={seg.surface}
              className="thr-split-seg"
              style={{ width: `${pct}%`, background: surfaceFill(i) }}
              title={`${SURFACE_LABEL[seg.surface]}: ${seg.turns} turns (${Math.round(pct)}%)`}
            >
              {pct >= 8 && <span className="thr-split-lbl">{Math.round(pct)}%</span>}
            </div>
          );
        })}
      </div>
      <div className="thr-split-legend">
        {segments.map((seg, i) => (
          <span key={seg.surface} className="thr-split-leg">
            <span className="thr-dot" style={{ background: surfaceFill(i) }} />
            {SURFACE_LABEL[seg.surface]}
            <b>{seg.turns.toLocaleString()}</b>
          </span>
        ))}
      </div>
    </div>
  );
}

// The accent, stepped down in opacity for each successive surface segment - a one-hue magnitude ramp.
function surfaceFill(index: number): string {
  const opacities = [100, 66, 42, 26];
  const o = opacities[Math.min(index, opacities.length - 1)];
  return `color-mix(in srgb, var(--accent) ${o}%, transparent)`;
}

function StatTile({ value, label, sub }: { value: string; label: string; sub?: string }) {
  return (
    <div className="thr-stat">
      <div className="thr-stat-value">{value}</div>
      <div className="thr-stat-label">
        {label}
        {sub !== undefined && <span className="thr-stat-sub"> {sub}</span>}
      </div>
    </div>
  );
}

// ---- Activity: the time charts, now full-width and tall enough to read ------------------------------

function ActivityTab({ data }: { data: ThrottleData }) {
  const hasTurns = data.hourlyTurns.length > 0;
  const hasConcurrency = data.concurrency !== null && data.concurrency.hourly.length > 0;

  if (!hasTurns && !hasConcurrency) {
    return <div className="thr-empty">No hourly activity recorded in the last day yet.</div>;
  }

  return (
    <>
      {hasTurns && (
        <div className="thr-panel">
          <div className="thr-panel-head">
            <h2>Turns per hour (last 24h)</h2>
            <span className="thr-panel-sub">
              Your working day: how many turns you submitted each hour (UTC), voice over typed. Empty
              hours are when you were away.
            </span>
          </div>
          <TurnsPerHourChart hourly={data.hourlyTurns} />
        </div>
      )}

      {hasConcurrency && (
        <div className="thr-panel">
          <div className="thr-panel-head">
            <h2>Sessions per hour (last 24h)</h2>
            <span className="thr-panel-sub">
              Peak concurrent sessions in each hour (UTC). The bar is loaded/running; the darker portion
              is actively working. Hover a bar for that hour&apos;s distinct sessions and machines.
            </span>
          </div>
          <ConcurrencyChart hourly={data.concurrency!.hourly} />
        </div>
      )}
    </>
  );
}

// A 24-hour bar chart of turns submitted per hour - the "working day" shape. Each bar is the total turns
// that hour, stacked voice (accent) over typed (muted). Pure CSS bars.
function TurnsPerHourChart({ hourly }: { hourly: InputHour[] }) {
  const recent = hourly.slice(-24);
  const peak = Math.max(1, ...recent.map((h) => h.turns));
  return (
    <>
      <div
        className="thr-chart thr-chart-tall"
        role="img"
        aria-label={`Turns submitted per hour for the last ${recent.length} hours`}
      >
        {recent.map((h, i) => {
          const totalPct = (h.turns / peak) * 100;
          const voicePortion = h.turns > 0 ? (h.voiceTurns / h.turns) * 100 : 0;
          const typedPortion = h.turns > 0 ? (h.typedTurns / h.turns) * 100 : 0;
          return (
            <div
              className="thr-bar-col"
              key={h.hour}
              title={`${h.hour}:00 UTC - ${h.turns} turns (${h.voiceTurns} voice, ${h.typedTurns} typed), ${h.characters.toLocaleString()} chars`}
            >
              <div className="thr-bar-track">
                <div className="thr-turns-bar" style={{ height: `${totalPct}%` }}>
                  <div className="thr-turns-typed" style={{ height: `${typedPortion}%` }} />
                  <div className="thr-turns-voice" style={{ height: `${voicePortion}%` }} />
                </div>
              </div>
              <div className="thr-bar-label">{i % 3 === 0 ? h.hour.slice(-2) : ""}</div>
            </div>
          );
        })}
      </div>
      <div className="thr-legend">
        <span className="thr-legend-item">
          <span className="thr-swatch thr-swatch-voice" /> Voice
        </span>
        <span className="thr-legend-item">
          <span className="thr-swatch thr-swatch-typed" /> Typed
        </span>
      </div>
    </>
  );
}

// A 24-hour bar chart of the hourly peak concurrent sessions. Each bar is the max loaded/running in that
// hour; the darker inner portion is the max actively-working. Pure CSS bars.
function ConcurrencyChart({ hourly }: { hourly: ConcurrencyHour[] }) {
  const recent = hourly.slice(-24);
  const peak = Math.max(1, ...recent.map((h) => h.maxLive));
  return (
    <>
      <div
        className="thr-chart thr-chart-tall"
        role="img"
        aria-label={`Peak concurrent sessions per hour for the last ${recent.length} hours`}
      >
        {recent.map((h, i) => {
          const livePct = (h.maxLive / peak) * 100;
          const workPct = h.maxLive > 0 ? (h.maxWorking / h.maxLive) * 100 : 0;
          const hourNum = h.hour.slice(-2);
          return (
            <div
              className="thr-bar-col"
              key={h.hour}
              title={`${h.hour}:00 UTC - ${h.maxLive} loaded, ${h.maxWorking} working, ${h.sessions} distinct sessions, ${h.machines} machine(s)`}
            >
              <div className="thr-bar-track">
                <div className="thr-bar-live" style={{ height: `${livePct}%` }}>
                  <div className="thr-bar-work" style={{ height: `${workPct}%` }} />
                </div>
              </div>
              <div className="thr-bar-label">{i % 3 === 0 ? hourNum : ""}</div>
            </div>
          );
        })}
      </div>
      <div className="thr-legend">
        <span className="thr-legend-item">
          <span className="thr-swatch thr-swatch-live" /> Loaded / running
        </span>
        <span className="thr-legend-item">
          <span className="thr-swatch thr-swatch-voice" /> Actively working
        </span>
      </div>
    </>
  );
}

// ---- Breakdown: the supporting tables + honesty caveats ---------------------------------------------

function BreakdownTab({ summary, data }: { summary: ThrottleSummary; data: ThrottleData }) {
  return (
    <>
      {data.concurrency !== null && (
        <div className="thr-panel">
          <div className="thr-panel-head">
            <h2>Fleet concurrency</h2>
            <span className="thr-panel-sub">
              How many sessions run at once across every machine. Loaded/running is the parallel capacity
              in flight; actively working is the subset whose agent is processing a turn this instant.
            </span>
          </div>
          <table className="thr-table">
            <thead>
              <tr>
                <th></th>
                <th className="thr-num">Now</th>
                <th className="thr-num">This week</th>
                <th className="thr-num">All-time</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td>Loaded / running</td>
                <td className="thr-num">{data.concurrency.live.current}</td>
                <td className="thr-num">{data.concurrency.live.weeklyMax}</td>
                <td className="thr-num">{data.concurrency.live.allTimeMax}</td>
              </tr>
              <tr>
                <td>Actively working</td>
                <td className="thr-num">{data.concurrency.working.current}</td>
                <td className="thr-num">{data.concurrency.working.weeklyMax}</td>
                <td className="thr-num">{data.concurrency.working.allTimeMax}</td>
              </tr>
            </tbody>
          </table>
        </div>
      )}

      {summary.hasData && (
        <>
          <div className="thr-panel">
            <div className="thr-panel-head">
              <h2>Turns by surface</h2>
            </div>
            <table className="thr-table">
              <thead>
                <tr>
                  <th>Surface</th>
                  <th className="thr-num">Turns</th>
                </tr>
              </thead>
              <tbody>
                {SURFACE_ORDER.map((s: Surface) => (
                  <tr key={s}>
                    <td>{SURFACE_LABEL[s]}</td>
                    <td className="thr-num">{summary.turnsBySurface[s]}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <div className="thr-panel">
            <div className="thr-panel-head">
              <h2>Full breakdown</h2>
            </div>
            <table className="thr-table">
              <thead>
                <tr>
                  <th>Modality</th>
                  <th>Surface</th>
                  <th className="thr-num">Turns</th>
                  <th className="thr-num">Characters</th>
                </tr>
              </thead>
              <tbody>
                {data.buckets.length === 0 ? (
                  <tr>
                    <td colSpan={4} className="thr-muted">
                      No buckets yet.
                    </td>
                  </tr>
                ) : (
                  data.buckets.map((b) => (
                    <tr key={`${b.modality}-${b.surface}`}>
                      <td>{MODALITY_LABEL[b.modality]}</td>
                      <td>{SURFACE_LABEL[b.surface]}</td>
                      <td className="thr-num">{b.turns}</td>
                      <td className="thr-num">{b.characters.toLocaleString()}</td>
                    </tr>
                  ))
                )}
              </tbody>
            </table>
          </div>
        </>
      )}

      {data.notCaptured.length > 0 && (
        <div className="thr-caveats">
          <h2>What these numbers do not include</h2>
          <ul>
            {data.notCaptured.map((c, i) => (
              <li key={i}>{c}</li>
            ))}
          </ul>
        </div>
      )}
    </>
  );
}
