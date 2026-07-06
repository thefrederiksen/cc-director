import { useCallback, useEffect, useRef, useState } from "react";
import {
  sendClearContext,
  sendEscape,
  sendHistoryPicker,
  sendInterrupt,
  gatewayErrorMessage,
} from "@devthrottle/client-core/api/client";

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
          onClick={() => sessionId && void act(() => sendClearContext(sessionId), "context cleared", "Clear context failed")}
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
    </div>
  );
}
