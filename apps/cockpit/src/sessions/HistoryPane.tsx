import { useCallback, useEffect, useLayoutEffect, useRef, useState } from "react";
import { getSessionHistory } from "@devthrottle/client-core/api/client";
import type { SessionHistoryDto } from "@devthrottle/client-core/history/types";
import {
  anyHidden,
  mapHistory,
  type HistoryBubble,
  type HistoryBubbleFilter,
} from "@devthrottle/client-core/history/bubbleMapper";
import { cleanForReading } from "@devthrottle/client-core/history/historyText";
import { markdownToHtml } from "@devthrottle/client-core/history/historyMarkdown";
import { extractLinks, type HistoryLink } from "@devthrottle/client-core/history/historyLinks";

// The History tab for the React desktop Cockpit (issue #974) - a read-only, agent-agnostic view of one
// session's conversation, rendered from GET /sessions/{sid}/history (proxied by the Gateway). It is a
// port of the Blazor Cockpit's HistoryPane.razor AND the mobile app's Chat history renderer: all three
// share the SAME client-core/history pipeline (mapHistory -> cleanForReading -> markdownToHtml +
// extractLinks), so the desktop, web, and mobile History views read identically for the same session
// and never diverge. This is the explicit issue-#974 reuse mandate.
//
// It is layered as a session-main tab beside the live terminal, which stays MOUNTED (hidden, not torn
// down) behind it, so this pane's own polled fetch never blocks or freezes the terminal stream.
// Mounting only while the tab is shown means the poll never runs while hidden.
//
// Bodies render Markdown with raw HTML disabled (client-core markdownToHtml escapes any embedded
// markup), so a transcript can never inject live markup. File paths and URLs surface as copyable link
// chips via the shared LinkDetector port. Sticky-bottom scroll: the view follows new turns while the
// reader is at the bottom and stops the instant they scroll up.

const POLL_INTERVAL_MS = 2500;
const BOTTOM_THRESHOLD_PX = 40;
const FILTER_STORAGE_KEY = "cockpit.historyFilter";

interface RenderedBubble {
  bubble: HistoryBubble;
  html: string;
  links: HistoryLink[];
}

function loadFilter(): HistoryBubbleFilter {
  const fallback: HistoryBubbleFilter = { showToolCalls: false, showToolResults: false, showThinking: false };
  try {
    const raw = window.localStorage.getItem(FILTER_STORAGE_KEY);
    if (!raw) return fallback;
    const parts = raw.split(",");
    if (parts.length !== 3) return fallback;
    return {
      showToolCalls: parts[0] === "true",
      showToolResults: parts[1] === "true",
      showThinking: parts[2] === "true",
    };
  } catch {
    return fallback;
  }
}

function persistFilter(filter: HistoryBubbleFilter): void {
  try {
    window.localStorage.setItem(
      FILTER_STORAGE_KEY,
      `${filter.showToolCalls},${filter.showToolResults},${filter.showThinking}`,
    );
  } catch {
    /* storage unavailable (private mode) - the filter still works for this session */
  }
}

// Cheap change signature: count + total chars + last bubble tail + history state + filter. Mirrors the
// desktop/mobile HistoryPane so a steady, identical poll never re-renders (and never disturbs the
// scroll position of a reader who has scrolled up).
function buildSignature(bubbles: HistoryBubble[], state: string | null | undefined, filter: HistoryBubbleFilter): string {
  const f = `${filter.showToolCalls}${filter.showToolResults}${filter.showThinking}`;
  if (bubbles.length === 0) return `0|${state ?? ""}|${f}`;
  let total = 0;
  for (const b of bubbles) total += b.body.length;
  const last = bubbles[bubbles.length - 1].body;
  const tail = last.length <= 64 ? last : last.slice(-64);
  return `${bubbles.length}|${total}|${tail}|${state ?? ""}|${f}`;
}

// A link label, truncated in the middle for long URLs/paths (mirrors HistoryPane.LinkLabel).
function linkLabel(text: string): string {
  return text.length <= 60 ? text : text.slice(0, 28) + "..." + text.slice(-28);
}

// The transcript-derived history-state label (#741): distinct from the live status badge.
function stateLabel(state: string): string {
  switch (state) {
    case "BackgroundRunning":
      return "history: Background running";
    case "Working":
      return "history: Working";
    case "NeedsYou":
      return "history: Needs you";
    default:
      return "history: Idle";
  }
}

function stateClass(state: string): string {
  switch (state) {
    case "BackgroundRunning":
      return "bg";
    case "Working":
      return "working";
    case "NeedsYou":
      return "needs";
    default:
      return "idle";
  }
}

interface HistoryPaneProps {
  sessionId: string | undefined;
}

export function HistoryPane({ sessionId }: HistoryPaneProps) {
  const [filter, setFilter] = useState<HistoryBubbleFilter>(loadFilter);
  const [bubbles, setBubbles] = useState<RenderedBubble[]>([]);
  const [emptyText, setEmptyText] = useState("Waiting for the conversation to start...");
  const [historyState, setHistoryState] = useState<string | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const [copied, setCopied] = useState<string | null>(null);

  const scrollRef = useRef<HTMLDivElement | null>(null);
  const atBottomRef = useRef(true);
  const signatureRef = useRef("");
  const lastHistoryRef = useRef<SessionHistoryDto | null>(null);
  const filterRef = useRef(filter);
  filterRef.current = filter;

  // Map the given history through the current filter, clean + render each bubble, and commit it -
  // unless the signature is unchanged (the guard that keeps a steady poll from yanking the scroll).
  const renderHistory = useCallback((history: SessionHistoryDto | null, force: boolean) => {
    const f = filterRef.current;
    const mapped = mapHistory(history, f);

    if (history && history.isSupported === false) setEmptyText("History is not available for this agent yet.");
    else if (mapped.length === 0 && anyHidden(f) && history && history.messages.length > 0)
      setEmptyText("No messages match the current filters.");
    else setEmptyText("Waiting for the conversation to start...");

    setHistoryState(history?.historyState ?? null);

    const signature = buildSignature(mapped, history?.historyState, f);
    if (!force && signature === signatureRef.current) return; // unchanged - do not re-render
    signatureRef.current = signature;

    const rendered: RenderedBubble[] = [];
    for (const b of mapped) {
      // Raw terminal scrollback (Gemini) is shown verbatim; everything else is cleaned of transcript
      // machinery (command wrapper tags, system-reminder blocks, ANSI codes) before Markdown.
      if (b.isRawText) {
        rendered.push({ bubble: b, html: "", links: [] });
        continue;
      }
      const clean = cleanForReading(b.body);
      if (clean.length === 0) continue; // the whole message was machinery - drop the empty bubble
      rendered.push({ bubble: { ...b, body: clean }, html: markdownToHtml(clean), links: extractLinks(clean) });
    }
    setBubbles(rendered);
  }, []);

  // Live poll every 2.5s. AbortController cancels the in-flight fetch on unmount/session switch. The
  // signature/scroll refs reset per session so a switch never mixes another session's cache in.
  useEffect(() => {
    if (!sessionId) return;
    signatureRef.current = "";
    lastHistoryRef.current = null;
    atBottomRef.current = true;
    setBubbles([]);
    setLoadFailed(false);

    const controller = new AbortController();
    let cancelled = false;

    const refresh = async () => {
      try {
        const history = await getSessionHistory(sessionId, controller.signal);
        if (cancelled) return;
        setLoadFailed(false);
        lastHistoryRef.current = history;
        renderHistory(history, false);
      } catch {
        if (cancelled || controller.signal.aborted) return;
        setLoadFailed(true);
      }
    };

    void refresh();
    const timer = window.setInterval(() => void refresh(), POLL_INTERVAL_MS);
    return () => {
      cancelled = true;
      controller.abort();
      window.clearInterval(timer);
    };
  }, [sessionId, renderHistory]);

  // Sticky-bottom: after the bubble list changes, stick to the bottom ONLY if the reader is already
  // at the bottom. A scrolled-up reader is never moved.
  useLayoutEffect(() => {
    const el = scrollRef.current;
    if (el && atBottomRef.current) el.scrollTop = el.scrollHeight;
  }, [bubbles]);

  const onScroll = useCallback(() => {
    const el = scrollRef.current;
    if (!el) return;
    atBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < BOTTOM_THRESHOLD_PX;
  }, []);

  // A "Show:" checkbox flipped: remember the choice and re-render the cached history immediately
  // through the new filter (force, since the filter is part of the signature).
  const onFilterChange = useCallback((next: HistoryBubbleFilter) => {
    setFilter(next);
    filterRef.current = next;
    persistFilter(next);
    renderHistory(lastHistoryRef.current, true);
  }, [renderHistory]);

  const copyLink = useCallback(async (text: string) => {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(text);
      window.setTimeout(() => setCopied((cur) => (cur === text ? null : cur)), 1500);
    } catch {
      /* copy is best-effort */
    }
  }, []);

  return (
    <div className="hist">
      <div className="hist-head">
        <span className="hist-title">Conversation history</span>
        {bubbles.length > 0 && (
          <span className="hist-count">
            {bubbles.length} message{bubbles.length === 1 ? "" : "s"}
          </span>
        )}
        <span className="hist-live">live</span>
        {historyState !== null && (
          <span
            className={`hist-state ${stateClass(historyState)}`}
            title="Transcript-derived state - separate from the live status badge"
          >
            {stateLabel(historyState)}
          </span>
        )}
        <span className="hist-filter">
          <span>Show:</span>
          <label title="Show the &quot;[tool] ...&quot; lines where the assistant calls a tool.">
            <input
              type="checkbox"
              checked={filter.showToolCalls}
              onChange={(e) => onFilterChange({ ...filter, showToolCalls: e.target.checked })}
            />{" "}
            Tool calls
          </label>
          <label title="Show the &quot;Tool result&quot; bubbles (command output, file lists, exit codes).">
            <input
              type="checkbox"
              checked={filter.showToolResults}
              onChange={(e) => onFilterChange({ ...filter, showToolResults: e.target.checked })}
            />{" "}
            Results
          </label>
          <label title="Show the assistant's &quot;(thinking) ...&quot; reasoning lines.">
            <input
              type="checkbox"
              checked={filter.showThinking}
              onChange={(e) => onFilterChange({ ...filter, showThinking: e.target.checked })}
            />{" "}
            Thinking
          </label>
        </span>
      </div>

      {loadFailed && bubbles.length === 0 ? (
        <div className="hist-empty">Could not read this session's history right now. Retrying...</div>
      ) : bubbles.length === 0 ? (
        <div className="hist-empty">{emptyText}</div>
      ) : (
        <div className="hist-scroll" ref={scrollRef} onScroll={onScroll}>
          {bubbles.map((r, i) => (
            <div className={`hist-bubble ${r.bubble.kind}`} key={i}>
              <div className="hist-speaker">{r.bubble.speaker}</div>
              {r.bubble.isRawText ? (
                <pre className="hist-body raw">{r.bubble.body}</pre>
              ) : (
                <div className="hist-body md" dangerouslySetInnerHTML={{ __html: r.html }} />
              )}
              {r.links.length > 0 && (
                <div className="hist-links">
                  {r.links.map((link, j) => (
                    <span className={`hist-link ${link.isUrl ? "url" : "path"}`} key={j}>
                      {link.isUrl ? (
                        <a
                          className="hist-link-open"
                          href={link.text}
                          target="_blank"
                          rel="noopener noreferrer"
                          title={link.text}
                        >
                          {linkLabel(link.text)}
                        </a>
                      ) : (
                        <span className="hist-link-text" title={link.text}>
                          {linkLabel(link.text)}
                        </span>
                      )}
                      <button type="button" className="hist-link-copy" onClick={() => void copyLink(link.text)}>
                        {link.isUrl ? "Copy URL" : "Copy path"}
                      </button>
                      {copied === link.text && <span className="hist-link-copied">copied</span>}
                    </span>
                  ))}
                </div>
              )}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
