// Whether a configuration is good enough to use on THIS device.
//
// Three gates, and a configuration has to pass all three. They are separate rather than one score
// because when a rung fails we want to know WHICH way it failed: too slow is a different problem from
// mishearing, and both are different from the one that actually bit us.
//
// THE THIRD GATE EXISTS BECAUSE OF A REAL BUG. On 28 August 2026 whisper-base with a four-bit decoder
// returned "Wilson." for a clip that said "Wilson, set a timer for ten minutes." It did not mishear
// the command, it stopped after the first word. Averages hid it: the run still looked like a
// respectable 24 percent error overall. A wake word followed by an instruction is the shape of every
// single thing anyone will ever say to this device, so a configuration that truncates there is
// useless no matter how good its averages are.
//
// Note the direction of that check. It asks for the PRESENCE of the words that should be there, not
// for the absence of something bad. A gate whose pass condition is an absence passes when nothing
// ran at all.

import type { ClipOutcome, CombinationResult } from "../benchmark/runBenchmark";

export interface Thresholds {
  /** Mean time to transcribe divided by real time. Below 1 keeps up; the margin is deliberate. */
  readonly maximumRealTimeFactor: number;
  /** Mean word error rate across the clips. */
  readonly maximumErrorRate: number;
  /** Fraction of the expected words a transcript must actually contain to count as complete. */
  readonly minimumCompleteness: number;
}

export const DEFAULT_THRESHOLDS: Thresholds = {
  // 0.7 rather than 1.0 so there is room for a slower moment, a busy phone, or a longer sentence.
  maximumRealTimeFactor: 0.7,
  maximumErrorRate: 0.2,
  // Whisper reasonably writes "10" for "ten", so an exact word count is too strict. Two thirds
  // catches truncation, which is what this is for, and lets ordinary rewording through.
  minimumCompleteness: 0.66,
};

export interface GateVerdict {
  readonly passed: boolean;
  /** Every gate that failed, in plain words, ready to show. */
  readonly failures: string[];
  readonly meanRealTimeFactor: number | null;
  readonly meanErrorRate: number | null;
  readonly worstCompleteness: number | null;
}

/**
 * How much of the expected sentence actually came back.
 *
 * Word counts, not word matching: this is looking for a transcript that stops early, not for one
 * that gets words wrong. Getting words wrong is the second gate's job.
 */
export function completenessOf(expectedWordCount: number, heard: string): number {
  if (expectedWordCount === 0) {
    return 1;
  }
  const heardWords = heard.trim().length === 0 ? 0 : heard.trim().split(/\s+/).length;
  return Math.min(1, heardWords / expectedWordCount);
}

/** Judge one measured configuration against the thresholds. */
export function judge(
  result: CombinationResult,
  expectedWordCounts: ReadonlyMap<string, number>,
  thresholds: Thresholds = DEFAULT_THRESHOLDS,
): GateVerdict {
  if (result.status !== "ok") {
    return {
      passed: false,
      failures: [result.status === "skipped" ? "Not available on this device." : `Failed to run. ${result.message ?? ""}`.trim()],
      meanRealTimeFactor: null,
      meanErrorRate: null,
      worstCompleteness: null,
    };
  }

  const failures: string[] = [];
  const factor = result.meanRealTimeFactor ?? Number.POSITIVE_INFINITY;
  const errors = result.meanErrorRate ?? 1;

  if (factor > thresholds.maximumRealTimeFactor) {
    failures.push(`Too slow: ${factor.toFixed(2)} against a limit of ${thresholds.maximumRealTimeFactor}.`);
  }
  if (errors > thresholds.maximumErrorRate) {
    failures.push(`Too many mistakes: ${Math.round(errors * 100)}% against a limit of ${Math.round(thresholds.maximumErrorRate * 100)}%.`);
  }

  const worst = worstCompleteness(result.clips, expectedWordCounts);
  if (worst !== null && worst < thresholds.minimumCompleteness) {
    failures.push(
      `Cut a sentence short: only ${Math.round(worst * 100)}% of the words came back on one clip.`,
    );
  }

  return {
    passed: failures.length === 0,
    failures,
    meanRealTimeFactor: result.meanRealTimeFactor ?? null,
    meanErrorRate: result.meanErrorRate ?? null,
    worstCompleteness: worst,
  };
}

function worstCompleteness(
  clips: readonly ClipOutcome[],
  expectedWordCounts: ReadonlyMap<string, number>,
): number | null {
  let worst: number | null = null;
  for (const clip of clips) {
    const expected = expectedWordCounts.get(clip.clipId);
    if (expected === undefined) {
      continue;
    }
    const value = completenessOf(expected, clip.heard);
    if (worst === null || value < worst) {
      worst = value;
    }
  }
  return worst;
}
