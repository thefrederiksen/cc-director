import { useMemo, useState } from "react";
import {
  formatPercent,
  type ThrottleFigure,
  type RepoStat,
  type RepoSummary,
} from "@devthrottle/client-core/stats/statsClient";

// The "Repos" tab of Your Throttle: where the owner's development actually happens - how driving splits
// across the codebases worked in, ranked by submitted turns.
//
// This was its own page and rail entry until 2026-07-14, kept apart so the private per-repo split could
// never ride along on-screen beside the shareable voice/surface splits. It is now a tab here (owner ask),
// which holds that line just as well: tabs are mutually exclusive, so Repos still never renders next to
// the throttle - the owner has to select it. The "private - only you" badge stays on the tab body so it
// still announces, on sight, that this view is not for sharing.
//
// The figure arrives as a prop: Your Throttle already polls GET /stats/data, and the per-repository split
// is part of the one figure that feed carries (mission "Clean up Your Throttle", ruling R9: the same
// submission ledger as the rings above, joined to the session history for the repository). Counts only -
// never any message text, and no character volume (ruling R16).

// The two honest metrics the ledger actually measures per repo. Tokens and characters are intentionally
// absent: the figure counts turns, and this tab never fabricates a number.
type Metric = "turns" | "sessions";
const METRIC_LABEL: Record<Metric, string> = { turns: "Turns", sessions: "Sessions" };
const METRIC_WORD: Record<Metric, string> = { turns: "turns", sessions: "sessions" };

function metricValue(r: RepoStat, metric: Metric): number {
  return metric === "turns" ? r.turns : r.sessions;
}

/** A big headline metric card, built from the same thr-stat tokens the Overview tab's tiles use, so the
 * two tabs read as one page. */
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
}: {
  rank: number;
  repo: RepoStat;
  metric: Metric;
  max: number;
}) {
  const value = metricValue(repo, metric);
  // The bar's length against the longest bar is layout; every RATIO printed or drawn is the Gateway's own
  // row share (fix-round finding F-01): the row's share of the metric, and its spoken share.
  const width = max > 0 ? (value / max) * 100 : 0;
  const share = formatPercent(metric === "turns" ? repo.turnPercent : repo.sessionPercent);
  const voicePct = formatPercent(repo.voicePercent);
  const voiceWidth = repo.voiceShare === null ? 0 : repo.voiceShare;
  const showSplit = metric === "turns" && repo.turns > 0;

  // The secondary facts on the meta line depend on which metric is the headline, so the row never just
  // repeats the big number - it shows the other honest measure alongside it.
  const meta =
    metric === "turns"
      ? `${repo.sessions} session${repo.sessions === 1 ? "" : "s"} - ${voicePct} voice`
      : `${repo.turns.toLocaleString()} turns - ${voicePct} voice`;

  return (
    <div className="repo-row">
      <div className="repo-rank">{rank}</div>
      <div className="repo-main">
        <div className="repo-top">
          <span className="repo-name">{repo.repoName}</span>
          {repo.repo && repo.repo !== repo.repoName && <span className="repo-path">{repo.repo}</span>}
        </div>
        <div className="repo-meta">{meta}</div>
        {repo.checkouts.length > 1 && (
          <div className="repo-checkouts" title={repo.checkouts.join("\n")}>
            {repo.checkouts.length} checkouts: {repo.checkouts.join(" - ")}
          </div>
        )}
        <div className="repo-bar-track" title={`${value.toLocaleString()} ${METRIC_WORD[metric]}`}>
          {showSplit ? (
            <>
              <div className="repo-bar-voice" style={{ width: `${width * voiceWidth}%` }} />
              <div className="repo-bar-typed" style={{ width: `${width * (1 - voiceWidth)}%` }} />
            </>
          ) : (
            <div className="repo-bar-voice" style={{ width: `${width}%` }} />
          )}
        </div>
      </div>
      <div className="repo-val">
        <div className="repo-val-num">{value.toLocaleString()}</div>
        <div className="repo-val-share">{share}</div>
      </div>
    </div>
  );
}

export function ReposTab({ figure }: { figure: ThrottleFigure }) {
  const [metric, setMetric] = useState<Metric>("turns");

  const repos = figure.repos;
  // The headline cards are the Gateway's (fix-round finding F-01): nothing here totals a row.
  const summary: RepoSummary = figure.reposSummary;

  // Rank by the active metric (the feed arrives ranked by turns; re-sort so switching to sessions
  // re-orders the list honestly).
  const ranked = useMemo(
    () => [...repos].sort((a, b) => metricValue(b, metric) - metricValue(a, metric)),
    [repos, metric],
  );
  const max = ranked.length > 0 ? metricValue(ranked[0], metric) : 0;

  if (!summary.hasData) {
    return (
      <div className="thr-empty">
        No turn counted in this window. Drive a session in any repo - by voice, from the phone, or typed
        on the desktop - and the repos you work in will appear here, ranked.
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
        ranked by {METRIC_WORD[metric]}. {figure.window.label}, the same turns as the rings on the Overview
        tab - desktop typing included.
      </p>

      <div className="thr-stats repos-cards">
        <HeadlineCard
          label="Repos worked in"
          value={String(summary.repoCount)}
          sub={`${summary.totalSessions} session${summary.totalSessions === 1 ? "" : "s"} in total`}
        />
        <HeadlineCard
          label="Turns placed in a repo"
          value={summary.totalTurns.toLocaleString()}
          sub={figure.window.label.toLowerCase()}
        />
        <HeadlineCard
          label="Busiest repo"
          value={formatPercent(summary.topPercent)}
          sub={summary.topRepoName !== null ? `${summary.topRepoName} leads` : "of your turns"}
        />
        <HeadlineCard
          label="Voice-driven"
          value={formatPercent(summary.voicePercent)}
          sub="of turns spoken, not typed"
        />
      </div>

      {/* Turns whose session has no repository on record are disclosed, never folded into a row (R7). */}
      {figure.reposUnattributedTurns > 0 && (
        <p className="thr-muted" data-testid="repos-unattributed">
          <b>{figure.reposUnattributedTurns.toLocaleString()}</b> of your turns went into sessions with no
          repository on record, so they are in the rings but not in any row here.
        </p>
      )}

      <div className="repos-controls">
        <span className="repos-controls-label">Rank by</span>
        <div className="repos-seg" role="tablist" aria-label="Rank repositories by">
          {(["turns", "sessions"] as Metric[]).map((m) => (
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
            <RepoRow key={r.repo} rank={i + 1} repo={r} metric={metric} max={max} />
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
            Repos are grouped by their GitHub repository, so every worktree and every machine you check a
            repo out on rolls up into one row (the working directories that fed it are listed under the
            name). A checkout whose Director has not reported a GitHub remote is grouped by its folder name
            instead, so the same repo across machines still merges. Time is deliberately not shown: an idle
            hour looks the same as a heads-down hour, so it would lie.
          </li>
          <li>
            Tokens and characters are not shown - the figure counts submitted turns, and this tab never
            fabricates a number it did not measure.
          </li>
        </ul>
      </div>
    </>
  );
}
