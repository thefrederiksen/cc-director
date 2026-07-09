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
// success it shows a brief "Sent" that clears itself. A held send (saved and still being delivered on a
// bad connection) shows a calm amber strip with the honest reason and an "Upload now" control that kicks
// a waiting or throttled retry to full speed - it is not a failure, so it offers no Dismiss. A PARKED send
// (a permanent failure that stopped the auto-loop, issue #1184) shows the saved-and-retryable message with
// an explicit "Retry" control - the audio is safe, delivery just no longer loops on its own. A genuine
// failure (durable storage unavailable, so nothing could be queued) shows a red alert with Dismiss; it
// does NOT disappear on its own, so it can never be missed.

const DONE_AUTOCLEAR_MS = 2500;

export function DictationStatusStrip({ sessionId }: { sessionId: string | undefined }) {
  const status = useDictationStatusFor(sessionId);
  const [uploadingNow, setUploadingNow] = useState(false);

  // A successful send is acknowledged briefly, then clears itself so the strip does not linger.
  useEffect(() => {
    if (status?.phase !== "done") return;
    const uploadId = status.uploadId;
    const t = window.setTimeout(() => clearDictationStatus(uploadId), DONE_AUTOCLEAR_MS);
    return () => window.clearTimeout(t);
  }, [status?.phase, status?.uploadId]);

  if (!status) return null;

  if (status.phase === "held") {
    const onUploadNow = async () => {
      setUploadingNow(true);
      try {
        await retryPendingDictation(status.uploadId);
      } finally {
        setUploadingNow(false);
      }
    };
    return (
      <div className="dictate-strip dictate-strip-held" role="status">
        <span className="dictate-strip-icon" aria-hidden="true">!</span>
        <span className="dictate-strip-text">{status.error ?? "Saved - still trying to send your recording..."}</span>
        <button type="button" className="dictate-strip-btn" onClick={() => void onUploadNow()} disabled={uploadingNow}>
          {uploadingNow ? "Uploading..." : "Upload now"}
        </button>
      </div>
    );
  }

  if (status.phase === "parked") {
    const onRetry = async () => {
      setUploadingNow(true);
      try {
        await retryPendingDictation(status.uploadId);
      } finally {
        setUploadingNow(false);
      }
    };
    return (
      <div className="dictate-strip dictate-strip-parked" role="status">
        <span className="dictate-strip-icon" aria-hidden="true">!</span>
        <span className="dictate-strip-text">{status.error ?? "Saved on your device - you can retry it."}</span>
        <button type="button" className="dictate-strip-btn" onClick={() => void onRetry()} disabled={uploadingNow}>
          {uploadingNow ? "Retrying..." : "Retry"}
        </button>
      </div>
    );
  }

  if (status.phase === "failed") {
    return (
      <div className="dictate-strip dictate-strip-failed" role="alert">
        <span className="dictate-strip-icon" aria-hidden="true">!</span>
        <span className="dictate-strip-text">{status.error ?? "Dictation failed."}</span>
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
