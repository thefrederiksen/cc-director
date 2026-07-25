import { describe, expect, it, vi } from "vitest";
import { MIC_OPEN_TIMEOUT_MS, startRecorderWithTimeout, type MicRecorder } from "./recorder";

// The backstop for a microphone that never opens.
//
// getUserMedia is specified to resolve or reject, and in practice it can do NEITHER: a contended or
// wedged capture device leaves the promise pending forever. This was not theoretical - it happened
// while testing the voice checks in a real browser, and the symptom was a Start button that did
// nothing at all, with no error, for as long as you cared to wait. A check whose failure looks
// exactly like a dead button is worse than no check.

/** A stand-in recorder whose start() resolves, rejects, or hangs on demand. */
function fakeRecorder(behaviour: "resolves" | "rejects" | "hangs") {
  const disposed = { count: 0 };
  const recorder = {
    start: () => {
      if (behaviour === "resolves") return Promise.resolve();
      if (behaviour === "rejects") return Promise.reject(new Error("Permission denied"));
      return new Promise<void>(() => {
        /* never settles - the real wedged-device case */
      });
    },
    dispose: () => {
      disposed.count++;
    },
  } as unknown as MicRecorder;
  return { recorder, disposed };
}

describe("startRecorderWithTimeout", () => {
  it("returns normally when the microphone opens", async () => {
    const { recorder, disposed } = fakeRecorder("resolves");
    await expect(startRecorderWithTimeout(recorder, 50)).resolves.toBeUndefined();
    // A successful open must NOT dispose the recorder - the caller is about to record with it.
    expect(disposed.count).toBe(0);
  });

  it("passes a real rejection through unchanged, so the reason still reaches the user", async () => {
    const { recorder } = fakeRecorder("rejects");
    await expect(startRecorderWithTimeout(recorder, 50)).rejects.toThrow("Permission denied");
  });

  it("gives up on a microphone that never opens, instead of hanging forever", async () => {
    const { recorder } = fakeRecorder("hangs");
    await expect(startRecorderWithTimeout(recorder, 30)).rejects.toThrow(/did not open within/);
  });

  it("releases the microphone when it times out, so a late stream cannot leak", async () => {
    const { recorder, disposed } = fakeRecorder("hangs");
    await expect(startRecorderWithTimeout(recorder, 30)).rejects.toThrow();
    expect(disposed.count).toBe(1);
  });

  it("says how long it waited, in seconds, so the message is actionable", async () => {
    const { recorder } = fakeRecorder("hangs");
    await expect(startRecorderWithTimeout(recorder, 2000)).rejects.toThrow(/2 seconds/);
  });

  it("clears its timer on success, so a resolved open leaves nothing pending", async () => {
    const clearSpy = vi.spyOn(globalThis, "clearTimeout");
    const { recorder } = fakeRecorder("resolves");
    await startRecorderWithTimeout(recorder, 5000);
    expect(clearSpy).toHaveBeenCalled();
    clearSpy.mockRestore();
  });

  it("defaults to a wait short enough to be noticed but long enough for a slow device", () => {
    expect(MIC_OPEN_TIMEOUT_MS).toBeGreaterThanOrEqual(3000);
    expect(MIC_OPEN_TIMEOUT_MS).toBeLessThanOrEqual(15000);
  });
});
