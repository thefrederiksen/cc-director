import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
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
} from "@devthrottle/client-core/stats/statsClient";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";

// Your Throttle (devthrottle-stats mission): the in-app port of the standalone Gateway /stats page for
// the phone. Reads the same GET /stats/data feed the Cockpit reads, so the phone shows the same numbers
// in the app the user already has open. Renders immediately with a loading state, loads asynchronously,
// shows an explicit error banner on failure (no-fallback rule), and auto-refreshes so the split moves
// live as the user drives by voice.

const REFRESH_MS = 10_000;

/** A 24-hour bar chart of the hourly peak concurrent sessions. Bar = max loaded/running that hour;
 * darker inner portion = max actively-working. Pure CSS bars, theme-aware. */
function ConcurrencyChart({ hourly }: { hourly: ConcurrencyHour[] }) {
  const recent = hourly.slice(-24);
  const peak = Math.max(1, ...recent.map((h) => h.maxLive));
  return (
    <div
      className="thr-chart"
      role="img"
      aria-label={`Peak concurrent sessions per hour for the last ${recent.length} hours`}
    >
      {recent.map((h, i) => {
        const livePct = (h.maxLive / peak) * 100;
        const workPct = h.maxLive > 0 ? (h.maxWorking / h.maxLive) * 100 : 0;
        return (
          <div
            className="thr-bar-col"
            key={h.hour}
            title={`${h.hour}:00 UTC - ${h.maxLive} loaded, ${h.maxWorking} working, ${h.sessions} sessions`}
          >
            <div className="thr-bar-track">
              <div className="thr-bar-live" style={{ height: `${livePct}%` }}>
                <div className="thr-bar-work" style={{ height: `${workPct}%` }} />
              </div>
            </div>
            <div className="thr-bar-label">{i % 6 === 0 ? h.hour.slice(-2) : ""}</div>
          </div>
        );
      })}
    </div>
  );
}

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

      {summary === null && error === null && <div className="thr-note">Loading...</div>}

      {summary !== null && !summary.hasData && (
        <div className="thr-note">
          No input counted yet. Send a turn from the phone, desktop, or cockpit and it will show up here.
        </div>
      )}

      {data !== null && data.concurrency !== null && (
        <section className="thr-list" aria-label="Fleet concurrency">
          <div className="thr-list-title">Fleet concurrency</div>
          <div className="thr-row">
            <span className="thr-row-label">Loaded / running</span>
            <span className="thr-row-value">
              {data.concurrency.live.current} now, {data.concurrency.live.allTimeMax} peak
            </span>
          </div>
          <div className="thr-row">
            <span className="thr-row-label">Actively working</span>
            <span className="thr-row-value">
              {data.concurrency.working.current} now, {data.concurrency.working.allTimeMax} peak
            </span>
          </div>
        </section>
      )}

      {data !== null && data.concurrency !== null && data.concurrency.hourly.length > 0 && (
        <section className="thr-list" aria-label="Sessions per hour">
          <div className="thr-list-title">Sessions per hour (last 24h)</div>
          <ConcurrencyChart hourly={data.concurrency.hourly} />
        </section>
      )}

      {summary !== null && summary.hasData && (
        <>
          <section className="thr-cards" aria-label="Headline shares">
            <div className="thr-card">
              <div className="thr-card-value">{formatShare(summary.voiceShare)}</div>
              <div className="thr-card-label">Voice</div>
            </div>
            <div className="thr-card">
              <div className="thr-card-value">{formatShare(summary.phoneShare)}</div>
              <div className="thr-card-label">Phone</div>
            </div>
            <div className="thr-card">
              <div className="thr-card-value">{summary.totalTurns}</div>
              <div className="thr-card-label">Turns</div>
            </div>
          </section>

          <div className="thr-caption">
            {summary.voiceTurns} of {summary.totalTurns} turns spoken;{" "}
            {summary.turnsBySurface.phone} of {summary.totalTurns} from phone.{" "}
            {summary.totalCharacters.toLocaleString()} characters total.
          </div>

          <section className="thr-list" aria-label="Turns by surface">
            <div className="thr-list-title">Turns by surface</div>
            {SURFACE_ORDER.map((s) => (
              <div className="thr-row" key={s}>
                <span className="thr-row-label">{SURFACE_LABEL[s]}</span>
                <span className="thr-row-value">{summary.turnsBySurface[s]}</span>
              </div>
            ))}
          </section>

          <section className="thr-list" aria-label="Full breakdown">
            <div className="thr-list-title">Full breakdown</div>
            {data!.buckets.map((b) => (
              <div className="thr-row" key={`${b.modality}-${b.surface}`}>
                <span className="thr-row-label">
                  {MODALITY_LABEL[b.modality]} / {SURFACE_LABEL[b.surface]}
                </span>
                <span className="thr-row-value">
                  {b.turns} turns, {b.characters.toLocaleString()} chars
                </span>
              </div>
            ))}
          </section>
        </>
      )}

      {data !== null && data.notCaptured.length > 0 && (
        <section className="thr-caveats" aria-label="What is not included">
          <div className="thr-list-title">What these numbers do not include</div>
          <ul>
            {data.notCaptured.map((c, i) => (
              <li key={i}>{c}</li>
            ))}
          </ul>
        </section>
      )}
    </div>
  );
}
