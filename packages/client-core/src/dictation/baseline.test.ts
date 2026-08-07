// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { act, renderHook } from "@testing-library/react";

// Regression coverage for issue #2478: the Gateway's "session moved on" guard requires a
// baselineBufferBytes above zero, and both taught Speak flows omitted it - so the guard never armed.
// This file pins the shared snapshot the flows now take: the roster's terminal-byte position for the
// selected session, read when Speak is pressed, and "unknown" (undefined, guard skipped for safety)
// whenever the roster cannot answer - never a blocked or lost dictation.

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
  });

  it("converts the wire's string form of the 64-bit integer", async () => {
    listSessions.mockResolvedValueOnce([{ sessionId: "sess-42", totalBufferBytes: "9007199254740993" }]);
    await expect(snapshotBaselineBufferBytes("sess-42")).resolves.toBe(Number("9007199254740993"));
  });

  it("returns unknown when the session is not on the roster", async () => {
    listSessions.mockResolvedValueOnce([{ sessionId: "other", totalBufferBytes: 5 }]);
    await expect(snapshotBaselineBufferBytes("sess-42")).resolves.toBeUndefined();
  });

  it("returns unknown when the session carries no terminal-byte position", async () => {
    listSessions.mockResolvedValueOnce([{ sessionId: "sess-42" }]);
    await expect(snapshotBaselineBufferBytes("sess-42")).resolves.toBeUndefined();
  });

  it("returns unknown when the roster read fails, so the dictation still delivers unguarded", async () => {
    listSessions.mockRejectedValueOnce(new Error("gateway unreachable"));
    await expect(snapshotBaselineBufferBytes("sess-42")).resolves.toBeUndefined();
  });
});

describe("useDictationBaseline", () => {
  it("is unknown before Speak is pressed, then reads the snapshot taken at Speak press", async () => {
    listSessions.mockResolvedValue([{ sessionId: "sess-42", totalBufferBytes: 4321 }]);
    const { result } = renderHook(() => useDictationBaseline("sess-42"));

    expect(result.current.read()).toBeUndefined();

    await act(async () => {
      result.current.snapshot();
    });
    expect(result.current.read()).toBe(4321);
  });

  it("forgets the previous recording's snapshot the moment Speak is pressed again", async () => {
    // The first press's roster answer is held back until AFTER the second press has answered, to
    // prove a late first answer can never stamp the second recording (the token guard).
    let releaseFirst: (roster: { sessionId: string; totalBufferBytes: number }[]) => void = () => {};
    listSessions
      .mockImplementationOnce(() => new Promise((resolve) => { releaseFirst = resolve; }))
      .mockResolvedValueOnce([{ sessionId: "sess-42", totalBufferBytes: 200 }]);
    const { result } = renderHook(() => useDictationBaseline("sess-42"));

    await act(async () => {
      result.current.snapshot(); // first Speak press: roster answer deliberately in flight
    });
    expect(result.current.read()).toBeUndefined(); // forgotten/unknown while pending

    await act(async () => {
      result.current.snapshot(); // second Speak press: answers 200 immediately
    });
    expect(result.current.read()).toBe(200);

    await act(async () => {
      releaseFirst([{ sessionId: "sess-42", totalBufferBytes: 100 }]); // the late first answer arrives
    });
    expect(result.current.read()).toBe(200); // and is discarded, never stamping the second recording
  });

  it("stays unknown with no session selected, without calling the roster", async () => {
    const { result } = renderHook(() => useDictationBaseline(undefined));
    await act(async () => {
      result.current.snapshot();
    });
    expect(result.current.read()).toBeUndefined();
    expect(listSessions).not.toHaveBeenCalled();
  });
});
