import { useCallback, useEffect, useState } from "react";
import {
  countFailedTurns,
  getTranscriptionStats,
  getTranscriptionTerms,
  SUCCESS_OUTCOME,
  type TranscriptionStats,
  type TermFrequency,
} from "@devthrottle/client-core/transcription/transcriptionAnalysisClient";

// The Transcription Health page: shows how fast and how well voice dictation is working on THIS
// machine, read from the local transcription telemetry the Gateway records for every turn (nothing
// leaves the machine). It reads GET /transcription/stats + /transcription/terms through the Gateway
// front door (client-core). Responsive (CodingStyle.md): renders immediately with a loading state and
// loads asynchronously; a load failure shows an explicit message, never a fabricated healthy state.

const WINDOWS: ReadonlyArray<{ label: string; days: number | undefined }> = [
  { label: "Today", days: 1 },
  { label: "7 days", days: 7 },
  { label: "All time", days: undefined },
];

function seconds(ms: number): string {
  if (ms <= 0) return "-";
  return ms >= 1000 ? `${(ms / 1000).toFixed(1)} s` : `${Math.round(ms)} ms`;
}

function outcomeLabel(code: string): string {
  switch (code) {
    case "ok":
      return "succeeded";
    case "out_of_credits":
      return "ran out of credits";
    case "provider_error":
      return "the provider rejected it";
    default:
      return code;
  }
}

export function TranscriptionHealthView() {
  const [days, setDays] = useState<number | undefined>(7);
  const [stats, setStats] = useState<TranscriptionStats | null>(null);
  const [terms, setTerms] = useState<TermFrequency[]>([]);
  const [loadError, setLoadError] = useState(false);
  const [refreshing, setRefreshing] = useState(false);

  const load = useCallback(async (window: number | undefined, signal?: AbortSignal) => {
    try {
      setLoadError(false);
      const [s, t] = await Promise.all([
        getTranscriptionStats(window, signal),
        getTranscriptionTerms(10, window, signal),
      ]);
      setStats(s);
      setTerms(t);
    } catch {
      if (signal?.aborted) return;
      setLoadError(true);
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void load(days, controller.signal);
    return () => controller.abort();
  }, [load, days]);

  // Refetch the current window on demand, so the page can be brought up to date without a full
  // browser reload. Immediate visual feedback: the button reports "Refreshing..." while it runs.
  const refresh = useCallback(async () => {
    setRefreshing(true);
    try {
      await load(days);
    } finally {
      setRefreshing(false);
    }
  }, [load, days]);

  // The failure count comes from the same byOutcome map the outcome breakdown renders, so the
  // number, the banner, and the breakdown are one source and can never contradict each other.
  const failures = stats ? countFailedTurns(stats) : 0;
  const healthy = stats !== null && stats.totalTurns > 0 && failures === 0;

  return (
    <div className="page txh">
      <div className="page-head">
        <h1>Transcription Health</h1>
        <button
          type="button"
          className="txh-refresh"
          onClick={() => void refresh()}
          disabled={refreshing}
        >
          {refreshing ? "Refreshing..." : "Refresh"}
        </button>
      </div>
      <p className="txh-lede">
        How fast and how well your voice dictation is working, from this machine only. Nothing here is
        sent anywhere - it is recorded locally for every dictation so you can see the speed, spot
        failures, and check that your custom words are being fixed.
      </p>

      <div className="txh-windows" role="group" aria-label="Time window">
        {WINDOWS.map((w) => (
          <button
            key={w.label}
            type="button"
            className={days === w.days ? "txh-window txh-window-on" : "txh-window"}
            onClick={() => setDays(w.days)}
          >
            {w.label}
          </button>
        ))}
      </div>

      {loadError ? (
        <div className="txh-error">
          Couldn&apos;t load transcription data from the Gateway.
          <button type="button" className="txh-retry" onClick={() => void load(days)}>
            Retry
          </button>
        </div>
      ) : stats === null ? (
        <div className="txh-loading">Loading...</div>
      ) : stats.totalTurns === 0 ? (
        <div className="txh-empty">No dictations recorded in this window yet. Try dictating something.</div>
      ) : (
        <>
          <div className={healthy ? "txh-banner txh-ok" : "txh-banner txh-warn"}>
            {healthy
              ? `Transcription is healthy - all ${stats.totalTurns} dictations succeeded.`
              : `${failures} of ${stats.totalTurns} dictations did not succeed - see the breakdown below.`}
          </div>

          <div className="txh-cards">
            <div className="txh-card">
              <div className="txh-card-title">Speech to text</div>
              <div className="txh-card-big">{seconds(stats.transcriptionMs.p50)}</div>
              <div className="txh-card-sub">
                typical &middot; slowest {seconds(stats.transcriptionMs.max)}
              </div>
            </div>
            <div className="txh-card">
              <div className="txh-card-title">Fixing your words</div>
              <div className="txh-card-big">{seconds(stats.cleanupMs.p50)}</div>
              <div className="txh-card-sub">
                typical &middot; slowest {seconds(stats.cleanupMs.max)}
              </div>
            </div>
            <div className="txh-card">
              <div className="txh-card-title">Dictations</div>
              <div className="txh-card-big">{stats.totalTurns}</div>
              <div className="txh-card-sub">{stats.successfulTurns} succeeded</div>
            </div>
            <div className="txh-card">
              <div className="txh-card-title">Words dictated</div>
              <div className="txh-card-big">{stats.totalWords.toLocaleString()}</div>
              <div className="txh-card-sub">
                {stats.cleanupAppliedTurns} had a word corrected
              </div>
            </div>
          </div>

          {failures > 0 && (
            <div className="txh-section">
              <h2>Why dictations failed</h2>
              <ul className="txh-outcomes">
                {Object.entries(stats.byOutcome)
                  .filter(([code]) => code !== SUCCESS_OUTCOME)
                  .map(([code, n]) => (
                    <li key={code}>
                      <span className="txh-count">{n}</span> {outcomeLabel(code)}
                    </li>
                  ))}
              </ul>
            </div>
          )}

          <div className="txh-section">
            <h2>Most-corrected words</h2>
            {terms.length === 0 ? (
              <p className="txh-muted">No custom words needed correcting in this window.</p>
            ) : (
              <table className="txh-terms">
                <thead>
                  <tr>
                    <th>Heard as</th>
                    <th>Corrected to</th>
                    <th>Times</th>
                  </tr>
                </thead>
                <tbody>
                  {terms.map((t) => (
                    <tr key={`${t.find}->${t.replace}`}>
                      <td>{t.find}</td>
                      <td>{t.replace}</td>
                      <td>{t.count}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            )}
          </div>

          <p className="txh-agent-note">
            Want more? Ask any agent to dig into this - it can query the Gateway directly at
            <code> /transcription/stats</code>, <code>/transcription/turns</code>,
            <code> /transcription/terms</code>, and <code>/transcription/words</code> - for example
            &quot;which word do I mis-say most&quot; or &quot;how has my dictation speed changed&quot;.
          </p>
        </>
      )}
    </div>
  );
}
