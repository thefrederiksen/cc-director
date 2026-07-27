import { useCallback, useEffect, useMemo, useState } from "react";
import { Link } from "react-router-dom";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import { durationFromMs } from "@devthrottle/client-core/sessions/waiting";
import {
  getWorkHistoryReport,
  type WorkHistoryReport,
  type WorkHistorySession,
} from "@devthrottle/client-core/history/historyClient";
import { ErrorBanner, LoadingState, PageHeader } from "../components";

// The History page (issue #2194): "what have I been working on?" answered from the Gateway's durable
// per-session record, over a range you pick, grouped by repository with a written summary per day and
// the individual sessions beneath it. Sessions running RIGHT NOW appear as entries that have not
// ended yet - the current session is just the row without an ending.
//
// THE CLIENT IS DUMB (rule 7): every ending label, tone, description line and summary on this page
// was folded once on the Gateway and is rendered verbatim. The one thing this page computes is
// layout. Honesty rule (#2157): a day whose roll-up paragraph has not been written yet says so; no
// number or paragraph is ever invented client-side.

const RANGES: ReadonlyArray<{ label: string; days: number }> = [
  { label: "Last day", days: 1 },
  { label: "3 days", days: 3 },
  { label: "Week", days: 7 },
  { label: "30 days", days: 30 },
];

/** The inclusive UTC day range for a preset: today back (days - 1). */
function rangeFor(days: number): { from: string; to: string } {
  const dayString = (d: Date) => d.toISOString().slice(0, 10);
  const now = new Date();
  const from = new Date(now.getTime() - (days - 1) * 24 * 60 * 60 * 1000);
  return { from: dayString(from), to: dayString(now) };
}

/** "2026-07-26" -> "Saturday 26 Jul 2026" in the viewer's locale, UTC calendar day. */
function dayHeading(day: string): string {
  const parsed = new Date(`${day}T00:00:00Z`);
  if (Number.isNaN(parsed.getTime())) return day;
  return parsed.toLocaleDateString(undefined, {
    weekday: "long",
    day: "numeric",
    month: "short",
    year: "numeric",
    timeZone: "UTC",
  });
}

function timeOf(iso: string | null | undefined): string {
  if (!iso) return "";
  const parsed = new Date(iso.endsWith("Z") ? iso : `${iso}Z`);
  if (Number.isNaN(parsed.getTime())) return iso;
  return parsed.toLocaleTimeString(undefined, { hour: "numeric", minute: "2-digit" });
}

function SessionEntry({ session }: { session: WorkHistorySession }) {
  const [open, setOpen] = useState(false);
  const live = session.endingKind == null;
  const meta: string[] = [];
  if (session.sessionName) meta.push(session.sessionNumber != null ? `${session.sessionName} (#${session.sessionNumber})` : session.sessionName);
  else if (session.sessionNumber != null) meta.push(`#${session.sessionNumber}`);
  if (session.machineName) meta.push(session.machineName);
  if (session.agentKind) meta.push(session.agentKind);
  if (session.model) meta.push(session.model);
  meta.push(`started ${timeOf(session.startedAtUtc)}`);
  // Agent turns (completed turns, internal#625) are the sharper fact when the Director reports
  // them; the input-turn count stays as the fallback for records from older Directors.
  if (session.agentTurnCount != null && session.agentTurnCount > 0) meta.push(`${session.agentTurnCount} turns`);
  else if (session.turnCount != null && session.turnCount > 0) meta.push(`${session.turnCount} turns`);
  if (session.idleSeconds != null && session.idleSeconds >= 60)
    meta.push(`idle ${durationFromMs(session.idleSeconds * 1000)}`);

  const hasDetail =
    (session.summaryText != null && session.summaryText.length > 0) ||
    (session.whatWasBuilt?.length ?? 0) > 0 ||
    (session.leftUnverified?.length ?? 0) > 0 ||
    (session.branches?.length ?? 0) > 0 ||
    (session.pullRequests?.length ?? 0) > 0 ||
    (session.commits?.length ?? 0) > 0;

  return (
    <li className={`wh-session wh-tone-${session.endingTone}`}>
      <button
        type="button"
        className="wh-session-row"
        onClick={() => setOpen((v) => !v)}
        disabled={!hasDetail}
        aria-expanded={hasDetail ? open : undefined}
      >
        <span className="wh-dot" aria-hidden="true" />
        <span className="wh-session-main">
          <span className="wh-session-desc">{session.descriptionLine}</span>
          <span className="wh-session-meta">{meta.join(" · ")}</span>
        </span>
        <span className="wh-ending">
          {live ? "Running now" : session.endingLabel ?? session.endingKind}
        </span>
      </button>
      {open && hasDetail && (
        <div className="wh-session-detail">
          {session.summaryText && (
            <p className="wh-summary-text">
              {session.summaryIsPartial && <span className="wh-partial">Partial record - </span>}
              {session.summaryText}
            </p>
          )}
          <DetailList title="Built" items={session.whatWasBuilt} />
          <DetailList title="Left unverified" items={session.leftUnverified} />
          <DetailList title="Branches" items={session.branches} />
          <DetailList title="Pull requests" items={session.pullRequests} />
          <DetailList title="Commits" items={session.commits} />
        </div>
      )}
    </li>
  );
}

function DetailList({ title, items }: { title: string; items?: string[] | null }) {
  if (!items || items.length === 0) return null;
  return (
    <div className="wh-detail-block">
      <div className="wh-detail-title">{title}</div>
      <ul className="wh-detail-items">
        {items.map((item) => (
          <li key={item}>{item}</li>
        ))}
      </ul>
    </div>
  );
}

export function HistoryView() {
  const [days, setDays] = useState(3);
  const [report, setReport] = useState<WorkHistoryReport | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (windowDays: number, signal?: AbortSignal) => {
    try {
      const { from, to } = rangeFor(windowDays);
      const result = await getWorkHistoryReport(from, to, signal);
      setReport(result);
      setError(null);
    } catch (err) {
      if (signal?.aborted !== true) setError(gatewayErrorMessage(err));
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    setReport(null);
    void load(days, controller.signal);
    return () => controller.abort();
  }, [load, days]);

  const totals = useMemo(() => {
    if (report === null) return null;
    const sessions = new Set<string>();
    let running = 0;
    for (const repo of report.repos)
      for (const day of repo.days)
        for (const s of day.sessions) {
          if (!sessions.has(s.sessionId) && s.endingKind == null) running++;
          sessions.add(s.sessionId);
        }
    return { repos: report.repos.length, sessions: sessions.size, running };
  }, [report]);

  return (
    <div className="page wh">
      <PageHeader
        title="History"
        subtitle="What was worked on, across every repository and machine - including the sessions running right now."
      />

      <div className="wh-ranges" role="group" aria-label="Time range">
        {RANGES.map((r) => (
          <button
            key={r.label}
            type="button"
            className={days === r.days ? "wh-range wh-range-on" : "wh-range"}
            onClick={() => setDays(r.days)}
          >
            {r.label}
          </button>
        ))}
        {totals && (
          <span className="wh-totals">
            {totals.sessions} session{totals.sessions === 1 ? "" : "s"} across {totals.repos}{" "}
            repositor{totals.repos === 1 ? "y" : "ies"}
            {totals.running > 0 ? ` · ${totals.running} running now` : ""}
          </span>
        )}
      </div>

      {error !== null ? (
        <ErrorBanner message={error} onRetry={() => void load(days)} />
      ) : report === null ? (
        <LoadingState message="Loading the work history..." />
      ) : report.repos.length === 0 ? (
        <div className="wh-empty">
          <p>No work recorded in this range yet.</p>
          <p className="wh-empty-sub">
            The Gateway records every session from the moment it first sees it on the stream, so
            history builds up from now on. Open <Link to="/sessions">Sessions</Link> to start one.
          </p>
        </div>
      ) : (
        report.repos.map((repo) => (
          <section key={repo.repoKey} className="wh-repo">
            <h2 className="wh-repo-name">{repo.displayName}</h2>
            {repo.days.map((day) => (
              <div key={day.day} className="wh-day">
                <h3 className="wh-day-head">{dayHeading(day.day)}</h3>
                {day.summaryText ? (
                  <p className="wh-rollup">
                    {day.summaryText}
                    {day.summaryPending && (
                      <span className="wh-pending"> (being refreshed)</span>
                    )}
                  </p>
                ) : (
                  <p className="wh-rollup wh-rollup-pending">
                    The day&apos;s summary has not been written yet.
                  </p>
                )}
                <ul className="wh-sessions">
                  {day.sessions.map((s) => (
                    <SessionEntry key={s.sessionId} session={s} />
                  ))}
                </ul>
              </div>
            ))}
          </section>
        ))
      )}
    </div>
  );
}
