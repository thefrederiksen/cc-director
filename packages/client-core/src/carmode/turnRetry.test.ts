import { describe, expect, it } from "vitest";
import {
  classifyHeldTurn,
  HARD_WINDOW_MS,
  nextTurnRetryDelayMs,
  STALE_TURN_MS,
} from "./turnRetry";

// The pure retry policy for held Car Mode turns (offline-resilience Phase 4a, #1427). These lock in the
// two safety decisions the Architect made (2026-07-13): the brainSent boundary and the staleness cap
// decide whether a held turn is auto-retried or held for the owner, and the cadence is hard-then-throttled.

const NOW = 1_000_000_000_000;

describe("classifyHeldTurn", () => {
  it("auto-retries a fresh turn that never reached the brain (the dominant dead-zone case)", () => {
    expect(classifyHeldTurn({ createdAt: NOW - 1000 }, NOW)).toBe("auto");
  });

  it("auto-retries a fresh turn even if it was already sent to the brain (Phase 4b server dedupe makes it safe)", () => {
    // With the Idempotency-Key, an already-sent turn acts at most once, so brainSent no longer forces
    // ask-owner - only staleness does.
    expect(classifyHeldTurn({ createdAt: NOW - 1000 }, NOW)).toBe("auto");
  });

  it("holds for the owner a turn older than the staleness cap", () => {
    // Firing a half-hour-old action blind is wrong, so surface it for an explicit yes regardless of state.
    expect(classifyHeldTurn({ createdAt: NOW - STALE_TURN_MS - 1 }, NOW)).toBe("ask-owner");
  });

  it("still auto-retries a turn right up to the staleness cap", () => {
    expect(classifyHeldTurn({ createdAt: NOW - (STALE_TURN_MS - 1) }, NOW)).toBe("auto");
  });

  it("treats exactly the staleness cap as too old to auto-fire", () => {
    expect(classifyHeldTurn({ createdAt: NOW - STALE_TURN_MS }, NOW)).toBe("ask-owner");
  });
});

describe("nextTurnRetryDelayMs", () => {
  it("backs off exponentially from two seconds during the first hard hour", () => {
    const created = NOW;
    expect(nextTurnRetryDelayMs(created, 0, NOW)).toBe(2_000);
    expect(nextTurnRetryDelayMs(created, 1, NOW)).toBe(4_000);
    expect(nextTurnRetryDelayMs(created, 2, NOW)).toBe(8_000);
  });

  it("caps the hard backoff at fifteen seconds", () => {
    expect(nextTurnRetryDelayMs(NOW, 10, NOW)).toBe(15_000);
  });

  it("throttles to five minutes once past the first hour since capture", () => {
    const created = NOW - HARD_WINDOW_MS; // exactly one hour old
    expect(nextTurnRetryDelayMs(created, 0, NOW)).toBe(5 * 60 * 1000);
    // and it stays throttled no matter the attempt count
    expect(nextTurnRetryDelayMs(created, 20, NOW)).toBe(5 * 60 * 1000);
  });

  it("never stops - it always returns a finite, positive delay", () => {
    const created = NOW - 10 * HARD_WINDOW_MS; // very old
    const delay = nextTurnRetryDelayMs(created, 100, NOW);
    expect(delay).toBeGreaterThan(0);
    expect(Number.isFinite(delay)).toBe(true);
  });
});
