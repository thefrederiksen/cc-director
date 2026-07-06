import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";

// Unit proof for the interactive desktop terminal engine (issue #971). It exercises the hard-won
// rules the pane must never regress, WITHOUT a browser: xterm, the Gateway client, and WebSocket are
// all faked so each rule is asserted deterministically.
//
//   1. Typing forwards RAW bytes to the PTY with appendEnter:false (control keys included).
//   2. A "size" message resizes the grid to the PTY's EXACT cols and rows.
//   3. A {"type":"closed"} control frame surfaces the reason and does NOT reset the reconnect streak.
//   4. The first live byte resets the reconnect streak (markLive).
//   5. Reconnect is BOUNDED - it gives up with a visible status line after the attempt cap.
//   6. dispose() stops reconnecting and tears the terminal down.
//   7. The xterm lifecycle is animation-frame safe (issue #1029): open() is deferred until the host is
//      laid out, and dispose() is deferred one frame so xterm's own queued syncScrollArea frame never
//      runs against a torn-down render service (the uncaught 'dimensions' TypeError).

// ----- a drivable requestAnimationFrame queue --------------------------------------------------
// The engine schedules the terminal bring-up and teardown on requestAnimationFrame (issue #1029), and
// xterm itself (modelled by FakeTerminal below) queues an internal frame on reset()/open(). The tests
// drive these frames explicitly with runFrames() so the exact "frame still pending at dispose" race is
// reproducible. Callbacks run in registration order within a round; a callback that schedules another
// frame runs in the NEXT round (as a real browser would run it on the next paint).
let rafCallbacks: Map<number, () => void> = new Map();
let rafSeq = 0;

function runFrames(rounds = 20): void {
  for (let r = 0; r < rounds && rafCallbacks.size > 0; r++) {
    const batch = [...rafCallbacks.entries()]; // Map preserves insertion (registration) order
    rafCallbacks.clear();
    for (const [, cb] of batch) cb();
  }
}

// ----- fakes for xterm + the Gateway client (hoisted so vi.mock factories may reference them) -----
const hoisted = vi.hoisted(() => {
  const sendPrompt = vi.fn((_sid: string, _text: string, _appendEnter: boolean) => Promise.resolve());
  const ensureGatewayCookie = vi.fn();
  // A faithful model of the xterm.js 5.5.0 lifecycle contract that matters for issue #1029:
  //  - Viewport.reset() and Terminal.open() each queue an internal requestAnimationFrame(syncScrollArea)
  //    whose handle xterm does NOT retain (so it cannot be cancelled).
  //  - syncScrollArea reads the render service's `dimensions` (xterm: `this._renderer.value.dimensions`).
  //  - dispose() tears the render service down, so a frame that fires AFTER dispose reads `dimensions`
  //    off `undefined` and throws `TypeError: Cannot read properties of undefined (reading 'dimensions')`.
  // The engine's fix must ensure that queued frame always flushes on a still-live terminal.
  class FakeTerminal {
    onDataCb: ((data: string) => void) | null = null;
    writes: string[] = [];
    cols = 80;
    rows = 24;
    resizes: Array<{ cols: number; rows: number }> = [];
    resetCount = 0;
    scrollToBottomCount = 0;
    disposed = false;
    buffer = { active: { viewportY: 0, baseY: 0 } };
    // The render service, torn down on dispose. `dimensions` reads through it, mirroring xterm's
    // `get dimensions(){return this._renderer.value.dimensions}` - so it throws once disposed.
    private renderValue: { dimensions: object } | undefined = { dimensions: {} };
    constructor(public options: unknown) {
      terminals.push(this);
    }
    private get dimensions(): object {
      return (this.renderValue as { dimensions: object }).dimensions;
    }
    // Mirrors xterm Viewport.syncScrollArea reading the render service dimensions.
    private syncScrollArea(): void {
      void this.dimensions;
    }
    open(): void {
      // xterm's Terminal.open() queues a syncScrollArea frame it does not retain the handle for.
      window.requestAnimationFrame(() => this.syncScrollArea());
    }
    onData(cb: (data: string) => void): void {
      this.onDataCb = cb;
    }
    write(data: unknown): void {
      if (typeof data === "string") this.writes.push(data);
    }
    reset(): void {
      this.resetCount += 1;
      // xterm's Viewport.reset() queues an un-cancellable syncScrollArea frame - the #1029 hazard.
      window.requestAnimationFrame(() => this.syncScrollArea());
    }
    resize(cols: number, rows: number): void {
      this.cols = cols;
      this.rows = rows;
      this.resizes.push({ cols, rows });
    }
    scrollToBottom(): void {
      this.scrollToBottomCount += 1;
    }
    dispose(): void {
      this.disposed = true;
      this.renderValue = undefined; // render service torn down -> `dimensions` now throws
    }
  }
  const terminals: FakeTerminal[] = [];
  return { FakeTerminal, terminals, sendPrompt, ensureGatewayCookie };
});

const sendPrompt = hoisted.sendPrompt;
const ensureGatewayCookie = hoisted.ensureGatewayCookie;

vi.mock("@xterm/xterm", () => ({ Terminal: hoisted.FakeTerminal }));
vi.mock("../api/client", () => ({
  sendPrompt: (sid: string, text: string, appendEnter: boolean) => hoisted.sendPrompt(sid, text, appendEnter),
  ensureGatewayCookie: () => hoisted.ensureGatewayCookie(),
}));

// ----- fake WebSocket (drivable) ----------------------------------------------------------------
class FakeWebSocket {
  static instances: FakeWebSocket[] = [];
  binaryType = "";
  onopen: (() => void) | null = null;
  onmessage: ((ev: { data: unknown }) => void) | null = null;
  onclose: ((ev: { code: number }) => void) | null = null;
  onerror: (() => void) | null = null;
  closed = false;
  constructor(public url: string) {
    FakeWebSocket.instances.push(this);
  }
  close(): void {
    this.closed = true;
  }
  // test drivers
  emitString(json: string): void {
    this.onmessage?.({ data: json });
  }
  emitBinary(bytes: Uint8Array): void {
    this.onmessage?.({ data: bytes.buffer });
  }
  triggerClose(code = 1006): void {
    this.onclose?.({ code });
  }
}

// Import AFTER the mocks are registered.
import { InteractiveTerminal } from "./interactive";

const SID = "sess-123";

// A host element that reports a real (non-zero) layout, so the engine's deferred bring-up opens the
// terminal on the first animation frame (issue #1029).
function host(): HTMLElement {
  return { clientWidth: 800, clientHeight: 600 } as unknown as HTMLElement;
}

// Construct, start, and run the bring-up frame so the terminal + first WebSocket exist. Returns the
// engine instance; the FakeTerminal is hoisted.terminals[0] and the socket is FakeWebSocket.instances[0].
function startTerminal(): InteractiveTerminal {
  const t = new InteractiveTerminal(host(), SID);
  t.start();
  runFrames();
  return t;
}

// Drain the microtask queue so the serialized keystroke pump (issue #1021) can advance between
// awaited sends. Under fake timers this still flushes microtasks (Promise.resolve resolves without a
// timer), which is all the default sendPrompt mock needs.
async function flush(times = 8): Promise<void> {
  for (let i = 0; i < times; i++) await Promise.resolve();
}

beforeEach(() => {
  hoisted.terminals.length = 0;
  FakeWebSocket.instances.length = 0;
  rafCallbacks = new Map();
  rafSeq = 0;
  sendPrompt.mockReset();
  sendPrompt.mockImplementation((_sid: string, _text: string, _appendEnter: boolean) => Promise.resolve());
  ensureGatewayCookie.mockClear();
  vi.useFakeTimers();
  // Minimal window/global surface the engine touches. setTimeout/clearTimeout delegate to globalThis
  // at CALL time so vitest's fake timers drive them; requestAnimationFrame/cancelAnimationFrame use the
  // drivable queue above so tests step frames explicitly (issue #1029).
  (globalThis as unknown as { window: unknown }).window = {
    location: { protocol: "http:", host: "gateway.local:8080" },
    setTimeout: (fn: () => void, ms: number) => globalThis.setTimeout(fn, ms),
    clearTimeout: (id: unknown) => globalThis.clearTimeout(id as ReturnType<typeof setTimeout>),
    requestAnimationFrame: (fn: () => void) => {
      const id = ++rafSeq;
      rafCallbacks.set(id, fn);
      return id;
    },
    cancelAnimationFrame: (id: number) => {
      rafCallbacks.delete(id);
    },
  };
  (globalThis as unknown as { WebSocket: unknown }).WebSocket = FakeWebSocket;
});

afterEach(() => {
  vi.useRealTimers();
});

function lastStatus(term: (typeof hoisted.terminals)[number]): string {
  // The most recent dim status line ("\r\n\x1b[2m[...]\x1b[0m\r\n").
  for (let i = term.writes.length - 1; i >= 0; i--) {
    const m = term.writes[i].match(/\[([^\]]+)\]/);
    if (m) return m[1];
  }
  return "";
}

// Advance one reconnect cycle: fire the current socket's close, then let the reconnect timer run so a
// fresh socket is opened. Returns the newly opened socket.
function reconnectOnce(): FakeWebSocket {
  const before = FakeWebSocket.instances.length;
  FakeWebSocket.instances[before - 1].triggerClose();
  vi.advanceTimersByTime(1300);
  return FakeWebSocket.instances[FakeWebSocket.instances.length - 1];
}

describe("InteractiveTerminal typing", () => {
  it("forwards a keystroke as raw bytes with appendEnter:false", () => {
    startTerminal();
    const term = hoisted.terminals[0];
    term.onDataCb?.("a");
    expect(sendPrompt).toHaveBeenCalledWith(SID, "a", false);
  });

  it("forwards control keys (Esc, Ctrl+C) verbatim and in order", async () => {
    startTerminal();
    const term = hoisted.terminals[0];
    term.onDataCb?.("\x1b"); // Esc
    term.onDataCb?.("\x03"); // Ctrl+C
    // Sends are serialized (issue #1021): the first POST fires immediately, the second only after the
    // first resolves, so flush the pump before asserting both landed in order.
    await flush();
    expect(sendPrompt).toHaveBeenNthCalledWith(1, SID, "\x1b", false);
    expect(sendPrompt).toHaveBeenNthCalledWith(2, SID, "\x03", false);
  });

  it("ignores an empty keystroke", () => {
    startTerminal();
    hoisted.terminals[0].onDataCb?.("");
    expect(sendPrompt).not.toHaveBeenCalled();
  });
});

describe("InteractiveTerminal keystroke ordering (issue #1021)", () => {
  // Model the Director as it really behaves under concurrent POSTs: it applies each POST body to the
  // PTY when that POST COMPLETES (arrives), and here every POST is given an adversarial latency so an
  // earlier-dispatched POST finishes LAST. The OLD fire-and-forget code dispatched one unawaited POST
  // per keystroke, so all POSTs were in flight at once and completion order != typed order - the PTY
  // saw reordered/dropped bytes. The serialized pump must make the applied order equal the typed order
  // regardless of latency, and must never have more than one POST in flight.
  function installReorderingDirector(): { applied: string[]; readonly maxInFlight: number } {
    const applied: string[] = []; // bytes as the PTY receives them, in completion (arrival) order
    let dispatched = 0;
    let inFlight = 0;
    let maxInFlight = 0;
    sendPrompt.mockImplementation((_sid: string, text: string) => {
      const n = dispatched++;
      inFlight += 1;
      if (inFlight > maxInFlight) maxInFlight = inFlight;
      // Earlier POSTs get LONGER latency: if two are ever in flight together, the later one lands
      // first - exactly the race that corrupted fast typing before the fix.
      const delay = 100 - n;
      return new Promise<void>((resolve) => {
        globalThis.setTimeout(() => {
          applied.push(text);
          inFlight -= 1;
          resolve();
        }, delay);
      });
    });
    return {
      applied,
      get maxInFlight() {
        return maxInFlight;
      },
    };
  }

  it("keeps a fast-typed 36-char string exactly in order at 0ms/key", async () => {
    const input = "abcdefghijklmnopqrstuvwxyz0123456789";
    startTerminal();
    const term = hoisted.terminals[0];
    const director = installReorderingDirector();

    // Type every character in the same tick (0ms/key) - xterm fires onData once per keystroke.
    for (const ch of input) term.onDataCb?.(ch);
    await vi.runAllTimersAsync();

    // Every byte reached the PTY, none dropped or reordered. (With the old fire-and-forget code the
    // adversarial latency reverses the order, so this would read "9876...a".)
    expect(director.applied.join("")).toBe(input);
    // Serialization is the mechanism: never more than one POST in flight at a time (the old code would
    // have had all 36 in flight together).
    expect(director.maxInFlight).toBe(1);
  });

  it("preserves order for a burst mixing typed text and control keys", async () => {
    // "ec" + Ctrl+C + "ho" + Esc + arrow-up + "x", all in one tick.
    const seq = ["e", "c", "\x03", "h", "o", "\x1b", "\x1b[A", "x"];
    startTerminal();
    const term = hoisted.terminals[0];
    const director = installReorderingDirector();

    for (const s of seq) term.onDataCb?.(s);
    await vi.runAllTimersAsync();

    expect(director.applied.join("")).toBe(seq.join(""));
    expect(director.maxInFlight).toBe(1);
  });
});

describe("InteractiveTerminal rendering", () => {
  it("opens the stream same-origin to the Gateway (root-relative path, no Director address)", () => {
    startTerminal();
    expect(FakeWebSocket.instances[0].url).toBe("ws://gateway.local:8080/sessions/sess-123/stream");
  });

  it("mirrors the PTY grid exactly - both cols and rows from the size message", () => {
    startTerminal();
    const term = hoisted.terminals[0];
    FakeWebSocket.instances[0].emitString(JSON.stringify({ type: "size", cols: 137, rows: 51 }));
    expect(term.resizes[term.resizes.length - 1]).toEqual({ cols: 137, rows: 51 });
  });
});

describe("InteractiveTerminal closed control frame", () => {
  it("surfaces the reason and does NOT reset the reconnect streak", () => {
    startTerminal();
    const term = hoisted.terminals[0];

    // Two failed reconnect cycles -> the streak is at 2 (next attempt would be #3).
    reconnectOnce();
    reconnectOnce();
    expect(lastStatus(term)).toContain("attempt 3");

    // A "closed" control frame arrives. It must write the reason and must NOT reset the streak.
    FakeWebSocket.instances[FakeWebSocket.instances.length - 1].emitString(JSON.stringify({ type: "closed", reason: "boom" }));
    expect(term.writes.some((w) => w.includes("[stream closed: boom]"))).toBe(true);

    // The next close continues the streak from 3 (-> announces attempt 4). If the closed frame had
    // reset the streak, this would read "attempt 2" instead.
    reconnectOnce();
    expect(lastStatus(term)).toContain("attempt 4");
  });
});

describe("InteractiveTerminal reconnect", () => {
  it("the first live byte resets the reconnect streak", () => {
    startTerminal();
    const term = hoisted.terminals[0];

    reconnectOnce();
    reconnectOnce();
    expect(lastStatus(term)).toContain("attempt 3");

    // A real PTY byte proves the path is live -> the streak resets, so the NEXT drop announces
    // "attempt 2" (0 -> incremented once by the following close).
    FakeWebSocket.instances[FakeWebSocket.instances.length - 1].emitBinary(new Uint8Array([0x68, 0x69]));
    reconnectOnce();
    expect(lastStatus(term)).toContain("attempt 2");
  });

  it("is bounded - gives up with a visible status line after the attempt cap", () => {
    startTerminal();
    const term = hoisted.terminals[0];

    // Drive well past the 30-attempt cap; each cycle closes then lets the timer reopen.
    for (let i = 0; i < 35; i++) {
      FakeWebSocket.instances[FakeWebSocket.instances.length - 1].triggerClose();
      vi.advanceTimersByTime(1300);
    }
    expect(lastStatus(term)).toContain("gave up after 30 attempts");
  });
});

describe("InteractiveTerminal dispose", () => {
  it("stops reconnecting and disposes the terminal", () => {
    const t = startTerminal();
    const term = hoisted.terminals[0];
    const socketsBefore = FakeWebSocket.instances.length;

    t.dispose();
    // The xterm teardown is deferred one frame (issue #1029); run it so the terminal is disposed.
    runFrames();
    expect(term.disposed).toBe(true);

    // A close after dispose must NOT schedule a reconnect (handler detached), so no new socket opens.
    FakeWebSocket.instances[socketsBefore - 1].triggerClose();
    vi.advanceTimersByTime(5000);
    expect(FakeWebSocket.instances.length).toBe(socketsBefore);
  });
});

describe("InteractiveTerminal xterm lifecycle safety (issue #1029)", () => {
  // The bug: xterm's reset()/open() queue an internal animation frame (syncScrollArea) that reads the
  // render service's `dimensions`. If the terminal is disposed while that frame is still pending, the
  // frame later runs against a torn-down render service and throws the uncaught
  // `TypeError: Cannot read properties of undefined (reading 'dimensions')`. This fires on every mount
  // and on every rapid session switch. The fix defers the teardown one frame so the queued frame
  // always flushes on a still-live terminal.

  it("does NOT throw when disposed while an xterm animation frame is still pending", () => {
    const t = startTerminal();
    const term = hoisted.terminals[0];
    expect(term).toBeDefined();

    // Queue a fresh xterm frame - exactly what reset()/markLive do on every reconnect and first byte -
    // then dispose while it is STILL pending. This is the reproduced race.
    term.reset();
    t.dispose();

    // Running the pending frames must not throw: the fix guarantees xterm's queued syncScrollArea runs
    // BEFORE the deferred teardown, i.e. on a live terminal. With the old synchronous dispose the
    // terminal was already torn down here, so the frame read `dimensions` off undefined and threw.
    expect(() => runFrames()).not.toThrow();
    expect(term.disposed).toBe(true);
  });

  it("survives a rapid mount / dispose / remount storm without throwing (StrictMode double-mount + A/B switching)", () => {
    // Six back-to-back lifecycles in the same tick, mimicking React 18 StrictMode's throwaway
    // double-mount and rapid A/B session switching. Each cycle opens (queuing xterm frames) and is torn
    // down before its frames flush. None may throw when the frames finally run.
    expect(() => {
      for (let i = 0; i < 6; i++) {
        const t = new InteractiveTerminal(host(), SID);
        t.start();
        runFrames(); // bring up + open/reset frames
        hoisted.terminals[hoisted.terminals.length - 1].reset(); // queue a fresh frame
        t.dispose(); // dispose with that frame pending
      }
      runFrames();
    }).not.toThrow();
  });

  it("never opens a terminal if disposed before the host is laid out (StrictMode throwaway mount)", () => {
    // A host that never gains a size, then disposed before any bring-up frame that opens it. No xterm
    // terminal must ever be constructed - the pending bring-up frame is cancelled on dispose.
    const zeroHost = { clientWidth: 0, clientHeight: 0 } as unknown as HTMLElement;
    const t = new InteractiveTerminal(zeroHost, SID);
    t.start();
    t.dispose(); // cancels the pending bring-up before it ever opens
    runFrames();
    expect(hoisted.terminals.length).toBe(0);
  });
});
