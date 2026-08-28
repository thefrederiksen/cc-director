import { describe, expect, it } from "vitest";
import { judgeEcho, similarity, type EchoContext } from "./echoGuard";

const QUIET: EchoContext = {
  speaking: false,
  lastSpokeEndedAt: 0,
  recentlySpoken: [],
  now: 100000,
};

describe("similarity", () => {
  it("scores identical text as one", () => {
    expect(similarity("no problem", "No problem.")).toBe(1);
  });

  // The actual pair from the loop: it said "Alright." and heard itself as "all right".
  it("sees through a space and a full stop", () => {
    expect(similarity("all right", "Alright.")).toBeGreaterThan(0.85);
  });

  it("scores unrelated text low", () => {
    expect(similarity("set a timer for ten minutes", "Copenhagen")).toBeLessThan(0.3);
  });

  it("handles empty text without dividing by zero", () => {
    expect(similarity("", "")).toBe(1);
    expect(similarity("hello", "")).toBe(0);
  });
});

describe("judgeEcho", () => {
  it("lets a genuine question through when the room is quiet", () => {
    expect(judgeEcho("can you make me a cup of coffee", QUIET).isEcho).toBe(false);
  });

  it("ignores everything while it is talking", () => {
    const decision = judgeEcho("something", { ...QUIET, speaking: true });
    expect(decision.isEcho).toBe(true);
    expect(decision.reason).toMatch(/while it was talking/);
  });

  it("ignores everything during the drain after it stops", () => {
    const decision = judgeEcho("no problem", { ...QUIET, lastSpokeEndedAt: 99500, now: 100000 });
    expect(decision.isEcho).toBe(true);
    expect(decision.reason).toMatch(/after it stopped talking/);
  });

  it("stops ignoring once the drain has passed", () => {
    expect(judgeEcho("what is the capital of Denmark", { ...QUIET, lastSpokeEndedAt: 90000 }).isEcho).toBe(false);
  });

  // The whole loop, replayed. Every one of these was a real turn the assistant took with itself.
  it("catches each leg of the coffee loop", () => {
    const spoken = ["I'm sorry, I can't make coffee.", "No problem.", "Alright.", "Got it."];
    for (const heard of ["I'm sorry I can't make coffee", "no problem", "all right", "got it"]) {
      const decision = judgeEcho(heard, { ...QUIET, recentlySpoken: spoken });
      expect(decision.isEcho, `should have suppressed "${heard}"`).toBe(true);
    }
  });

  it("still lets the question that started it through", () => {
    const spoken = ["I'm sorry, I can't make coffee.", "No problem.", "Alright."];
    expect(judgeEcho("can you make me a cup of coffee", { ...QUIET, recentlySpoken: spoken }).isEcho).toBe(false);
  });

  it("treats silence as an echo rather than a question", () => {
    expect(judgeEcho("   ", QUIET).isEcho).toBe(true);
  });
});
