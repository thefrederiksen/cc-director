import { describe, expect, it } from "vitest";
import type { CombinationResult } from "../benchmark/runBenchmark";
import { completenessOf, judge, DEFAULT_THRESHOLDS } from "./gates";

const EXPECTED = new Map<string, number>([["wake", 7], ["ask", 7]]);

function result(over: Partial<CombinationResult> = {}): CombinationResult {
  return {
    modelId: "onnx-community/whisper-base.en",
    device: "webgpu",
    decoderPrecision: "q8",
    status: "ok",
    loadMs: 1000,
    clips: [
      { clipId: "wake", heard: "Wilson set a timer for ten minutes", transcribeMs: 500, realTimeFactor: 0.3, errorRate: 0 },
      { clipId: "ask", heard: "How many grams are in an ounce", transcribeMs: 500, realTimeFactor: 0.3, errorRate: 0 },
    ],
    meanRealTimeFactor: 0.3,
    meanErrorRate: 0,
    ...over,
  };
}

describe("completenessOf", () => {
  it("is one when everything came back", () => {
    expect(completenessOf(7, "Wilson set a timer for ten minutes")).toBe(1);
  });

  it("is small when the transcript stopped early", () => {
    expect(completenessOf(7, "Wilson.")).toBeCloseTo(1 / 7, 3);
  });

  it("is zero for an empty transcript", () => {
    expect(completenessOf(7, "   ")).toBe(0);
  });

  it("never exceeds one, so a chatty model does not look extra complete", () => {
    expect(completenessOf(2, "one two three four five")).toBe(1);
  });
});

describe("judge", () => {
  it("passes a configuration that is fast, accurate and complete", () => {
    const verdict = judge(result(), EXPECTED);
    expect(verdict.passed).toBe(true);
    expect(verdict.failures).toEqual([]);
  });

  it("fails one that is too slow, and says so", () => {
    const verdict = judge(result({ meanRealTimeFactor: 0.95 }), EXPECTED);
    expect(verdict.passed).toBe(false);
    expect(verdict.failures.join(" ")).toMatch(/Too slow/);
  });

  it("fails one that mishears too much", () => {
    const verdict = judge(result({ meanErrorRate: 0.4 }), EXPECTED);
    expect(verdict.passed).toBe(false);
    expect(verdict.failures.join(" ")).toMatch(/Too many mistakes/);
  });

  // The real bug, kept as a test so it can never come back unnoticed.
  it("fails the four-bit truncation even though the averages look survivable", () => {
    const truncated = result({
      decoderPrecision: "q4",
      meanErrorRate: 0.19,
      clips: [
        { clipId: "wake", heard: "Wilson.", transcribeMs: 500, realTimeFactor: 0.3, errorRate: 0.857 },
        { clipId: "ask", heard: "How many grams are in an ounce", transcribeMs: 500, realTimeFactor: 0.3, errorRate: 0 },
      ],
    });
    const verdict = judge(truncated, EXPECTED);
    expect(verdict.passed).toBe(false);
    expect(verdict.failures.join(" ")).toMatch(/Cut a sentence short/);
  });

  it("fails a configuration that could not run at all", () => {
    const verdict = judge(result({ status: "failed", message: "out of memory" }), EXPECTED);
    expect(verdict.passed).toBe(false);
    expect(verdict.failures.join(" ")).toMatch(/out of memory/);
  });

  it("fails one that was skipped, rather than treating an absent run as a pass", () => {
    const verdict = judge(result({ status: "skipped" }), EXPECTED);
    expect(verdict.passed).toBe(false);
  });

  it("uses the thresholds it is given", () => {
    const strict = { ...DEFAULT_THRESHOLDS, maximumRealTimeFactor: 0.2 };
    expect(judge(result(), EXPECTED, strict).passed).toBe(false);
  });
});
