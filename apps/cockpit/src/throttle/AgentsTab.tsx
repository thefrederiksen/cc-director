import { useMemo, useState } from "react";
import {
  summarizeAgents,
  formatShare,
  type ThrottleFigure,
  type AgentStat,
  type AgentSummary,
} from "@devthrottle/client-core/stats/statsClient";

// The "Agents" tab of Your Throttle (owner ask): which agent CLI the work actually goes through - how
// driving splits across Claude Code, Codex, Gemini and the rest, ranked by submitted turns.
//
// The figure arrives as a prop: Your Throttle already polls GET /stats/data, and the per-agent split is
// part of the one figure that feed carries (mission "Clean up Your Throttle", ruling R9: the same
// submission ledger as the rings above, which records the agent kind on every submission). So this tab
// adds up to the Overview tab's totals, over the same window - the "attributing since" caveat the old
// tally needed is gone with the tally. Counts only - never any message text, and no character volume
// (ruling R16). It reuses the Repos tab's row and segmented-control styles so the two read as one page.

// The two honest metrics the ledger actually measures per agent. Tokens and characters are intentionally
// absent: the figure counts turns, and this tab never fabricates a number.
type Metric = "turns" | "sessions";
const METRIC_LABEL: Record<Metric, string> = { turns: "Turns", sessions: "Sessions" };
const METRIC_WORD: Record<Metric, string> = { turns: "turns", sessions: "sessions" };

function metricValue(a: AgentStat, metric: Metric): number {
  return metric === "turns" ? a.turns : a.sessions;
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

  // Turns other sessions drove into this agent's sessions. Stated as its own fact next to the human turns,
  // never added to them: "you drove 14, the fleet drove 300 into it" is the honest pair.
  const agentDriven =
    agent.agentDrivenTurns > 0 ? ` - ${agent.agentDrivenTurns.toLocaleString()} from agents` : "";

  const meta =
    metric === "turns"
      ? `${agent.sessions} session${agent.sessions === 1 ? "" : "s"} - ${voicePct}% voice${agentDriven}`
      : `${agent.turns.toLocaleString()} turns - ${voicePct}% voice${agentDriven}`;

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
        <div className="repo-bar-track" title={`${value.toLocaleString()} ${METRIC_WORD[metric]}`}>
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
        <div className="repo-val-num">{value.toLocaleString()}</div>
        <div className="repo-val-share">{share}%</div>
      </div>
    </div>
  );
}

export function AgentsTab({ figure }: { figure: ThrottleFigure }) {
  const [metric, setMetric] = useState<Metric>("turns");

  const agents = figure.agents;
  const summary: AgentSummary = summarizeAgents(agents);

  // Rank by the active metric (the feed arrives ranked by turns; re-sort so switching to sessions
  // re-orders the list honestly).
  const ranked = useMemo(
    () => [...agents].sort((a, b) => metricValue(b, metric) - metricValue(a, metric)),
    [agents, metric],
  );
  const max = ranked.length > 0 ? metricValue(ranked[0], metric) : 0;
  const total = ranked.reduce((t, a) => t + metricValue(a, metric), 0);

  if (!summary.hasData) {
    return (
      <div className="thr-empty">
        No agent usage counted in this window. Drive a session - Claude Code, Codex, or any other agent -
        and the split will appear here, ranked.
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
        {METRIC_WORD[metric]}. {figure.window.label}, the same turns as the rings on the Overview tab -
        desktop typing included.
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
          sub={figure.window.label.toLowerCase()}
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
        {/* Leverage (issue #1636): what the fleet did off the back of each turn the owner spent. Shown
            only once the fleet has actually driven itself - a "0x" on a machine that has never run a
            worker would be noise, not a fact. */}
        {summary.agentDrivenTurns > 0 && (
          <HeadlineCard
            label="Leverage"
            value={summary.leverage !== null ? `${summary.leverage.toFixed(1)}x` : "-"}
            sub={`${summary.agentDrivenTurns.toLocaleString()} turns agents drove agents`}
          />
        )}
      </div>

      <div className="repos-controls">
        <span className="repos-controls-label">Rank by</span>
        <div className="repos-seg" role="tablist" aria-label="Rank agents by">
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
          <li>
            On the turns view, each bar splits that agent&apos;s turns: the blue part is the share you
            spoke, the grey part is the share you typed.
          </li>
          <li>
            Agents are grouped by the CLI the session runs. A session whose agent was not recorded is
            counted under &quot;(unknown)&quot; rather than dropped - the turns are real either way.
          </li>
          <li>
            Turns you drove and turns agents drove into other agents are counted separately and never added
            together. The ranked bars and the voice share are YOUR driving; &quot;from agents&quot; and
            Leverage are the fleet driving itself. Text the product wrote itself - handover, queue drain -
            is not a turn and is counted by neither.
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
