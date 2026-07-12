import { useEffect, useState } from "react";
import {
  getThrottle,
  summarizeThrottle,
  formatShare,
  MODALITY_LABEL,
  SURFACE_LABEL,
  SURFACE_ORDER,
  type ThrottleData,
  type ThrottleSummary,
} from "@devthrottle/client-core/stats/statsClient";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";

// The "Your Throttle" page (devthrottle-stats mission): the in-Cockpit port of the standalone Gateway
// /stats HTML page, so the user reads their own throttle in the app they already have open rather than
// navigating to a bare URL. Read-only, reads the same GET /stats/data feed the standalone page uses.
// Responsive (CodingStyle.md): renders immediately with a loading state, loads asynchronously, and on a
// load failure shows an explicit error banner (no-fallback rule). Auto-refreshes so the split visibly
// moves as the user keeps driving.

const REFRESH_MS = 10_000;

/** A big headline metric card (voice share, phone share, total turns). */
function HeadlineCard({ label, value, sub }: { label: string; value: string; sub: string }) {
  return (
    <div className="thr-card">
      <div className="thr-card-value">{value}</div>
      <div className="thr-card-label">{label}</div>
      <div className="thr-card-sub">{sub}</div>
    </div>
  );
}

export function YourThrottleView() {
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

      {summary === null && error === null && <div className="thr-loading">Loading...</div>}

      {summary !== null && !summary.hasData && (
        <div className="thr-empty">
          No input counted yet. Send a turn from the composer, dictation, phone, or cockpit and it will
          appear here.
        </div>
      )}

      {data !== null && data.concurrency !== null && (
        <div className="thr-section">
          <h2>Fleet concurrency</h2>
          <p className="thr-hint">
            How many sessions run at once across every machine. Loaded/running is the parallel capacity in
            flight; actively working is the subset whose agent is processing a turn this instant.
          </p>
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

      {summary !== null && summary.hasData && (
        <>
          <div className="thr-cards">
            <HeadlineCard
              label="Voice share of turns"
              value={formatShare(summary.voiceShare)}
              sub={`${summary.voiceTurns} of ${summary.totalTurns} turns spoken`}
            />
            <HeadlineCard
              label="Phone share of turns"
              value={formatShare(summary.phoneShare)}
              sub={`${summary.turnsBySurface.phone} of ${summary.totalTurns} turns from phone`}
            />
            <HeadlineCard
              label="Total turns"
              value={String(summary.totalTurns)}
              sub={`${summary.totalCharacters.toLocaleString()} characters`}
            />
          </div>

          <div className="thr-section">
            <h2>Turns by surface</h2>
            <table className="thr-table">
              <thead>
                <tr>
                  <th>Surface</th>
                  <th className="thr-num">Turns</th>
                </tr>
              </thead>
              <tbody>
                {SURFACE_ORDER.map((s) => (
                  <tr key={s}>
                    <td>{SURFACE_LABEL[s]}</td>
                    <td className="thr-num">{summary.turnsBySurface[s]}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <div className="thr-section">
            <h2>Full breakdown</h2>
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
                {data!.buckets.length === 0 ? (
                  <tr>
                    <td colSpan={4} className="thr-muted">
                      No buckets yet.
                    </td>
                  </tr>
                ) : (
                  data!.buckets.map((b) => (
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

      {data !== null && data.notCaptured.length > 0 && (
        <div className="thr-caveats">
          <h2>What these numbers do not include</h2>
          <ul>
            {data.notCaptured.map((c, i) => (
              <li key={i}>{c}</li>
            ))}
          </ul>
        </div>
      )}
    </div>
  );
}
