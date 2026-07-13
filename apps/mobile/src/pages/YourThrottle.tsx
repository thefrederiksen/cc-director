import { useEffect, useState, type CSSProperties } from "react";
import { Link } from "react-router-dom";
import {
  getThrottle,
  summarizeThrottle,
  formatShare,
  SURFACE_LABEL,
  SURFACE_ORDER,
  type ThrottleData,
  type ThrottleSummary,
} from "@devthrottle/client-core/stats/statsClient";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";

// Your Throttle on the phone (devthrottle-stats mission): a compact dashboard of the MAIN stats, not the
// whole desktop page - many people drive the fleet mostly from their phone, so this is a clean, glanceable
// view of how they work. It reads the same GET /stats/data feed as the Cockpit, so the numbers match.
// Leads with the three headline rings (spoken, from phone, wingman voice mode), then where the turns come
// from, then the totals. The hourly charts, full breakdown, and caveats stay on the desktop page.
// Renders immediately with a loading state, shows an explicit error banner on failure (no-fallback rule),
// and auto-refreshes so the split moves live as the user drives by voice.

const REFRESH_MS = 10_000;

const RING_VOICE = "var(--accent)";
const RING_MOBILE = "#8b5cf6";
const RING_WINGMAN = "#14b8a6";

export function YourThrottle() {
  const [data, setData] = useState<ThrottleData | null>(null);
  const [error, setError] = useState<string | null>(null);

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

  const summary: ThrottleSummary | null = data === null ? null : summarizeThrottle(data);

  return (
    <div className="screen">
      <header className="app-bar">
        <Link className="back-link" to="/">
          Back
        </Link>
        <h1>Your Throttle</h1>
      </header>

      {error !== null && (
        <div className="banner banner-error" role="alert">
          {error}
        </div>
      )}

      {summary === null && error === null && <div className="thr-note">Loading your throttle...</div>}

      {summary !== null && !summary.hasData && (
        <div className="thr-note">
          No input counted yet. Send a turn from the phone, desktop, or cockpit and it will show up here.
        </div>
      )}

      {data !== null && summary !== null && summary.hasData && (
        <div className="mthr">
          <div className="mthr-metrics">
            <MetricRing
              title="Voice vs typing"
              share={summary.voiceShare}
              color={RING_VOICE}
              primary={{ label: "Voice", count: summary.voiceTurns }}
              secondary={{ label: "Typed", count: summary.typedTurns }}
            />
            <MetricRing
              title="Mobile vs desktop"
              share={summary.phoneShare}
              color={RING_MOBILE}
              primary={{ label: "Phone", count: summary.turnsBySurface.phone }}
              secondary={{ label: "Desk + Cockpit", count: summary.totalTurns - summary.turnsBySurface.phone }}
            />
            <MetricRing
              title="Wingman (voice mode)"
              share={summary.totalTurns > 0 ? data.wingman.turns / summary.totalTurns : null}
              color={RING_WINGMAN}
              primary={{ label: "Voice mode", count: data.wingman.turns }}
              secondary={{ label: "Hands-on", count: Math.max(0, summary.totalTurns - data.wingman.turns) }}
              note={`${data.wingman.sessions.toLocaleString()} session${data.wingman.sessions === 1 ? "" : "s"} used the wingman`}
            />
          </div>

          <SurfaceSplitBar summary={summary} />

          <div className="mthr-stats">
            <div className="mthr-stat">
              <div className="mthr-stat-value">{summary.totalTurns.toLocaleString()}</div>
              <div className="mthr-stat-label">Total turns</div>
            </div>
            <div className="mthr-stat">
              <div className="mthr-stat-value">{summary.totalCharacters.toLocaleString()}</div>
              <div className="mthr-stat-label">Characters</div>
            </div>
          </div>

          <p className="mthr-foot">
            The full hourly charts and breakdown live on Your Throttle in the desktop Cockpit.
          </p>
        </div>
      )}
    </div>
  );
}

// One metric as a compact card: a donut ring (the share) on the left, the title and the two named counts
// on the right. role="img" + aria-label so the number is announced; the counts carry identity so it is
// never color-alone.
function MetricRing({
  title,
  share,
  color,
  primary,
  secondary,
  note,
}: {
  title: string;
  share: number | null;
  color: string;
  primary: { label: string; count: number };
  secondary: { label: string; count: number };
  note?: string;
}) {
  const R = 42;
  const C = 2 * Math.PI * R;
  const filled = share === null ? 0 : share * C;
  const pctText = formatShare(share);

  return (
    <section className="mthr-metric">
      <div
        className="mthr-ring"
        role="img"
        aria-label={`${title}: ${primary.label} ${pctText} (${primary.count} of ${primary.count + secondary.count} turns)`}
      >
        <svg viewBox="0 0 100 100" className="mthr-ring-svg" style={{ ["--mthr-arc" as string]: color } as CSSProperties}>
          <circle className="mthr-ring-track" cx="50" cy="50" r={R} />
          <circle className="mthr-ring-arc" cx="50" cy="50" r={R} style={{ strokeDasharray: `${filled} ${C}` }} />
        </svg>
        <div className="mthr-ring-pct">{pctText}</div>
      </div>
      <div className="mthr-metric-body">
        <div className="mthr-metric-title">{title}</div>
        <div className="mthr-metric-legend">
          <span className="mthr-leg">
            <span className="mthr-dot" style={{ background: color }} />
            {primary.label} <b>{primary.count.toLocaleString()}</b>
          </span>
          <span className="mthr-leg">
            <span className="mthr-dot mthr-dot-muted" />
            {secondary.label} <b>{secondary.count.toLocaleString()}</b>
          </span>
        </div>
        {note !== undefined && <div className="mthr-metric-note">{note}</div>}
      </div>
    </section>
  );
}

// A single horizontal stacked bar of turns by surface (largest first, brightest first), plus a legend -
// the compact "where you drive from" the phone version keeps.
function SurfaceSplitBar({ summary }: { summary: ThrottleSummary }) {
  const total = summary.totalTurns;
  const segments = SURFACE_ORDER.map((s) => ({ surface: s, turns: summary.turnsBySurface[s] }))
    .filter((seg) => seg.turns > 0)
    .sort((a, b) => b.turns - a.turns);
  const fill = (i: number) => {
    const o = [100, 66, 42, 26][Math.min(i, 3)];
    return `color-mix(in srgb, var(--accent) ${o}%, transparent)`;
  };

  return (
    <section className="mthr-split" aria-label="Where you drive from">
      <div className="mthr-split-title">Where you drive from</div>
      <div className="mthr-split-bar">
        {segments.map((seg, i) => {
          const pct = total > 0 ? (seg.turns / total) * 100 : 0;
          return (
            <div
              key={seg.surface}
              className="mthr-split-seg"
              style={{ width: `${pct}%`, background: fill(i) }}
              title={`${SURFACE_LABEL[seg.surface]}: ${seg.turns} (${Math.round(pct)}%)`}
            />
          );
        })}
      </div>
      <div className="mthr-split-legend">
        {segments.map((seg, i) => (
          <span key={seg.surface} className="mthr-leg">
            <span className="mthr-dot" style={{ background: fill(i) }} />
            {SURFACE_LABEL[seg.surface]} <b>{seg.turns.toLocaleString()}</b>
          </span>
        ))}
      </div>
    </section>
  );
}
