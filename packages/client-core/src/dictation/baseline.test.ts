// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { act, renderHook } from "@testing-library/react";

// Regression coverage for issue #2478: the Gateway's "session moved on" guard requires a
// baselineBufferBytes above zero, and both taught Speak flows omitted it - so the guard never armed.
// This file pins the shared snapshot the flows now take: the roster's terminal-byte position for the
// selected session, STARTED when Speak is pressed and handed to the send pipeline AS A PROMISE (so a
// quick Send waits for the answer instead of racing it), retried once on a transient roster failure,
// and "unknown" (undefined - never a fabricated zero) only when the position is genuinely unknowable.

const { listSessions } = vi.hoisted(() => ({
  listSessions: vi.fn(async (): Promise<{ sessionId?: string; totalBufferBytes?: number | string }[]> => []),
}));
vi.mock("../api/client", () => ({ listSessions }));

import { snapshotBaselineBufferBytes, useDictationBaseline } from "./baseline";

beforeEach(() => {
  vi.clearAllMocks();
});

describe("snapshotBaselineBufferBytes", () => {
  it("returns the selected session's terminal-byte position", async () => {
    listSessions.mockResolvedValueOnce([
      { sessionId: "other", totalBufferBytes: 1 },
      { sessionId: "sess-42", totalBufferBytes: 48213 },
    ]);
    await expect(snapshotBaselineBufferBytes("sess-42")).resolves.toBe(48213);
    expect(listSessions).toHaveBeenCalledTimes(1);
  });

  it("converts the wire's string form of the 64-bit integer", async () => {
    listSessions.mockResolvedValueOnce([{ sessionId: "sess-42", totalBufferBytes: "9007199254740993" }]);
    await expect(snapshotBaselineBufferBytes("sess-42")).resolves.toBe(Number("9007199254740993"));
  });

  it("passes a genuine zero through as a real reading, distinct from unknown", async () => {
    // A session whose terminal has produced nothing yet reads zero. That is an answer, not an absence:
    // it must reach the record as 0, never be blurred into the unknown (undefined) state.
    listSessions.mockResolvedValueOnce([{ sessionId: "sess-42", totalBufferBytes: 0 }]);
    await expect(snapshotBaselineBufferBytes("sess-42")).resolves.toBe(0);
  });

  it("retries ONCE on a transient roster failure and returns the second answer", async () => {
    listSessions
      .mockRejectedValueOnce(new Error("request timed out"))
      .mockResolvedValueOnce([{ sessionId: "sess-42", totalBufferBytes: 555 }]);
    await expect(snapshotBaselineBufferBytes("sess-42")).resolves.toBe(555);
    expect(listSessions).toHaveBeenCalledTimes(2);
  });

  it("returns unknown when the roster fails twice - and never rejects", async () => {
    listSessions
      .mockRejectedValueOnce(new Error("gateway unreachable"))
      .mockRejectedValueOnce(new Error("gateway unreachable"));
    await expect(snapshotBaselineBufferBytes("sess-42")).resolves.toBeUndefined();
    expect(listSessions).toHaveBeenCalledTimes(2);
  });

  it("does NOT retry when the roster answered but does not know the session", async () => {
    // The retry exists for the transient network failure only: a roster that answered without the
    // session (or without its byte position) gave its answer, and asking again cannot change it.
    listSessions.mockResolvedValueOnce([{ sessionId: "other", totalBufferBytes: 5 }]);
    await expect(snapshotBaselineBufferBytes("sess-42")).resolves.toBeUndefined();
    expect(listSessions).toHaveBeenCalledTimes(1);
  });

  it("returns unknown when the session carries no terminal-byte position", async () => {
    listSessions.mockResolvedValueOnce([{ sessionId: "sess-42" }]);
    await expect(snapshotBaselineBufferBytes("sess-42")).resolves.toBeUndefined();
  });
});

describe("useDictationBaseline", () => {
  it("resolves unknown before Speak is pressed, and to the Speak-press snapshot after", async () => {
    listSessions.mockResolvedValue([{ sessionId: "sess-42", totalBufferBytes: 4321 }]);
    const { result } = renderHook(() => useDictationBaseline("sess-42"));

    await expect(result.current.read()).resolves.toBeUndefined();

    act(() => {
      result.current.snapshot();
    });
    await expect(result.current.read()).resolves.toBe(4321);
  });

  it("hands Send the promise of a roster read still in flight, so a quick Send WAITS instead of racing", async () => {
    // The exact race the review found: Speak starts the read, Send arrives before it resolves. The
    // hook must hand over the pending promise (which later resolves to the real position) - never a
    // peek at whatever had resolved by Send time.
    let releaseRoster: (roster: { sessionId: string; totalBufferBytes: number }[]) => void = () => {};
    listSessions.mockImplementationOnce(() => new Promise((resolve) => { releaseRoster = resolve; }));
    const { result } = renderHook(() => useDictationBaseline("sess-42"));

    act(() => {
      result.current.snapshot(); // Speak press: roster read deliberately still in flight
    });
    const handedToSend = result.current.read(); // the quick Send takes the promise now

    releaseRoster([{ sessionId: "sess-42", totalBufferBytes: 777 }]); // the roster answers afterwards
    await expect(handedToSend).resolves.toBe(777);
  });

  it("replaces the previous recording's snapshot on every Speak press", async () => {
    // The first press's roster answer is held back forever; the second press answers immediately. The
    // second recording's Send must get the second answer - a late first answer must be irrelevant.
    listSessions
      .mockImplementationOnce(() => new Promise(() => { /* never answers */ }))
      .mockResolvedValueOnce([{ sessionId: "sess-42", totalBufferBytes: 200 }]);
    const { result } = renderHook(() => useDictationBaseline("sess-42"));

    act(() => {
      result.current.snapshot(); // first Speak press
    });
    act(() => {
      result.current.snapshot(); // second Speak press replaces the stored promise
    });
    await expect(result.current.read()).resolves.toBe(200);
  });

  it("resolves unknown with no session selected, without calling the roster", async () => {
    const { result } = renderHook(() => useDictationBaseline(undefined));
    act(() => {
      result.current.snapshot();
    });
    await expect(result.current.read()).resolves.toBeUndefined();
    expect(listSessions).not.toHaveBeenCalled();
  });
});
