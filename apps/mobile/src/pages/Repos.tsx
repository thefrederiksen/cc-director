import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router-dom";
import {
  getThrottle,
  summarizeRepos,
  formatShare,
  type ThrottleData,
  type RepoStat,
  type RepoSummary,
} from "@devthrottle/client-core/stats/statsClient";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";

// The private "Repos" page for the phone (devthrottle-stats mission): where the owner's development
// actually happens - how driving splits across the codebases worked in, ranked by submitted turns. Its
// own page, deliberately separate from Your Throttle, because which repos take the owner's time is
// private (unlike the shareable voice/surface splits). Reads the same GET /stats/data feed (repos ride
// on it now) as the Cockpit Repos page, so both shells show one identical, single-source view. Renders
// immediately with a loading state, loads asynchronously, shows an explicit error banner on failure
// (no-fallback rule), and auto-refreshes. Counts only - never any message text.

const REFRESH_MS = 10_000;

type Metric = "turns" | "characters" | "sessions";
const METRIC_LABEL: Record<Metric, string> = { turns: "Turns", characters: "Characters", sessions: "Sessions" };
const METRIC_WORD: Record<Metric, string> = { turns: "turns", characters: "characters", sessions: "sessions" };

function metricValue(r: RepoStat, metric: Metric): number {
  return metric === "turns" ? r.turns : metric === "characters" ? r.characters : r.sessions;
}

/** Compact large counts (48200 -> "48.2K", 1_200_000 -> "1.2M"); plain below 1,000. */
function compactNumber(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}K`;
  return String(n);
}

function formatValue(value: number, metric: Metric): string {
  return metric === "characters" ? compactNumber(value) : value.toLocaleString();
}

export function Repos() {
  const [data, setData] = useState<ThrottleData | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [metric, setMetric] = useState<Metric>("turns");

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

  const repos = data?.repos ?? [];
  const summary: RepoSummary | null = data === null ? null : summarizeRepos(repos);

  const ranked = useMemo(
    () => [...repos].sort((a, b) => metricValue(b, metric) - metricValue(a, metric)),
    [repos, metric],
  );
  const max = ranked.length > 0 ? metricValue(ranked[0], metric) : 0;
  const total = ranked.reduce((t, r) => t + metricValue(r, metric), 0);

  return (
    <div className="screen">
      <header className="app-bar">
        <Link className="back-link" to="/">
          Back
        </Link>
        <h1>Repos</h1>
      </header>

      <div className="repos-private">
        <span className="repos-lock" aria-hidden="true" />
        Private - only you
      </div>

      {error !== null && (
        <div className="banner banner-error" role="alert">
          {error}
        </div>
      )}

      {summary === null && error === null && <div className="thr-note">Loading...</div>}

      {summary !== null && !summary.hasData && (
        <div className="thr-note">
          No input counted yet. Drive a session in any repo and the codebases you work in will appear here,
          ranked.
        </div>
      )}

      {summary !== null && summary.hasData && (
        <>
          <section className="thr-cards" aria-label="Repo headlines">
            <div className="thr-card">
              <div className="thr-card-value">{summary.repoCount}</div>
              <div className="thr-card-label">Repos</div>
            </div>
            <div className="thr-card">
              <div className="thr-card-value">{summary.totalTurns.toLocaleString()}</div>
              <div className="thr-card-label">Turns</div>
            </div>
            <div className="thr-card">
              <div className="thr-card-value">{formatShare(summary.topShare)}</div>
              <div className="thr-card-label">Busiest</div>
            </div>
          </section>

          <div className="thr-caption">
            {summary.topRepoName !== null && (
              <>
                {summary.topRepoName} leads;{" "}
              </>
            )}
            {summary.totalSessions} session{summary.totalSessions === 1 ? "" : "s"},{" "}
            {compactNumber(summary.totalCharacters)} characters in total.
          </div>

          <div className="repos-seg" role="tablist" aria-label="Rank repositories by">
            {(["turns", "characters", "sessions"] as Metric[]).map((m) => (
              <button
                key={m}
                type="button"
                role="tab"
                aria-selected={metric === m}
                className={metric === m ? "repos-seg-btn on" : "repos-seg-btn"}
                onClick={() => setMetric(m)}
              >
                {METRIC_LABEL[m]}
              </button>
            ))}
          </div>

          <section className="thr-list" aria-label="By repository">
            <div className="thr-list-title">By {METRIC_WORD[metric]}</div>
            {ranked.map((r, i) => {
              const value = metricValue(r, metric);
              const width = max > 0 ? (value / max) * 100 : 0;
              const share = total > 0 ? Math.round((value / total) * 100) : 0;
              const voicePct = r.turns > 0 ? Math.round((r.voiceTurns / r.turns) * 100) : 0;
              const showSplit = metric === "turns" && r.turns > 0;
              return (
                <div className="repo-item" key={r.repo}>
                  <div className="repo-item-top">
                    <span className="repo-item-rank">{i + 1}</span>
                    <span className="repo-item-name">{r.repoName}</span>
                    <span className="repo-item-val">
                      {formatValue(value, metric)}
                      <span className="repo-item-share"> - {share}%</span>
                    </span>
                  </div>
                  <div className="repo-item-bar">
                    {showSplit ? (
                      <>
                        <span className="repo-item-voice" style={{ width: `${(width * voicePct) / 100}%` }} />
                        <span className="repo-item-typed" style={{ width: `${(width * (100 - voicePct)) / 100}%` }} />
                      </>
                    ) : (
                      <span className="repo-item-voice" style={{ width: `${width}%` }} />
                    )}
                  </div>
                </div>
              );
            })}
          </section>
        </>
      )}

      <section className="thr-caveats" aria-label="What this does and doesn't count">
        <div className="thr-list-title">What this does and doesn't count</div>
        <ul>
          <li>
            A turn is one submitted message; the amber part of each bar is the share driven by voice.
            Repos are grouped by their GitHub repository, so every worktree and machine rolls up into one row.
          </li>
          <li>
            Time is not shown (an idle hour looks like a busy one), and tokens are not shown - the tally
            counts turns and characters, never tokens. Nothing here is fabricated.
          </li>
        </ul>
        {data !== null && data.notCaptured.length > 0 && (
          <ul>
            {data.notCaptured.map((c, i) => (
              <li key={i}>{c}</li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}
