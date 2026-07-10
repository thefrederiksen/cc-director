import { describe, expect, it } from "vitest";
import {
  countFailedTurns,
  SUCCESS_OUTCOME,
  type TranscriptionStats,
} from "./transcriptionAnalysisClient";

// Regression tests for issue 1253: the Transcription Health page must derive its failure count from
// exactly one source - the byOutcome map it also renders as the outcome breakdown - so the failure
// number, the success banner, and that breakdown can never contradict each other. The old code
// computed failures as totalTurns minus successfulTurns, a second number that could drift from the
// map. countFailedTurns is that single source.

function stats(fields: Partial<TranscriptionStats> = {}): TranscriptionStats {
  return {
    totalTurns: 0,
    successfulTurns: 0,
    byOutcome: {},
    firstTurnUtc: null,
    lastTurnUtc: null,
    transcriptionMs: { count: 0, min: 0, max: 0, avg: 0, p50: 0, p90: 0, p95: 0, p99: 0 },
    cleanupMs: { count: 0, min: 0, max: 0, avg: 0, p50: 0, p90: 0, p95: 0, p99: 0 },
    correctedTurns: 0,
    cleanupAppliedTurns: 0,
    totalWords: 0,
    totalCharacters: 0,
    totalAudioBytes: 0,
    ...fields,
  };
}

describe("countFailedTurns - single source for the failure count", () => {
  it("sums every non-success entry of byOutcome", () => {
    const s = stats({
      byOutcome: { ok: 8, out_of_credits: 2, provider_error: 3 },
    });

    expect(countFailedTurns(s)).toBe(5);
  });

  it("returns zero when every dictation succeeded", () => {
    const s = stats({ byOutcome: { [SUCCESS_OUTCOME]: 10 } });

    expect(countFailedTurns(s)).toBe(0);
  });

  it("returns zero for an empty window", () => {
    expect(countFailedTurns(stats())).toBe(0);
  });

  it("reads byOutcome, not totalTurns minus successfulTurns, so the two cannot drift", () => {
    // A payload where the subtraction disagrees with the authoritative map: the subtraction would
    // report one failure, but the map records none. The page must trust the map it renders.
    const s = stats({
      totalTurns: 5,
      successfulTurns: 4,
      byOutcome: { ok: 5 },
    });

    expect(s.totalTurns - s.successfulTurns).toBe(1); // the old, drifting derivation
    expect(countFailedTurns(s)).toBe(0); // the single source the breakdown also uses
  });
});
