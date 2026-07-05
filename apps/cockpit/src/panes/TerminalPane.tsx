import { useEffect, useRef } from "react";
import { useParams } from "react-router-dom";
import "@xterm/xterm/css/xterm.css";
import { InteractiveTerminal } from "@devthrottle/client-core/terminal/interactive";

// The live, interactive terminal pane for the React desktop Cockpit (issue #971) - the load-bearing
// pane and the reason for the whole rebuild. A one-to-one port of the Blazor Cockpit's
// TerminalPane.razor + cockpit-terminal.js, now driven entirely client-side:
//
//   * Output streams over a same-origin WebSocket to the Gateway (/sessions/{sid}/stream), which
//     reverse-proxies to the owning Director. The token rides as the cc-gateway-token cookie.
//   * Input is TYPEABLE: every keystroke (Esc, Ctrl+C, arrows, the slash-command interface) is
//     forwarded by a DIRECT REST call to the Gateway (POST /sessions/{sid}/prompt, appendEnter:false)
//     inside InteractiveTerminal - NOT through any server render channel, so a degraded OUTPUT stream
//     never blocks typing (the whole point of leaving Blazor's SignalR circuit).
//   * The engine mirrors the PTY grid exactly, uses the DOM renderer, and does bounded reconnect with
//     a visible status line and {"type":"closed"} handling (all in client-core/terminal/interactive).
//
// This pane serves EXACTLY ONE session. It is keyed by sessionId (via the route param + the effect
// dependency), so selecting a different session tears down this InteractiveTerminal and constructs a
// fresh one - the engine never switches sessions in place.

export function TerminalPane() {
  const { sessionId } = useParams<{ sessionId: string }>();
  const hostRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!sessionId || hostRef.current === null) return;
    const terminal = new InteractiveTerminal(hostRef.current, sessionId);
    terminal.start();
    return () => terminal.dispose();
  }, [sessionId]);

  if (!sessionId) {
    return (
      <section className="pane">
        <h1 className="pane-title">Terminal</h1>
        <p className="pane-note">Select a session to open its live terminal.</p>
      </section>
    );
  }

  return (
    <div className="term-screen">
      <div className="term-bar">
        <span className="term-bar-title">Terminal</span>
        <span className="term-bar-sid" title={sessionId}>{sessionId}</span>
      </div>
      {/* The terminal fills all remaining space. .term-host is the scroll container; the exact-size
          grid is anchored to the bottom so a too-tall PTY shows its live input box in view. Keyed by
          sessionId so React remounts the whole pane (and the engine) when the session changes. */}
      <div className="term-host" ref={hostRef} key={sessionId} />
    </div>
  );
}
