// Interactive desktop terminal engine (issue #971). A faithful TypeScript port of the Blazor
// Cockpit's cockpit-terminal.js into the shared client-core terminal package, for the React desktop
// Cockpit (apps/cockpit). It is the INTERACTIVE sibling of the read-only mobile mirror (stream.ts):
// both feed a real xterm.js terminal from the session's raw PTY byte stream over the same WebSocket
// (GET {gateway}/sessions/{sid}/stream, reverse-proxied by the Gateway to the owning Director), but
// this one is TYPEABLE - it forwards every keystroke to the PTY.
//
// Why a byte stream and not buffer polling: a snapshot cannot apply cursor moves, so Claude Code's
// constantly-repainting TUI stacked half-drawn frames as ghost lines. Raw bytes applied in order to
// a terminal emulator cannot desync, so the screen stays coherent.
//
// How typing reaches the PTY (the whole point of leaving Blazor): in the Blazor Cockpit every
// keystroke rode the SignalR circuit through a server-side [JSInvokable] OnInput, so a degraded
// circuit blocked typing. Here xterm's onData forwards the raw bytes DIRECTLY to the Gateway with a
// REST call - POST /sessions/{sid}/prompt with appendEnter:false (sendPrompt) - so input never
// depends on any server render channel and a slow/dropped OUTPUT stream never blocks INPUT. xterm
// does not echo locally; the rendered result comes back over the output stream.
//
// The browser only ever talks same-origin to the Gateway. A browser WebSocket cannot set an
// Authorization header, so the per-machine token rides as the cc-gateway-token cookie
// (ensureGatewayCookie), which the same-origin handshake carries and the Gateway's AuthMiddleware
// accepts - so the terminal works with global Gateway auth on or off.
//
// Rendering decisions (hard-won, do NOT regress - carried over from cockpit-terminal.js):
//  - The grid MIRRORS THE PTY EXACTLY - both cols AND rows, from the server's "size" message. Claude
//    Code's TUI redraws its bottom-docked footer with screen-height-relative cursor moves; if xterm's
//    row count differs from the PTY's, the redraw region drifts and stale footer copies pile up as
//    ghost lines. Never derive rows from the viewport height - that was the cause of the ghosting.
//  - DOM renderer, NOT the canvas addon (the xterm default is the DOM renderer). It uses the
//    platform's native text rasterization (ClearType on Windows), matching the desktop terminal; the
//    canvas addon's greyscale-AA glyph atlas reads blurry next to it at fractional display scaling.
//  - Bounded reconnect WITH a visible status line. A hung/failing socket must not retry forever with
//    no on-screen feedback (indistinguishable from a healthy idle stream / blank pane): every attempt
//    writes a dim status line, and after a run of consecutive failures it gives up and tells the user
//    how to retry. The counter resets the moment real stream data arrives, so a long-lived session
//    that drops occasionally is unaffected.
//  - The {"type":"closed"} control frame is the Gateway reporting WHY the owning Director is
//    unreachable; it is NOT proof of a live stream, so it must not reset the reconnect streak.

import { Terminal as Xterm } from "@xterm/xterm";
import { ensureGatewayCookie, sendPrompt } from "../api/client";

// Match the desktop terminal (TerminalFonts.Family + TerminalControl metrics): Cascadia MONO (not
// Code - no ligatures, crisper glyphs), then the same macOS/Linux fallbacks; 14px with lineHeight 1.2.
const FONT_FAMILY =
  '"Cascadia Mono", Consolas, Menlo, "DejaVu Sans Mono", "Courier New", monospace';
const FONT_SIZE = 14;
const LINE_HEIGHT = 1.2;
const SCROLLBACK = 5000;

const RECONNECT_DELAY_MS = 1200; // ~1200ms between reconnect attempts
const MAX_RECONNECT_ATTEMPTS = 30; // ~36s of dead-leg retries before announcing failure

// The stream URL is ALWAYS same-origin to the Gateway that served this page (never a Director's own
// address - the Gateway resolves the owning Director and reverse-proxies the upgrade). Built from
// window.location so no absolute URL is ever hard-coded (Gateway-only-ingress lint rule, #967). This
// mirrors the same-origin construction in stream.ts; the two engines are intentionally self-contained.
function streamUrl(sessionId: string): string {
  const proto = window.location.protocol === "https:" ? "wss:" : "ws:";
  return `${proto}//${window.location.host}/sessions/${encodeURIComponent(sessionId)}/stream`;
}

/** The gateway host label shown in the status line (the path, not a Director's own address). */
function wsHostOf(url: string): string {
  try {
    return new URL(url).host;
  } catch {
    return url;
  }
}

/**
 * A live, interactive xterm terminal for exactly one session. The React pane constructs one per
 * session (keyed by session id) and disposes it when the selection changes, so this class never has
 * to switch sessions in place.
 */
export class InteractiveTerminal {
  private readonly hostEl: HTMLElement;
  private readonly sessionId: string;

  private term: Xterm | null = null;
  private ws: WebSocket | null = null;
  private reconnectTimer: number | null = null;
  private wantOpen = true;

  private lastCols = 0;
  private lastRows = 0;
  private attempts = 0; // consecutive failed connect attempts; reset on the first live byte
  private gotFirstByte = false;

  constructor(hostEl: HTMLElement, sessionId: string) {
    this.hostEl = hostEl;
    this.sessionId = sessionId;
  }

  start(): void {
    const term = new Xterm({
      fontFamily: FONT_FAMILY,
      fontSize: FONT_SIZE,
      lineHeight: LINE_HEIGHT,
      scrollback: SCROLLBACK,
      cursorBlink: false,
      disableStdin: false, // interactive: keystrokes are forwarded via onData below
      convertEol: false,
      theme: { background: "#1e1e1e", foreground: "#d4d4d8" },
    });
    term.open(this.hostEl);
    this.term = term;

    // Forward every keystroke (raw bytes incl. Esc/Ctrl+C/arrows/the slash-command UI) to the owning
    // Director's PTY via a DIRECT REST call to the Gateway - appendEnter:false writes the bytes
    // verbatim (no submit newline). This is fire-and-forget by design: a keystroke is not a
    // request/response, xterm does not echo locally, and the rendered result returns over the output
    // stream. A failed send is logged (a degraded network) and the user simply retypes; blocking the
    // key handler on the POST would make typing feel laggy. Input does not depend on the OUTPUT
    // stream's health, so a dropped/reconnecting stream never blocks typing (the #971 goal).
    term.onData((data) => {
      if (!data) return;
      void sendPrompt(this.sessionId, data, false).catch((err) => {
        console.debug("[cockpit-terminal] keystroke send failed", this.sessionId, err);
      });
    });

    this.openWs();
  }

  dispose(): void {
    this.wantOpen = false;
    if (this.reconnectTimer !== null) {
      window.clearTimeout(this.reconnectTimer);
      this.reconnectTimer = null;
    }
    if (this.ws) {
      try {
        this.ws.onclose = null; // do not schedule a reconnect from an intentional close
        this.ws.close();
      } catch {
        /* already closing */
      }
      this.ws = null;
    }
    if (this.term) {
      try {
        this.term.dispose();
      } catch {
        /* already disposed */
      }
      this.term = null;
    }
  }

  // Write a dim status line into the terminal. It is wiped by the next term.reset() (on the first
  // byte of a live stream, or on the next reconnect), so it never lingers over real PTY content.
  private statusLine(text: string): void {
    if (!this.term) return;
    try {
      this.term.write("\r\n\x1b[2m[" + text + "]\x1b[0m\r\n");
    } catch {
      /* mid-dispose */
    }
  }

  // Mirror the PTY grid EXACTLY - both cols and rows from the size message. Vertical placement within
  // a too-tall pane is CSS's job (.term-host anchors the grid to the bottom); scrolling history is
  // xterm's. Preserve the viewport's at-bottom position across the resize.
  private fit(): void {
    const t = this.term;
    if (!t || this.lastCols <= 0 || this.lastRows <= 0) return;
    if (t.cols === this.lastCols && t.rows === this.lastRows) return;
    const buf = t.buffer.active;
    const atBottom = buf.viewportY >= buf.baseY;
    try {
      t.resize(this.lastCols, this.lastRows);
    } catch {
      /* transient */
    }
    if (atBottom) {
      try {
        t.scrollToBottom();
      } catch {
        /* transient */
      }
    }
  }

  // The first frame of a LIVE stream (a size header or PTY bytes) proves the whole
  // browser -> Gateway -> Director path is up: wipe the "connecting..." status and clear the failure
  // streak. Replay starts at byte 0, so resetting here loses nothing.
  private markLive(): void {
    if (this.gotFirstByte) return;
    this.gotFirstByte = true;
    this.attempts = 0;
    if (this.term) {
      try {
        this.term.reset();
      } catch {
        /* mid-dispose */
      }
    }
  }

  private openWs(): void {
    if (!this.wantOpen) return;
    const t = this.term;
    if (!t) return;

    // The per-machine token rides as the cc-gateway-token cookie so the same-origin WS handshake
    // authenticates (a browser cannot set an Authorization header on a WebSocket).
    ensureGatewayCookie();

    t.reset(); // each connection replays full history from byte 0
    this.gotFirstByte = false;

    const url = streamUrl(this.sessionId);
    const wsHost = wsHostOf(url);
    // wsHost is the GATEWAY this page was served from, not the owning Director - the Gateway proxies
    // on to the Director. Word it as the path so a loopback Gateway host is never mistaken for the
    // stream's real target.
    this.statusLine(
      this.attempts > 0
        ? "stream lost, reconnecting via gateway " + wsHost + " (attempt " + (this.attempts + 1) + ")..."
        : "connecting via gateway " + wsHost + "...",
    );

    let sock: WebSocket;
    try {
      sock = new WebSocket(url);
    } catch (err) {
      this.statusLine("cannot open stream: " + (err instanceof Error ? err.message : String(err)));
      return;
    }
    sock.binaryType = "arraybuffer";
    this.ws = sock;

    sock.onmessage = (ev: MessageEvent) => {
      if (typeof ev.data === "string") {
        let m: { type?: string; cols?: number; rows?: number; reason?: string };
        try {
          m = JSON.parse(ev.data);
        } catch {
          return;
        }
        // A "closed" control frame is the Gateway reporting WHY the owning Director is unreachable -
        // it is NOT proof of a live stream, so it must not reset the reconnect streak (onclose counts
        // it). Surface the real reason instead of a bare reconnect to the Gateway's own host.
        if (m.type === "closed") {
          try {
            t.write("\r\n[stream closed: " + (m.reason || "") + "]\r\n");
          } catch {
            /* mid-dispose */
          }
          return;
        }
        this.markLive();
        if (m.type === "size" && (m.cols ?? 0) > 0 && (m.rows ?? 0) > 0) {
          this.lastCols = m.cols ?? this.lastCols;
          this.lastRows = m.rows ?? this.lastRows;
          this.fit();
        }
        return;
      }
      this.markLive();
      try {
        t.write(new Uint8Array(ev.data as ArrayBuffer));
      } catch {
        /* mid-dispose */
      }
    };

    sock.onclose = (ev: CloseEvent) => {
      if (this.ws === sock) this.ws = null;
      if (!this.wantOpen || this.reconnectTimer !== null) return;
      this.attempts += 1;
      if (this.attempts > MAX_RECONNECT_ATTEMPTS) {
        this.statusLine(
          "stream via gateway " + wsHost + " is down - gave up after " + MAX_RECONNECT_ATTEMPTS +
            " attempts (last close code " + ev.code + "). Re-select the session to retry.",
        );
        return;
      }
      this.reconnectTimer = window.setTimeout(() => {
        this.reconnectTimer = null;
        if (this.wantOpen) this.openWs();
      }, RECONNECT_DELAY_MS);
    };

    sock.onerror = () => {
      try {
        sock.close();
      } catch {
        /* already closing */
      }
    };
  }
}
