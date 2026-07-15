import { useMemo, useState } from "react";
import {
  summarizeAgents,
  formatShare,
  type ThrottleData,
  type AgentStat,
  type AgentSummary,
} from "@devthrottle/client-core/stats/statsClient";

// The "Agents" tab of Your Throttle (owner ask): which agent CLI the work actually goes through - how
// driving splits across Claude Code, Codex, Gemini and the rest, ranked by submitted turns.
//
// Data arrives as a prop: Your Throttle already polls GET /stats/data (the agent tally rides on that
// envelope), so the tab reads its parent's snapshot rather than opening a second poll of the same feed.
// Counts only - never any message text.
//
// This tab counts from agentsSinceUtc, NOT all-time. The turn totals had been accumulating long before the
// per-agent breakdown existed, and nothing recorded which agent those earlier turns ran under - so the
// numbers here do not reconcile with the Overview tab's totals, and the tab says so on its face rather
// than letting the owner read a smaller number as a drop in usage. It reuses the Repos tab's row and
// segmented-control styles so the two read as one page.

// The three honest metrics the system actually measures per agent. Tokens are intentionally absent: the
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

function metricValue(a: AgentStat, metric: Metric): number {
  return metric === "turns" ? a.turns : metric === "characters" ? a.characters : a.sessions;
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

/** The since-stamp as a plain date the owner can read ("15 Jul 2026"). Returns null when the Gateway sent
 * no stamp or an unparseable one, so the caller states nothing rather than inventing a start date. */
function sinceDate(iso: string): string | null {
  if (iso === "") return null;
  const t = Date.parse(iso);
  if (Number.isNaN(t)) return null;
  return new Date(t).toLocaleDateString(undefined, { day: "numeric", month: "short", year: "numeric" });
}

/** A big headline metric card, built from the same thr-stat tokens the Overview and Repos tabs use. */
function HeadlineCard({ label, value, sub }: { label: string; value: string; sub: string }) {
  return (
    <div className="thr-stat">
      <div className="thr-stat-value">{value}</div>
      <div className="thr-stat-label">{label}</div>
      <div className="thr-stat-label thr-stat-sub">{sub}</div>
    </div>
  );
}

/** One ranked agent row: rank, name, a proportional bar (voice/typed split on the turns metric, single
 * accent otherwise), and the active metric's value with its share of the total. */
function AgentRow({
  rank,
  agent,
  metric,
  max,
  total,
}: {
  rank: number;
  agent: AgentStat;
  metric: Metric;
  max: number;
  total: number;
}) {
  const value = metricValue(agent, metric);
  const width = max > 0 ? (value / max) * 100 : 0;
  const share = total > 0 ? Math.round((value / total) * 100) : 0;
  const voicePct = agent.turns > 0 ? Math.round((agent.voiceTurns / agent.turns) * 100) : 0;
  const showSplit = metric === "turns" && agent.turns > 0;

  // The secondary facts depend on which metric is the headline, so the row never just repeats the big
  // number - it shows the other two honest measures alongside it.
  const meta =
    metric === "turns"
      ? `${compactNumber(agent.characters)} chars - ${agent.sessions} session${agent.sessions === 1 ? "" : "s"} - ${voicePct}% voice`
      : metric === "characters"
        ? `${agent.turns.toLocaleString()} turns - ${agent.sessions} session${agent.sessions === 1 ? "" : "s"}`
        : `${agent.turns.toLocaleString()} turns - ${compactNumber(agent.characters)} chars`;

  return (
    <div className="repo-row">
      <div className="repo-rank">{rank}</div>
      <div className="repo-main">
        <div className="repo-top">
          <span className="repo-name">{agent.agentName}</span>
          {agent.agent === "" && (
            <span className="repo-path">sessions whose agent was not recorded</span>
          )}
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

export function AgentsTab({ data }: { data: ThrottleData }) {
  const [metric, setMetric] = useState<Metric>("turns");

  const agents = data.agents ?? [];
  const summary: AgentSummary = summarizeAgents(agents);
  const since = sinceDate(data.agentsSinceUtc ?? "");

  // Rank by the active metric (the feed arrives ranked by turns; re-sort so switching to characters or
  // sessions re-orders the list honestly).
  const ranked = useMemo(
    () => [...agents].sort((a, b) => metricValue(b, metric) - metricValue(a, metric)),
    [agents, metric],
  );
  const max = ranked.length > 0 ? metricValue(ranked[0], metric) : 0;
  const total = ranked.reduce((t, a) => t + metricValue(a, metric), 0);

  if (!summary.hasData) {
    return (
      <div className="thr-empty">
        No agent usage counted yet
        {since !== null ? ` since ${since}, when this breakdown started counting` : ""}. Drive a session -
        Claude Code, Codex, or any other agent - and the split will appear here, ranked. Turns driven before
        this breakdown existed are not included: nothing recorded which agent they ran under.
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
        Which agent you actually drive: how your driving splits across the agent CLIs you run, ranked by{" "}
        {METRIC_WORD[metric]}. Counted at the Director, so desktop typing is included.
        {since !== null
          ? ` Counting since ${since}, when this breakdown was added - so these numbers are smaller than your all-time totals on the other tabs.`
          : ""}
      </p>

      <div className="thr-stats repos-cards">
        <HeadlineCard
          label="Agents driven"
          value={String(summary.agentCount)}
          sub={`${summary.totalSessions} session${summary.totalSessions === 1 ? "" : "s"} in total`}
        />
        <HeadlineCard
          label="Turns counted"
          value={summary.totalTurns.toLocaleString()}
          sub={since !== null ? `since ${since}` : `${compactNumber(summary.totalCharacters)} characters`}
        />
        <HeadlineCard
          label="Most driven"
          value={formatShare(summary.topShare)}
          sub={summary.topAgentName !== null ? `${summary.topAgentName} leads` : "of your turns"}
        />
        <HeadlineCard
          label="Voice-driven"
          value={formatShare(summary.totalTurns > 0 ? summary.voiceTurns / summary.totalTurns : null)}
          sub="of turns spoken, not typed"
        />
      </div>

      <div className="repos-controls">
        <span className="repos-controls-label">Rank by</span>
        <div className="repos-seg" role="tablist" aria-label="Rank agents by">
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
          <h2>By agent</h2>
        </div>
        <div className="repo-rows">
          {ranked.map((a, i) => (
            <AgentRow key={a.agent} rank={i + 1} agent={a} metric={metric} max={max} total={total} />
          ))}
        </div>
      </div>

      {/* Only the caveats specific to grouping by agent. The general "what these numbers do not include"
          list lives on the Breakdown tab of this same page, so it is not repeated here. */}
      <div className="thr-caveats">
        <h2>What this does and doesn&apos;t count</h2>
        <ul>
          {since !== null && (
            <li>
              This tab counts from {since}, when the breakdown was added - not all-time. The turns on the
              other tabs go back further, and nothing recorded which agent those earlier ones ran under, so
              the two do not add up to the same number.
            </li>
          )}
          <li>
            On the turns view, each bar splits that agent&apos;s turns: the blue part is the share you
            spoke, the grey part is the share you typed.
          </li>
          <li>
            Agents are grouped by the CLI the session runs. A session the Director reported no agent for is
            counted under &quot;(unknown)&quot; rather than dropped - the turns are real either way.
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
