import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";

// Unit proof for the mobile /m terminal mirror's Local Files link provider (Phase 3, hardened in
// Phase 4). The mirror (stream.ts) is the read-only sibling of the interactive Cockpit terminal
// (interactive.ts) and shares the SAME link contract: a clicked FILE path routes out to the app's
// onFileLink callback (its viewer), while an http/https URL opens in a new tab and is NEVER routed to
// the app. These tests drive the registered xterm link provider directly - xterm, the Gateway client,
// and WebSocket are faked - so the routing and the column ranges are asserted without a browser.

// ----- fakes for xterm + the Gateway client (hoisted so vi.mock factories may reference them) -----
const hoisted = vi.hoisted(() => {
  const ensureGatewayCookie = vi.fn();
  // The minimum of the xterm.js surface the mirror touches during start() + the link provider path.
  class FakeTerminal {
    // getLine backs the link provider's per-line lookup; lineText is buffer row 1's rendered text.
    lineText = "";
    buffer = {
      active: {
        viewportY: 0,
        baseY: 0,
        getLine: (y: number) =>
          y === 0 && this.lineText
            ? { translateToString: (_trim?: boolean) => this.lineText }
            : null,
      },
    };
    linkProvider: unknown = null;
    linkProviderDisposed = false;
    writes: string[] = [];
    resetCount = 0;
    disposed = false;
    constructor(public options: unknown) {
      terminals.push(this);
    }
    open(): void {}
    registerLinkProvider(provider: unknown): { dispose: () => void } {
      this.linkProvider = provider;
      return { dispose: () => { this.linkProviderDisposed = true; } };
    }
    write(data: unknown): void {
      if (typeof data === "string") this.writes.push(data);
    }
    reset(): void {
      this.resetCount += 1;
    }
    resize(): void {}
    dispose(): void {
      this.disposed = true;
    }
  }
  const terminals: FakeTerminal[] = [];
  return { FakeTerminal, terminals, ensureGatewayCookie };
});

const ensureGatewayCookie = hoisted.ensureGatewayCookie;
// window.open is used by the link provider to open URLs in a new tab (never routed to the app).
const windowOpen = vi.fn();

vi.mock("@xterm/xterm", () => ({ Terminal: hoisted.FakeTerminal }));
vi.mock("../api/client", () => ({
  ensureGatewayCookie: () => hoisted.ensureGatewayCookie(),
}));

// ----- fake WebSocket (the mirror opens one on connect; the tests never drive it) ---------------
class FakeWebSocket {
  static instances: FakeWebSocket[] = [];
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  binaryType = "";
  readyState = 0;
  onmessage: ((ev: { data: unknown }) => void) | null = null;
  onclose: (() => void) | null = null;
  closed = false;
  constructor(public url: string) {
    FakeWebSocket.instances.push(this);
  }
  close(): void {
    this.closed = true;
  }
}

// A minimal element that records event listeners (the mirror attaches touch handlers in start()).
function fakeEl(): HTMLElement {
  return {
    clientWidth: 390,
    clientHeight: 700,
    addEventListener: () => {},
    removeEventListener: () => {},
  } as unknown as HTMLElement;
}

// Import AFTER the mocks are registered.
import { TerminalMirror } from "./stream";

const SID = "sess-777";

beforeEach(() => {
  hoisted.terminals.length = 0;
  FakeWebSocket.instances.length = 0;
  ensureGatewayCookie.mockClear();
  windowOpen.mockReset();
  // The mirror touches only these members of window during start()/connect(). ResizeObserver is left
  // undefined so the observer branch is skipped (nothing to measure in these routing tests).
  (globalThis as unknown as { window: unknown }).window = {
    location: { protocol: "https:", host: "gateway.local" },
    open: (...args: unknown[]) => windowOpen(...args),
    setTimeout: (fn: () => void, ms: number) => globalThis.setTimeout(fn, ms),
    clearTimeout: (id: unknown) => globalThis.clearTimeout(id as ReturnType<typeof setTimeout>),
    requestAnimationFrame: (fn: () => void) => globalThis.setTimeout(fn, 0),
    ResizeObserver: undefined,
  };
  (globalThis as unknown as { WebSocket: unknown }).WebSocket = FakeWebSocket;
});

afterEach(() => {
  delete (globalThis as unknown as { window?: unknown }).window;
  delete (globalThis as unknown as { WebSocket?: unknown }).WebSocket;
});

describe("TerminalMirror link provider (Local Files, Phase 3/4)", () => {
  type XLink = {
    text: string;
    range: { start: { x: number; y: number }; end: { x: number; y: number } };
    activate: (event: MouseEvent) => void;
  };

  // Build a mirror with an onFileLink callback, set buffer row 1 to `lineText`, drive the registered
  // link provider over that row, and return the xterm links plus the callback spy.
  function linksFor(lineText: string): { onFileLink: ReturnType<typeof vi.fn>; links: XLink[] } {
    const onFileLink = vi.fn();
    const mirror = new TerminalMirror(fakeEl(), fakeEl(), SID, () => {}, onFileLink);
    mirror.start();
    const term = hoisted.terminals[0];
    term.lineText = lineText;
    const provider = term.linkProvider as {
      provideLinks: (bufferLineNumber: number, cb: (links: XLink[] | undefined) => void) => void;
    };
    let links: XLink[] | undefined;
    provider.provideLinks(1, (l) => { links = l; }); // row 1 -> getLine(0) -> lineText
    return { onFileLink, links: links ?? [] };
  }

  it("routes a clicked FILE path to onFileLink and does NOT open a browser tab", () => {
    const line = "saved D:\\out\\report.pdf ok";
    const { onFileLink, links } = linksFor(line);
    const fileLink = links.find((l) => l.text === "D:\\out\\report.pdf");
    expect(fileLink).toBeDefined();
    fileLink!.activate({} as MouseEvent);
    expect(onFileLink).toHaveBeenCalledWith("D:\\out\\report.pdf");
    expect(windowOpen).not.toHaveBeenCalled();
  });

  it("opens a clicked http/https URL in a new tab and does NOT route it to onFileLink", () => {
    const line = "open https://example.com/report for details";
    const { onFileLink, links } = linksFor(line);
    const urlLink = links.find((l) => l.text === "https://example.com/report");
    expect(urlLink).toBeDefined();
    urlLink!.activate({} as MouseEvent);
    expect(windowOpen).toHaveBeenCalledWith("https://example.com/report", "_blank", "noopener,noreferrer");
    expect(onFileLink).not.toHaveBeenCalled();
  });

  it("gives each link an xterm buffer range that lines up with the on-screen columns", () => {
    const line = "C:\\a\\b.png and http://host/x";
    const { links } = linksFor(line);
    expect(links).toHaveLength(2);
    for (const link of links) {
      expect(link.range.start.y).toBe(1);
      expect(link.range.end.y).toBe(1);
      expect(line.slice(link.range.start.x - 1, link.range.end.x)).toBe(link.text);
    }
  });

  it("returns no links for a line with none, so xterm leaves the line undecorated", () => {
    const { links } = linksFor("plain output, nothing clickable");
    expect(links).toEqual([]);
  });
});
