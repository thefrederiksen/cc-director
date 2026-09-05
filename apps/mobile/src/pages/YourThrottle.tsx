import { useEffect, useMemo, useState, type CSSProperties } from "react";
import { Link, useSearchParams } from "react-router-dom";
import {
  getThrottle,
  summarizeThrottle,
  formatShare,
  safeTimeZone,
  throttleWindowFromSearch,
  SURFACE_LABEL,
  SURFACE_ORDER,
  type ThrottleData,
  type ThrottleFigure,
  type ThrottleSummary,
} from "@devthrottle/client-core/stats/statsClient";
import { ThrottleWindowSelector } from "@devthrottle/client-core/stats/ThrottleWindowSelector";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";

// Your Throttle on the phone: a compact dashboard of the MAIN stats, not the whole desktop page - many
// people drive the fleet mostly from their phone, so this is a clean, glanceable view of how they work. It
// reads the same GET /stats/data feed as the Cockpit, so the numbers are the same numbers: one figure,
// computed by the Gateway from the submission ledger, with the window it covers stated on it (mission
// "Clean up Your Throttle", 2026-09-05). Leads with the two headline rings (spoken, from phone), then
// where the turns come from, then the totals and what was left out. The hourly charts, full breakdown,
// and caveats stay on the desktop page. A self-hosted Gateway answers with a sentence, and this page shows
// that sentence (rulings R1 and R6).
// THE WINDOW COMES FROM THE URL (rulings R4 and R5): `/throttle?week=2026-W35` is what the mentor report's
// link carries, `?days=N` is a choice from the shared period selector, and neither asks for the Gateway's
// default. Choosing writes the length back to the URL; the Gateway decides what it means.
// Renders immediately with a loading state, shows an explicit error banner on failure (no-fallback rule),
// and auto-refreshes so the split moves live as the user drives by voice.

const REFRESH_MS = 10_000;

const RING_VOICE = "var(--accent)";
const RING_MOBILE = "#8b5cf6";

export function YourThrottle() {
  const [data, setData] = useState<ThrottleData | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [searchParams, setSearchParams] = useSearchParams();
  // The window the URL asks for. Stable for one URL, so the effect below re-runs only when the URL changes.
  const request = useMemo(() => throttleWindowFromSearch(searchParams), [searchParams]);
  const choose = (days: number) => setSearchParams({ days: String(days) });

  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    // A new window is a new page: back to the loading state at once, never the old window's numbers under
    // the new window's selection.
    setData(null);
    setError(null);

    const tick = async () => {
      try {
        const fresh = await getThrottle(controller.signal, request);
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
  }, [request]);

  const figure: ThrottleFigure | null = data !== null && data.available ? data.throttle : null;
  const timeZone = data !== null && data.available ? safeTimeZone(data.timeZone) : "UTC";
  const summary: ThrottleSummary | null = figure === null ? null : summarizeThrottle(figure);

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

      {data === null && error === null && <div className="thr-note">Loading your throttle...</div>}

      {/* The Gateway has no figure to show here and said why, in one sentence. Rendered verbatim. */}
      {data !== null && !data.available && (
        <div className="thr-note" role="status">
          {data.reason}
        </div>
      )}

      {figure !== null && <WindowNote figure={figure} timeZone={timeZone} />}
      {figure !== null && <ThrottleWindowSelector window={figure.window} onChoose={choose} />}

      {figure !== null && summary !== null && !summary.hasData && (
        <div className="thr-note">
          No turn counted in this window. Send a turn from the phone, desktop, or cockpit and it will show up
          here.
        </div>
      )}

      {figure !== null && summary !== null && summary.hasData && (
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
          </div>

          <SurfaceSplitBar summary={summary} />

          <div className="mthr-stats">
            <div className="mthr-stat">
              <div className="mthr-stat-value">{summary.totalTurns.toLocaleString()}</div>
              <div className="mthr-stat-label">Turns counted</div>
            </div>
            <div className="mthr-stat">
              <div className="mthr-stat-value">{figure.sessions.toLocaleString()}</div>
              <div className="mthr-stat-label">Sessions you drove</div>
            </div>
          </div>

          <ExcludedNote figure={figure} />

          <p className="mthr-foot">
            The full hourly charts and breakdown live on Your Throttle in the desktop Cockpit.
          </p>
        </div>
      )}
    </div>
  );
}

/** Format an ISO instant as a short local date in the display zone, or the raw text if it does not parse. */
function localDate(iso: string, timeZone: string): string {
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return iso;
  return new Intl.DateTimeFormat("en-US", { day: "numeric", month: "short", timeZone }).format(new Date(t));
}

// Which stretch of time the numbers describe: the Gateway's label and dates, rendered in the display zone.
function WindowNote({ figure, timeZone }: { figure: ThrottleFigure; timeZone: string }) {
  const { window: w, ledger } = figure;
  const recordStartsLate =
    ledger.earliestUtc !== null && w.fromUtc !== "" && Date.parse(ledger.earliestUtc) > Date.parse(w.fromUtc);
  return (
    <p className="thr-note" data-testid="mthr-window">
      <b>{w.label}</b>: {localDate(w.fromUtc, timeZone)} to {localDate(w.toUtc, timeZone)}, in submitted
      turns.
      {recordStartsLate && ledger.earliestUtc !== null && (
        <> Your record begins {localDate(ledger.earliestUtc, timeZone)}.</>
      )}
    </p>
  );
}

// What the definition left out, beside the share (rulings R7 and R17). The counts are the Gateway's.
function ExcludedNote({ figure }: { figure: ThrottleFigure }) {
  const { excluded, agentDrivenTurns } = figure;
  if (excluded.unresolved === 0 && agentDrivenTurns === 0) return null;
  return (
    <p className="thr-note" data-testid="mthr-excluded">
      {excluded.unresolved > 0 && (
        <>
          {excluded.unresolved.toLocaleString()} submission{excluded.unresolved === 1 ? "" : "s"} of yours could
          not be placed on a surface and {excluded.unresolved === 1 ? "is" : "are"} outside every number here.{" "}
        </>
      )}
      {agentDrivenTurns > 0 && (
        <>
          {agentDrivenTurns.toLocaleString()} turn{agentDrivenTurns === 1 ? " was" : "s were"} other sessions
          prompting yours; those are never in your share.
        </>
      )}
    </p>
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
