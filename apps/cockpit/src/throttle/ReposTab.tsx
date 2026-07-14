import { useMemo, useState } from "react";
import {
  summarizeRepos,
  formatShare,
  type ThrottleData,
  type RepoStat,
  type RepoSummary,
} from "@devthrottle/client-core/stats/statsClient";

// The "Repos" tab of Your Throttle (devthrottle-stats mission): where the owner's development actually
// happens - how driving splits across the codebases worked in, ranked by submitted turns.
//
// This was its own page and rail entry until 2026-07-14, kept apart so the private per-repo split could
// never ride along on-screen beside the shareable voice/surface splits. It is now the fourth tab here
// (owner ask), which holds that line just as well: tabs are mutually exclusive, so Repos still never
// renders next to the throttle - the owner has to select it. The "private - only you" badge stays on the
// tab body so it still announces, on sight, that this view is not for sharing.
//
// Data arrives as a prop: Your Throttle already polls GET /stats/data (repos ride on that envelope), so
// the tab reads its parent's snapshot rather than opening a second poll of the same feed. Counts only -
// never any message text.

// The three honest metrics the system actually measures per repo. Tokens are intentionally absent: the
// tally counts turns and characters, never tokens, and this tab never fabricates a number.
type Metric = "turns" | "characters" | "sessions";
const METRIC_LABEL: Record<Metric, string> = {
  turns: "Turns",
  characters: "Characters",
  sessions: "Sessions",
};
const METRIC_WORD: Record<Metric, string> = {
  turns: "turns",
  characters: "characters",
  sessions: "sessions",
};

function metricValue(r: RepoStat, metric: Metric): number {
  return metric === "turns" ? r.turns : metric === "characters" ? r.characters : r.sessions;
}

function formatValue(value: number, metric: Metric): string {
  return metric === "characters" ? compactNumber(value) : value.toLocaleString();
}

/** Compact large character counts (e.g. 48200 -> "48.2K", 1_200_000 -> "1.2M"); plain below 1,000. */
function compactNumber(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}K`;
  return String(n);
}

/** A big headline metric card, built from the same thr-stat tokens the Overview tab's tiles use, so the
 * two tabs read as one page. (Until this became a tab it asked for thr-card-*, which the Cockpit
 * stylesheet has never defined - only the phone's does - so these four cards rendered as an unstyled
 * stack on the old standalone page.) */
function HeadlineCard({ label, value, sub }: { label: string; value: string; sub: string }) {
  return (
    <div className="thr-stat">
      <div className="thr-stat-value">{value}</div>
      <div className="thr-stat-label">{label}</div>
      <div className="thr-stat-label thr-stat-sub">{sub}</div>
    </div>
  );
}

/** One ranked repository row: rank, name + full path, a proportional bar (voice/typed split on the turns
 * metric, single accent otherwise), and the active metric's value with its share of the total. */
function RepoRow({
  rank,
  repo,
  metric,
  max,
  total,
}: {
  rank: number;
  repo: RepoStat;
  metric: Metric;
  max: number;
  total: number;
}) {
  const value = metricValue(repo, metric);
  const width = max > 0 ? (value / max) * 100 : 0;
  const share = total > 0 ? Math.round((value / total) * 100) : 0;
  const voicePct = repo.turns > 0 ? Math.round((repo.voiceTurns / repo.turns) * 100) : 0;
  const showSplit = metric === "turns" && repo.turns > 0;

  // The secondary facts on the meta line depend on which metric is the headline, so the row never just
  // repeats the big number - it shows the other two honest measures alongside it.
  const meta =
    metric === "turns"
      ? `${compactNumber(repo.characters)} chars - ${repo.sessions} session${repo.sessions === 1 ? "" : "s"} - ${voicePct}% voice`
      : metric === "characters"
        ? `${repo.turns.toLocaleString()} turns - ${repo.sessions} session${repo.sessions === 1 ? "" : "s"}`
        : `${repo.turns.toLocaleString()} turns - ${compactNumber(repo.characters)} chars`;

  return (
    <div className="repo-row">
      <div className="repo-rank">{rank}</div>
      <div className="repo-main">
        <div className="repo-top">
          <span className="repo-name">{repo.repoName}</span>
          {repo.repo && repo.repo !== repo.repoName && <span className="repo-path">{repo.repo}</span>}
        </div>
        <div className="repo-meta">{meta}</div>
        <div className="repo-bar-track" title={`${formatValue(value, metric)} ${METRIC_WORD[metric]}`}>
          {showSplit ? (
            <>
              <div className="repo-bar-voice" style={{ width: `${(width * voicePct) / 100}%` }} />
              <div className="repo-bar-typed" style={{ width: `${(width * (100 - voicePct)) / 100}%` }} />
            </>
          ) : (
            <div className="repo-bar-voice" style={{ width: `${width}%` }} />
          )}
        </div>
      </div>
      <div className="repo-val">
        <div className="repo-val-num">{formatValue(value, metric)}</div>
        <div className="repo-val-share">{share}%</div>
      </div>
    </div>
  );
}

export function ReposTab({ data }: { data: ThrottleData }) {
  const [metric, setMetric] = useState<Metric>("turns");

  const repos = data.repos ?? [];
  const summary: RepoSummary = summarizeRepos(repos);

  // Rank by the active metric (the feed arrives ranked by turns; re-sort so switching to characters or
  // sessions re-orders the list honestly).
  const ranked = useMemo(
    () => [...repos].sort((a, b) => metricValue(b, metric) - metricValue(a, metric)),
    [repos, metric],
  );
  const max = ranked.length > 0 ? metricValue(ranked[0], metric) : 0;
  const total = ranked.reduce((t, r) => t + metricValue(r, metric), 0);

  if (!summary.hasData) {
    return (
      <div className="thr-empty">
        No input counted yet. Drive a session in any repo - by voice, from the phone, or typed on the
        desktop - and the repos you work in will appear here, ranked.
      </div>
    );
  }

  return (
    <>
      <div className="repos-private" title="This tab is only ever shown to you">
        <span className="repos-lock" aria-hidden="true" />
        Private - only you
      </div>

      <p className="repos-intro">
        Where your development actually happens: how your driving splits across the codebases you work in,
        ranked by {METRIC_WORD[metric]}. All-time, counted at the Director so desktop typing is included.
      </p>

      <div className="thr-stats repos-cards">
        <HeadlineCard
          label="Repos worked in"
          value={String(summary.repoCount)}
          sub={`${summary.totalSessions} session${summary.totalSessions === 1 ? "" : "s"} in total`}
        />
        <HeadlineCard
          label="Total turns"
          value={summary.totalTurns.toLocaleString()}
          sub={`${compactNumber(summary.totalCharacters)} characters`}
        />
        <HeadlineCard
          label="Busiest repo"
          value={formatShare(summary.topShare)}
          sub={summary.topRepoName !== null ? `${summary.topRepoName} leads` : "of your turns"}
        />
        <HeadlineCard
          label="Voice-driven"
          value={formatShare(summary.totalTurns > 0 ? summary.voiceTurns / summary.totalTurns : null)}
          sub="of turns spoken, not typed"
        />
      </div>

      <div className="repos-controls">
        <span className="repos-controls-label">Rank by</span>
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
      </div>

      <div className="thr-panel">
        <div className="thr-panel-head">
          <h2>By repository</h2>
        </div>
        <div className="repo-rows">
          {ranked.map((r, i) => (
            <RepoRow key={r.repo} rank={i + 1} repo={r} metric={metric} max={max} total={total} />
          ))}
        </div>
      </div>

      {/* Only the caveats specific to grouping by repository. The general "what these numbers do not
          include" list lives on the Breakdown tab of this same page, so it is not repeated here. */}
      <div className="thr-caveats">
        <h2>What this does and doesn&apos;t count</h2>
        <ul>
          <li>
            On the turns view, each bar splits that repo&apos;s turns: the blue part is the share you
            spoke, the grey part is the share you typed.
          </li>
          <li>
            Repos are grouped by the session&apos;s working directory. Time is deliberately not shown: an
            idle hour looks the same as a heads-down hour, so it would lie. Turns and characters track what
            you actually pushed through.
          </li>
          <li>
            Tokens are not shown - the tally counts turns and characters, never tokens, and this tab never
            fabricates a number it did not measure.
          </li>
        </ul>
      </div>
    </>
  );
}
