import { useCallback, useEffect, useState } from "react";
import {
  getBriefFeedback,
  type BriefFeedbackItem,
} from "@devthrottle/client-core/feedback/feedbackClient";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";

// The Feedback page (issue #978, epic #967) - the React port of the Blazor Cockpit Feedback.razor. It
// reads the Wingman feedback corpus (brief votes + reasons, issue #207) from GET /turnbriefs/feedback:
// each record is saved on the Gateway with the full brief and a replayable TurnPackage. Read-only, with
// a manual Refresh. Responsive (CodingStyle.md): renders immediately with a loading state and loads
// asynchronously; on a load failure it shows an explicit error (the no-fallback rule).
//
// SCOPE NOTE: this is the Wingman feedback-corpus reader, NOT the desktop "Help > Send Feedback" flow
// that files a GitHub issue with a screenshot on the feedback-assets branch. That GitHub-filing flow is
// CcDirector.Core.Feedback.FeedbackService, a desktop-app feature the Cockpit never served; the
// Cockpit's Feedback page has always been this corpus reader, and this port matches it one-to-one.

// Format the reported timestamp to the compact "yyyy-MM-dd HH:mm" local-time string the Blazor page
// showed. When the value is unparseable, show it verbatim rather than fabricating a time.
function formatTime(iso: string): string {
  if (iso.length === 0) return "";
  const parsed = new Date(iso);
  if (Number.isNaN(parsed.getTime())) return iso;
  const pad = (n: number) => String(n).padStart(2, "0");
  return (
    `${parsed.getFullYear()}-${pad(parsed.getMonth() + 1)}-${pad(parsed.getDate())} ` +
    `${pad(parsed.getHours())}:${pad(parsed.getMinutes())}`
  );
}

function shortId(value: string): string {
  return value.length <= 8 ? value : value.slice(0, 8);
}

export function FeedbackView() {
  const [items, setItems] = useState<BriefFeedbackItem[]>([]);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async (signal?: AbortSignal) => {
    setLoading(true);
    setError(null);
    try {
      const list = await getBriefFeedback(100, signal);
      setItems(list);
    } catch (err) {
      if (signal?.aborted) return;
      setError(gatewayErrorMessage(err));
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  return (
    <div className="page fb">
      <div className="page-head">
        <div className="fb-head-text">
          <h1>Wingman feedback</h1>
          <p className="fb-sub">
            Recent brief votes and reasons. Each record is saved on the Gateway with the full brief and
            replayable TurnPackage.
          </p>
        </div>
        <button className="fb-refresh" onClick={() => void load()} disabled={loading}>
          {loading ? "loading..." : "Refresh"}
        </button>
      </div>

      {error !== null ? (
        <div className="fb-error">{error}</div>
      ) : loading && items.length === 0 ? (
        <div className="fb-loading">Loading feedback...</div>
      ) : items.length === 0 ? (
        <div className="fb-empty">No wingman feedback recorded yet.</div>
      ) : (
        <div className="fb-list">
          {items.map((item) => (
            <article key={item.feedbackId} className={item.vote === "up" ? "fb-card up" : "fb-card down"}>
              <div className="fb-card-head">
                <span className="fb-vote">{item.vote === "up" ? "useful" : "wrong"}</span>
                <span className="fb-model">{item.brainModel}</span>
                <span className="fb-time">{formatTime(item.reportedAtUtc)}</span>
              </div>
              <div className="fb-title">
                {item.briefHeadline.trim().length > 0 ? item.briefHeadline : item.sessionId}
              </div>
              {item.briefRailLine.trim().length > 0 && (
                <div className="fb-rail">rail: {item.briefRailLine}</div>
              )}
              <div className="fb-reason">
                {item.reason.trim().length === 0 ? "No reason yet." : item.reason}
              </div>
              <div className="fb-meta">
                <span>session {shortId(item.sessionId)}</span>
                <span>turn {item.turnNumber}</span>
                <span>{item.hasTurnPackage ? "TurnPackage captured" : "TurnPackage missing"}</span>
                <span>{item.feedbackId}</span>
              </div>
            </article>
          ))}
        </div>
      )}
    </div>
  );
}
