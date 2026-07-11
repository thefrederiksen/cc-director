import { useCallback, useEffect, useRef, useState } from "react";
import {
  sendClearContext,
  sendEscape,
  sendHistoryPicker,
  sendInterrupt,
  gatewayErrorMessage,
} from "@devthrottle/client-core/api/client";
import { ConfirmDialog } from "../components";

// The driver action bar (issue #972) - the React port of the Blazor Cockpit action bar / desktop
// SessionActionBar. Each button is rendered from the SELECTED session's declared driver capabilities
// (verbatim - a verb the tool lacks is simply absent, never guessed), and acts on the session through
// the shared Gateway client:
//
//   Stop (Cancel cap)          -> POST /sessions/{sid}/escape    (the driver's soft cancel - Esc)
//   Interrupt (Interrupt cap)  -> POST /sessions/{sid}/interrupt (hard Ctrl+C, stronger than Stop)
//   Clear context (ClearContext cap) -> POST /sessions/{sid}/clear-context (/clear in place)
//   History (History cap)      -> POST /sessions/{sid}/history-picker (the in-terminal history picker)

export interface SessionActionBarProps {
  sessionId: string | undefined;
  /** The selected session's SessionDto.driverCapabilities; a button shows only if its verb is listed. */
  capabilities: string[] | undefined;
}

export function SessionActionBar({ sessionId, capabilities }: SessionActionBarProps) {
  const [acting, setActing] = useState(false);
  const [status, setStatus] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  // Clear context resets the session's whole conversation in place; it is destructive, so it asks
  // through the shared ConfirmDialog (issue #1244) instead of firing on the first click.
  const [confirmClear, setConfirmClear] = useState(false);
  const statusTimer = useRef<number | null>(null);

  useEffect(() => {
    return () => {
      if (statusTimer.current !== null) window.clearTimeout(statusTimer.current);
    };
  }, []);

  const flash = useCallback((message: string) => {
    setStatus(message);
    if (statusTimer.current !== null) window.clearTimeout(statusTimer.current);
    statusTimer.current = window.setTimeout(() => setStatus(null), 5000);
  }, []);

  const act = useCallback(
    async (verb: () => Promise<void>, done: string, failed: string) => {
      if (!sessionId || acting) return;
      setActing(true);
      setError(null);
      try {
        await verb();
        flash(done);
      } catch (err) {
        setError(err instanceof Error ? gatewayErrorMessage(err) : failed);
      } finally {
        setActing(false);
      }
    },
    [sessionId, acting, flash],
  );

  const has = (cap: string) => capabilities?.includes(cap) === true;

  // A cold deep link into a session (the roster has not arrived yet, so the selected session and its
  // declared capabilities are still undefined) used to render an empty button row. Show a small loading
  // state instead until the capabilities resolve (issue #1247). An empty array - a session that loaded
  // and genuinely declares no driver verbs - is NOT loading, so it correctly renders no buttons.
  if (capabilities === undefined) {
    return (
      <div className="action-bar">
        <span className="action-loading">Loading session...</span>
      </div>
    );
  }

  return (
    <div className="action-bar">
      {has("Cancel") && (
        <button
          type="button"
          className="act-btn act-stop"
          disabled={acting}
          onClick={() => sessionId && void act(() => sendEscape(sessionId), "turn stopped", "Stop failed")}
          title="Stop the current turn (the driver's soft cancel - Esc)"
        >
          Stop
        </button>
      )}
      {has("Interrupt") && (
        <button
          type="button"
          className="act-btn"
          disabled={acting}
          onClick={() => sessionId && void act(() => sendInterrupt(sessionId), "interrupted", "Interrupt failed")}
          title="Hard interrupt (Ctrl+C) - stronger than Stop"
        >
          Interrupt
        </button>
      )}
      {has("ClearContext") && (
        <button
          type="button"
          className="act-btn"
          disabled={acting}
          onClick={() => setConfirmClear(true)}
          title="Reset the conversation in place (/clear) - the process keeps running"
        >
          Clear context
        </button>
      )}
      {has("History") && (
        <button
          type="button"
          className="act-btn"
          disabled={acting}
          onClick={() => sessionId && void act(() => sendHistoryPicker(sessionId), "history picker opened (Esc closes)", "History failed")}
          title="Open the in-terminal history picker (Claude's double-Esc)"
        >
          History
        </button>
      )}
      {status !== null && <span className="action-status">{status}</span>}
      {error !== null && <span className="action-error">{error}</span>}

      <ConfirmDialog
        open={confirmClear}
        title="Clear this session's context?"
        message={
          "This resets the conversation in place (/clear). The running process keeps going, but the " +
          "agent loses the current conversation. This cannot be undone."
        }
        confirmLabel="Clear context"
        busyLabel="Clearing..."
        onConfirm={async () => {
          if (sessionId === undefined) return;
          // Let a failure throw so the dialog surfaces it (fail loudly); flash on success.
          await sendClearContext(sessionId);
          flash("context cleared");
        }}
        onClose={() => setConfirmClear(false)}
      />
    </div>
  );
}
