import { useEffect, useState } from "react";
import { retryPendingDictation } from "@devthrottle/client-core/dictation/backgroundSend";
import { clearDictationStatus, useDictationStatusFor } from "@devthrottle/client-core/dictation/status";

// The on-screen live-status strip for a dictation Send, shown on the Terminal, Chat, and Voice
// screens (owner rule after #1139: a dictation must never fail silently, and while the user stays on
// the screen it must show what is happening). It is non-blocking - a thin bar under the header, not a
// modal - so the user can keep working or walk away; the roster badge (Home) carries the same status
// once they leave, because both read the one shared store.
//
// While a send is in flight it shows the live phase (saving -> uploading N of M -> transcribing). On
// success it shows a brief "Sent" that clears itself. On failure it stays put, red, with a plain
// sentence and a Retry (for a held, will-retry failure) plus Dismiss - it does NOT disappear on its
// own, so a failed dictation can never be missed.

const DONE_AUTOCLEAR_MS = 2500;

export function DictationStatusStrip({ sessionId }: { sessionId: string | undefined }) {
  const status = useDictationStatusFor(sessionId);
  const [retrying, setRetrying] = useState(false);

  // A successful send is acknowledged briefly, then clears itself so the strip does not linger.
  useEffect(() => {
    if (status?.phase !== "done") return;
    const uploadId = status.uploadId;
    const t = window.setTimeout(() => clearDictationStatus(uploadId), DONE_AUTOCLEAR_MS);
    return () => window.clearTimeout(t);
  }, [status?.phase, status?.uploadId]);

  if (!status) return null;

  if (status.phase === "failed") {
    const onRetry = async () => {
      setRetrying(true);
      try {
        await retryPendingDictation(status.uploadId);
      } finally {
        setRetrying(false);
      }
    };
    return (
      <div className="dictate-strip dictate-strip-failed" role="alert">
        <span className="dictate-strip-icon" aria-hidden="true">!</span>
        <span className="dictate-strip-text">{status.error ?? "Dictation failed."}</span>
        {status.retryable !== false && (
          <button type="button" className="dictate-strip-btn" onClick={() => void onRetry()} disabled={retrying}>
            {retrying ? "Retrying..." : "Retry"}
          </button>
        )}
        <button type="button" className="dictate-strip-btn dictate-strip-dismiss" onClick={() => clearDictationStatus(status.uploadId)}>
          Dismiss
        </button>
      </div>
    );
  }

  if (status.phase === "done") {
    return (
      <div className="dictate-strip dictate-strip-done" role="status">
        <span className="dictate-strip-icon" aria-hidden="true">+</span>
        <span className="dictate-strip-text">Sent</span>
      </div>
    );
  }

  // In flight: saving / uploading / transcribing.
  return (
    <div className="dictate-strip dictate-strip-busy" role="status">
      <span className="dictate-strip-spin" aria-hidden="true" />
      <span className="dictate-strip-text">{busyLabel(status.phase, status.uploaded, status.total)}</span>
    </div>
  );
}

function busyLabel(phase: string, uploaded?: number, total?: number): string {
  if (phase === "saving") return "Saving your recording...";
  if (phase === "uploading") {
    if (total && total > 1) return `Uploading recording... ${uploaded ?? 0} of ${total}`;
    return "Uploading recording...";
  }
  if (phase === "transcribing") return "Transcribing...";
  return "Working...";
}
