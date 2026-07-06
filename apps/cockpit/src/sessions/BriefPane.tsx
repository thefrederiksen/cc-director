import { useCallback, useEffect, useRef, useState } from "react";
import {
  getBrief,
  getSummary,
  getScreenTail,
  type BriefResponse,
  type SessionSummaryDto,
} from "@devthrottle/client-core/brief/briefClient";
import { finalParagraph } from "@devthrottle/client-core/brief/briefFallback";
import { markdownToHtml } from "@devthrottle/client-core/history/historyMarkdown";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";

// The full-page session Brief for the React desktop Cockpit (issue #973) - the async-enrichment view
// layered OVER the live terminal: what the user asked (YOU ASKED), what the agent did (CLAUDE DID),
// and what the agent needs from the user (NEEDS YOU). A port of the Blazor Cockpit's BriefPane.razor
// condenser + raw tiers and the DirectorClient GetBrief/GetSummary degrade path.
//
// It reads GET /sessions/{sid}/brief same-origin through the Gateway (client-core/brief/briefClient),
// degrading to GET /sessions/{sid}/summary + the ported finalParagraph fallback on an old Director
// that predates the Brief endpoint - identical to the current behavior. The reply markdown renders
// with raw HTML DISABLED (client-core markdownToHtml escapes any embedded markup), so a transcript can
// never inject live markup into the page.
//
// The Brief is ASYNC ENRICHMENT: this component owns its own fetch/reload, entirely independent of the
// terminal's WebSocket. A slow condensation (the first /brief after a turn runs a ~1-2s model call)
// never blocks or freezes the live terminal - the terminal keeps streaming behind the tab while the
// Brief catches up. It reloads as turns progress, driven by the roster poll's activityState /
// briefingState transitions handed down from the parent.
//
// Scope (issue #973): the condenser tier (/brief) and the raw degrade tier (/summary). The wingman
// TurnBrief tier (one-tap options, explain, feedback, mission-complete close) depends on the Gateway
// wingman endpoints and lands with the awareness/turn-rail work (issue #974); it is intentionally not
// part of this pane yet.

interface BriefPaneProps {
  sessionId: string | undefined;
  /** The selected session's activity state from the roster poll (Working / WaitingForInput / Idle...).
   *  Drives the working/on-screen-prompt states and the reload-on-turn-complete. */
  activityState: string;
  /** The roster poll's briefing state (None / Briefing / Briefed / Failed): the transient yellow
   *  "reading the turn" window and the reload-when-a-fresh-brief-lands trigger. */
  briefingState: string;
  /** Switch the session-detail tab back to the live terminal (the on-screen-prompt "answer" path). */
  onOpenTerminal: () => void;
}

const TAIL_LINES = 8;
const TAIL_POLL_MS = 2000;

// A brand-new session has no transcript to brief yet: the Director returns "no_session_id" before the
// session links to a transcript, and "no_jsonl" once it links but before its first turn writes the
// .jsonl file. Both are the normal "just started" state - not an error - and the Director's error text
// for them is a raw absolute file path we must never surface (issue #1030).
function isJustStarted(status: string | undefined): boolean {
  return status === "no_session_id" || status === "no_jsonl";
}

export function BriefPane({ sessionId, activityState, briefingState, onOpenTerminal }: BriefPaneProps) {
  const [brief, setBrief] = useState<BriefResponse | null>(null);
  const [summary, setSummary] = useState<SessionSummaryDto | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [showFull, setShowFull] = useState(false);
  const [tail, setTail] = useState("");

  // The in-flight load's controller, so a newer load (a refresh, or a state-transition reload)
  // supersedes an older one rather than racing it onto the screen.
  const loadCtl = useRef<AbortController | null>(null);

  const isWorking = activityState === "Working";
  const isBriefing = briefingState === "Briefing";
  const replyPending = brief?.replyPending === true;

  // ---- derived content, mirroring BriefPane.razor (condenser + raw tiers only) ----
  const goal = brief?.goal ?? null;
  const lastAsk = brief?.lastAsk ?? summary?.lastUserPrompt ?? null;
  const fullReply = brief?.fullReply ?? summary?.lastAssistantText ?? null;
  const needsYou =
    brief?.needsYou ??
    (activityState === "WaitingForInput" && summary?.lastAssistantText
      ? finalParagraph(summary.lastAssistantText)
      : null);

  const load = useCallback(async () => {
    if (!sessionId) {
      setError("This session has no id.");
      return;
    }
    loadCtl.current?.abort();
    const ctl = new AbortController();
    loadCtl.current = ctl;
    setLoading(true);
    setError(null);
    try {
      // The condenser tier drives the three blocks and supplies the full reply for the expander. On a
      // 404 (an old Director without /brief, or a session it no longer knows) we degrade to /summary.
      const b = await getBrief(sessionId, ctl.signal);
      if (ctl.signal.aborted) return;
      setShowFull(false);
      if (b === null) {
        const s = await getSummary(sessionId, ctl.signal);
        if (ctl.signal.aborted) return;
        setBrief(null);
        setSummary(s);
        if (s === null) {
          setError("This Director does not know the session (or predates the summary endpoint).");
        } else if (s.status !== "ok" && !isJustStarted(s.status)) {
          // A "just started" status (no_session_id / no_jsonl) is a normal new-session state, not an
          // error - the render shows a friendly message for it and never surfaces s.error, which is a
          // raw absolute .jsonl path (issue #1030).
          setError(`Transcript not readable: ${s.error ?? s.status}`);
        }
      } else {
        setBrief(b);
        setSummary(null);
      }
    } catch (err) {
      if (ctl.signal.aborted) return;
      setError(`Brief load failed: ${gatewayErrorMessage(err)}`);
    } finally {
      if (!ctl.signal.aborted) setLoading(false);
    }
  }, [sessionId]);

  // Initial load (and a full reset) whenever the selected session changes.
  useEffect(() => {
    setBrief(null);
    setSummary(null);
    setError(null);
    setTail("");
    void load();
    return () => loadCtl.current?.abort();
  }, [sessionId, load]);

  // Reload as turns progress (the OnParametersSetAsync transitions in the Blazor pane): when the
  // wingman finished reading (Briefing -> Briefed/Failed) or a turn completed (Working -> not Working),
  // a fresh brief is in the store - pick it up. Refs hold the previously-seen states so a transition
  // fires the reload exactly once.
  const prevBriefing = useRef(briefingState);
  const prevActivity = useRef(activityState);
  useEffect(() => {
    if (!sessionId || loading) return;
    const briefingSettled =
      prevBriefing.current === "Briefing" && (briefingState === "Briefed" || briefingState === "Failed");
    const turnCompleted = prevActivity.current === "Working" && activityState !== "Working";
    prevBriefing.current = briefingState;
    prevActivity.current = activityState;
    if (briefingSettled || turnCompleted) void load();
  }, [activityState, briefingState, sessionId, loading, load]);

  // The live screen tail: while the session works / is briefing / is still writing its reply, poll the
  // current-screen grid so the pane shows "what is the agent doing right now". Independent of the load
  // above and of the terminal stream; cancels the moment the state no longer wants a tail or the pane
  // unmounts. This is the async peek that must never hold up the terminal - it is a plain polled read.
  const tailWanted = isWorking || isBriefing || replyPending;
  useEffect(() => {
    if (!sessionId || !tailWanted) {
      setTail("");
      return;
    }
    const ctl = new AbortController();
    let timer = 0;
    const poll = async () => {
      try {
        const t = await getScreenTail(sessionId, TAIL_LINES, ctl.signal);
        if (!ctl.signal.aborted) setTail(t);
      } catch {
        /* the tail is best-effort; the structured brief stands on its own if the screen read fails */
      }
      if (!ctl.signal.aborted) timer = window.setTimeout(() => void poll(), TAIL_POLL_MS);
    };
    void poll();
    return () => {
      ctl.abort();
      window.clearTimeout(timer);
    };
  }, [sessionId, tailWanted]);

  // ---- render ----
  if (loading && brief === null && summary === null && error === null) {
    return (
      <div className="brief">
        <div className="brief-loading">Loading brief...</div>
      </div>
    );
  }

  if (error !== null) {
    return (
      <div className="brief">
        <div className="brief-error">
          <div>{error}</div>
          <div className="brief-error-hint">Use the Terminal tab to see this session.</div>
        </div>
      </div>
    );
  }

  // A brand-new session (no_session_id / no_jsonl, from either the brief or the degraded summary tier)
  // gets a friendly "just started" state - never the raw error, which is an absolute .jsonl path.
  if (isJustStarted(brief?.status) || (brief === null && isJustStarted(summary?.status))) {
    return (
      <div className="brief">
        <div className="brief-status">
          <div className="blabel">THIS SESSION JUST STARTED</div>
          <div className="brief-muted">
            There is nothing to brief yet - this session has not taken a turn.
          </div>
          <div className="brief-error-hint">The Terminal tab always works.</div>
        </div>
      </div>
    );
  }

  if (brief !== null && brief.status !== "ok") {
    return (
      <div className="brief">
        <div className="brief-status">
          <div className="blabel">BRIEF UNAVAILABLE ({brief.status})</div>
          <div className="brief-muted">{brief.error ?? "The transcript is not readable yet."}</div>
          <div className="brief-error-hint">Use the Terminal tab to see this session.</div>
        </div>
      </div>
    );
  }

  const didBullets = brief?.didBullets ?? [];
  const showDidBlock = !isWorking && !isBriefing && !replyPending;
  const canExpand = didBullets.length > 0 && fullReply !== null;

  return (
    <div className="brief">
      {goal !== null && (
        <div className="brief-goal">
          <span className="blabel">GOAL</span>
          <span className="brief-goal-text">{goal}</span>
        </div>
      )}

      <div className="brief-block">
        <div className="blabel">YOU ASKED</div>
        {lastAsk !== null ? (
          <div className="brief-ask">{lastAsk}</div>
        ) : (
          <div className="brief-muted">No user prompt found in the transcript yet.</div>
        )}
      </div>

      {isBriefing ? (
        <div className="brief-block">
          <div className="blabel yellow">READING THE TURN...</div>
          {tail.trim().length > 0 && <pre className="brief-tail">{tail}</pre>}
        </div>
      ) : replyPending && activityState === "WaitingForInput" ? (
        <div className="brief-block">
          <div className="blabel red">NEEDS YOU - ON-SCREEN PROMPT</div>
          <div className="brief-needsyou urgency-blocking">
            The agent is asking with an interactive prompt that only the terminal shows.
          </div>
          {tail.trim().length > 0 && <pre className="brief-tail">{tail}</pre>}
          <button type="button" className="brief-open-term" onClick={onOpenTerminal}>
            Open the Terminal to answer
          </button>
        </div>
      ) : isWorking || replyPending ? (
        <div className="brief-block">
          <div className="blabel">THE AGENT IS WORKING</div>
          {tail.trim().length > 0 ? (
            <pre className="brief-tail">{tail}</pre>
          ) : (
            <div className="brief-working">
              The agent is {replyPending ? "writing its reply" : "working"} - nothing needed from you right now.
            </div>
          )}
        </div>
      ) : needsYou !== null ? (
        <div className="brief-block">
          <div className="blabel red">
            NEEDS YOU
            <span className="brief-verbatim-tag">
              {brief?.needsYouSource === "model"
                ? "in the agent's own words"
                : "from the reply's last paragraph"}
            </span>
          </div>
          <div className="brief-needsyou urgency-review">{needsYou}</div>
        </div>
      ) : null}

      {showDidBlock && (
        <>
          <div className="brief-divider" />
          <div className="brief-block">
            <div className="blabel">CLAUDE DID</div>
            {didBullets.length > 0 ? (
              <ul className="brief-did">
                {didBullets.map((b, i) => (
                  <li key={i}>{b}</li>
                ))}
              </ul>
            ) : fullReply !== null ? (
              // Raw HTML disabled: markdownToHtml escapes any embedded markup, so the transcript's
              // reply renders as safe markdown only (the Markdig DisableHtml posture).
              <div
                className="brief-reply-md"
                dangerouslySetInnerHTML={{ __html: markdownToHtml(fullReply) }}
              />
            ) : (
              <div className="brief-muted">The agent has not replied yet.</div>
            )}

            {canExpand && (
              <>
                <button type="button" className="brief-expand" onClick={() => setShowFull((v) => !v)}>
                  {showFull ? "hide full reply" : "full reply"}
                </button>
                {showFull && (
                  <div
                    className="brief-reply-md"
                    dangerouslySetInnerHTML={{ __html: markdownToHtml(fullReply) }}
                  />
                )}
              </>
            )}
          </div>
        </>
      )}

      <div className="brief-foot">
        {summary !== null && brief === null && (
          <span className="brief-muted">
            Raw summary tier (this Director predates the Brief endpoint - relaunch it on a new build).
          </span>
        )}
        <span className="brief-foot-spacer" />
        <button type="button" className="brief-refresh" onClick={() => void load()} disabled={loading}>
          {loading ? "refreshing..." : "refresh"}
        </button>
      </div>
    </div>
  );
}
