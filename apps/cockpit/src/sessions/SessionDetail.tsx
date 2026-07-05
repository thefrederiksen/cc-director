import { useCallback, useEffect, useState } from "react";
import { useOutletContext, useParams } from "react-router-dom";
import { getQueue, type QueueItem } from "@devthrottle/client-core/api/client";
import { TerminalPane } from "../panes/TerminalPane";
import type { SessionsOutletContext } from "./SessionsView";
import { SessionActionBar } from "./SessionActionBar";
import { SessionComposer } from "./SessionComposer";
import { QueuePanel } from "./QueuePanel";
import { ScreenshotsPanel } from "./ScreenshotsPanel";

// The selected session's detail region (issue #972): the live terminal (issue #971's TerminalPane,
// reused verbatim) stacked over the driver action bar and the composer, with a tabbed dock for the
// prompt queue and the screenshots gallery. This is the "answer it" half of the driving loop - it is
// mounted only on /session/{sid}, so selecting a different session remounts it (the terminal engine,
// the composer text, and the queue are all per-session).

type DockTab = "queue" | "shots";

export function SessionDetail() {
  const { sessionId } = useParams<{ sessionId: string }>();
  const { sessions } = useOutletContext<SessionsOutletContext>();
  const selected = sessions?.find((s) => s.sessionId === sessionId);

  const [compose, setCompose] = useState("");
  const [queue, setQueue] = useState<QueueItem[]>([]);
  const [tab, setTab] = useState<DockTab>("queue");

  // Load the queue for the selected session (and reset the composer) whenever the session changes, so
  // the composer and the queue never carry over from a previously-selected session.
  useEffect(() => {
    if (!sessionId) return;
    const controller = new AbortController();
    setCompose("");
    setQueue([]);
    getQueue(sessionId, controller.signal)
      .then(setQueue)
      .catch(() => {
        /* the queue panel and composer surface their own action errors; an empty initial load is fine */
      });
    return () => controller.abort();
  }, [sessionId]);

  // Append text (a popped queue item, or a screenshot path) into the composer, separated by a space.
  const appendToCompose = useCallback((text: string) => {
    setCompose((cur) => (cur.length > 0 && !cur.endsWith(" ") ? `${cur} ${text}` : `${cur}${text}`));
  }, []);

  return (
    <div className="session-detail">
      <div className="session-main">
        <TerminalPane />
        <SessionActionBar sessionId={sessionId} capabilities={selected?.driverCapabilities} />
        <SessionComposer sessionId={sessionId} value={compose} onChange={setCompose} onQueued={setQueue} />
      </div>

      <aside className="session-dock">
        <div className="dock-tabs">
          <button type="button" className={`dock-tab ${tab === "queue" ? "on" : ""}`} onClick={() => setTab("queue")}>
            Queue{queue.length > 0 ? ` (${queue.length})` : ""}
          </button>
          <button type="button" className={`dock-tab ${tab === "shots" ? "on" : ""}`} onClick={() => setTab("shots")}>
            Screenshots
          </button>
        </div>
        <div className="dock-body">
          {tab === "queue" ? (
            <QueuePanel sessionId={sessionId} queue={queue} onQueue={setQueue} onPop={appendToCompose} />
          ) : (
            <ScreenshotsPanel sessionId={sessionId} onInsert={appendToCompose} />
          )}
        </div>
      </aside>
    </div>
  );
}
