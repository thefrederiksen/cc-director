import { useCallback, useLayoutEffect, useRef, useState } from "react";
import { useSessionChat } from "@devthrottle/client-core/history/useSessionChat";
import { chatLinkLabel } from "@devthrottle/client-core/history/chatView";

// The Cockpit Chat tab (issue #1213): a thin view over the SAME shared client-core hook the mobile Chat
// page uses (useSessionChat), so the two apps render the cleaned conversation history from one source.
// The composer at the bottom of SessionDetail drives replies for every tab, so this tab is the read
// view only: the desktop-History "Show:" filter and the live bubble list. The terminal socket is never
// touched (the TerminalPane stays mounted-hidden in SessionDetail while this tab is active).

const BOTTOM_THRESHOLD_PX = 40;

export function ChatTab({ sessionId }: { sessionId: string | undefined }) {
  const { bubbles, emptyText, loadFailed, filter, setFilter } = useSessionChat(sessionId);
  const [copied, setCopied] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const scrollRef = useRef<HTMLDivElement | null>(null);
  const atBottomRef = useRef(true);

  // Sticky-bottom: stick to the bottom after the list changes ONLY if the reader is already there.
  useLayoutEffect(() => {
    const el = scrollRef.current;
    if (el && atBottomRef.current) el.scrollTop = el.scrollHeight;
  }, [bubbles]);

  const onScroll = useCallback(() => {
    const el = scrollRef.current;
    if (!el) return;
    atBottomRef.current = el.scrollHeight - el.scrollTop - el.clientHeight < BOTTOM_THRESHOLD_PX;
  }, []);

  const copyLink = useCallback(async (text: string) => {
    try {
      await navigator.clipboard.writeText(text);
      setCopied(text);
      window.setTimeout(() => setCopied((cur) => (cur === text ? null : cur)), 1500);
    } catch (err) {
      setError(err instanceof Error ? err.message : "Copy failed");
    }
  }, []);

  return (
    <div className="chat-tab">
      {/* Compact "Show:" filter, mirroring the desktop History tab and the mobile Chat page. */}
      <div className="chat-filter">
        <span className="chat-filter-label">Show:</span>
        <label>
          <input
            type="checkbox"
            checked={filter.showToolCalls}
            onChange={(e) => setFilter({ ...filter, showToolCalls: e.target.checked })}
          />{" "}
          Tool calls
        </label>
        <label>
          <input
            type="checkbox"
            checked={filter.showToolResults}
            onChange={(e) => setFilter({ ...filter, showToolResults: e.target.checked })}
          />{" "}
          Results
        </label>
        <label>
          <input
            type="checkbox"
            checked={filter.showThinking}
            onChange={(e) => setFilter({ ...filter, showThinking: e.target.checked })}
          />{" "}
          Thinking
        </label>
        <span className="chat-live">live</span>
      </div>

      {error !== null && <div className="composer-error" role="alert">{error}</div>}

      <div className="chat-stage">
        {loadFailed && bubbles.length === 0 ? (
          <div className="chat-empty">Could not read this session's history right now. Retrying...</div>
        ) : bubbles.length === 0 ? (
          <div className="chat-empty">{emptyText}</div>
        ) : (
          <div className="chat-scroll" ref={scrollRef} onScroll={onScroll}>
            {bubbles.map((r, i) => (
              <div className={`chat-bubble ${r.bubble.kind}`} key={i}>
                <div className="chat-speaker">{r.bubble.speaker}</div>
                {r.bubble.isRawText ? (
                  <pre className="chat-body raw">{r.bubble.body}</pre>
                ) : (
                  <div className="chat-body md" dangerouslySetInnerHTML={{ __html: r.html }} />
                )}
                {r.links.length > 0 && (
                  <div className="chat-links">
                    {r.links.map((link, j) => (
                      <span className={`chat-link ${link.isUrl ? "url" : "path"}`} key={j}>
                        {link.isUrl ? (
                          <a
                            className="chat-link-open"
                            href={link.text}
                            target="_blank"
                            rel="noopener noreferrer"
                            title={link.text}
                          >
                            {chatLinkLabel(link.text)}
                          </a>
                        ) : (
                          <span className="chat-link-text" title={link.text}>{chatLinkLabel(link.text)}</span>
                        )}
                        <button type="button" className="chat-link-copy" onClick={() => void copyLink(link.text)}>
                          {link.isUrl ? "Copy URL" : "Copy path"}
                        </button>
                        {copied === link.text && <span className="chat-link-copied">copied</span>}
                      </span>
                    ))}
                  </div>
                )}
              </div>
            ))}
          </div>
        )}
      </div>
    </div>
  );
}
