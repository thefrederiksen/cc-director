import { describe, it, expect } from "vitest";
import type { SessionDto } from "../api/client";
import { supervisionStats, startedValue, totalIdleSeconds, turnCount } from "./supervision";
import { durationFromMs, durationLabel } from "./waiting";

// The supervision line is pure formatting over the Phase 1 wire facts, so everything here is a
// fixed-clock unit test. The cases that matter most are the silent-when-wrong ones: an older
// Director that sends no turn count and no idle clock must produce NO stat - never "turns 0" or
// "idle 0m" - and the impossible 0001-01-01 CreatedAt must not render as a decades-long runtime.

const HOUR_MS = 3600 * 1000;

// A local-time anchor (not UTC-derived) so startedValue's local-calendar rules test the same way
// in every timezone the suite runs in.
const NOW = new Date(2026, 6, 26, 14, 30, 0).getTime();

function session(overrides: Record<string, unknown>): SessionDto {
  return overrides as never;
}

function isoAgo(ms: number): string {
  return new Date(NOW - ms).toISOString();
}

describe("turnCount", () => {
  it("reads a number, coerces the numeric-string form, floors fractions", () => {
    expect(turnCount(session({ turnCount: 14 }))).toBe(14);
    expect(turnCount(session({ turnCount: "14" }))).toBe(14);
    expect(turnCount(session({ turnCount: 3.7 }))).toBe(3);
  });

  it("treats missing, null, negative and garbage as unknown", () => {
    expect(turnCount(session({}))).toBeNull();
    expect(turnCount(session({ turnCount: null }))).toBeNull();
    expect(turnCount(session({ turnCount: -1 }))).toBeNull();
    expect(turnCount(session({ turnCount: "soon" }))).toBeNull();
  });
});

describe("totalIdleSeconds", () => {
  it("is the closed total while the session is not waiting", () => {
    expect(totalIdleSeconds(session({ cumulativeIdleSeconds: 2520 }), NOW)).toBe(2520);
  });

  it("adds the open stretch while the session is waiting right now", () => {
    const s = session({ cumulativeIdleSeconds: 60, waitingSince: isoAgo(30 * 1000) });
    expect(totalIdleSeconds(s, NOW)).toBe(90);
  });

  it("is unknown when the Director does not report the clock, even if an anchor is present", () => {
    expect(totalIdleSeconds(session({}), NOW)).toBeNull();
    expect(totalIdleSeconds(session({ waitingSince: isoAgo(1000) }), NOW)).toBeNull();
  });

  it("clamps a clock-skewed future anchor to zero rather than going negative", () => {
    const s = session({ cumulativeIdleSeconds: 10, waitingSince: isoAgo(-60 * 1000) });
    expect(totalIdleSeconds(s, NOW)).toBe(10);
  });
});

describe("startedValue", () => {
  it("is the bare wall-clock time for a same-day start", () => {
    const created = new Date(2026, 6, 26, 9, 14, 0).getTime();
    expect(startedValue(created, NOW)).toBe("09:14");
  });

  it("carries the weekday within the past six days", () => {
    // 2026-07-23 is a Thursday.
    const created = new Date(2026, 6, 23, 18, 2, 0).getTime();
    expect(startedValue(created, NOW)).toBe("Thu 18:02");
  });

  it("falls back to the date beyond a week", () => {
    const created = new Date(2026, 5, 30, 8, 0, 0).getTime();
    expect(startedValue(created, NOW)).toBe("30 Jun");
  });
});

describe("supervisionStats", () => {
  it("renders all four facts, in the fixed started / open / idle / turns order", () => {
    const s = session({
      createdAt: isoAgo(3 * HOUR_MS),
      turnCount: 14,
      cumulativeIdleSeconds: 42 * 60,
      waitingSince: null,
    });

    const stats = supervisionStats(s, NOW);

    expect(stats.map((x) => x.key)).toEqual(["started", "open", "idle", "turns"]);
    expect(stats[1].value).toBe("3h 0m");
    expect(stats[2].value).toBe("42m");
    expect(stats[3].value).toBe("14");
  });

  it("omits idle and turns for an older Director, rather than rendering zeros", () => {
    const stats = supervisionStats(session({ createdAt: isoAgo(HOUR_MS) }), NOW);

    expect(stats.map((x) => x.key)).toEqual(["started", "open"]);
  });

  it("renders a live Director's measured zero turns - zero from a measurement is an answer", () => {
    const s = session({ createdAt: isoAgo(60 * 1000), turnCount: 0, cumulativeIdleSeconds: 0 });

    const stats = supervisionStats(s, NOW);

    expect(stats.find((x) => x.key === "turns")?.value).toBe("0");
    expect(stats.find((x) => x.key === "idle")?.value).toBe("0m");
  });

  it("drops the impossible 0001-01-01 CreatedAt instead of claiming a decades-long runtime", () => {
    const s = session({ createdAt: "0001-01-01T00:00:00Z", turnCount: 2, cumulativeIdleSeconds: 5 });

    const stats = supervisionStats(s, NOW);

    expect(stats.map((x) => x.key)).toEqual(["idle", "turns"]);
  });

  it("turns the idle stat amber past an hour and red past four", () => {
    const base = { createdAt: isoAgo(HOUR_MS) };
    const at = (seconds: number) =>
      supervisionStats(session({ ...base, cumulativeIdleSeconds: seconds }), NOW).find((x) => x.key === "idle")?.tone;

    expect(at(30 * 60)).toBe("normal");
    expect(at(90 * 60)).toBe("warm");
    expect(at(5 * 3600)).toBe("hot");
  });

  it("turns the open stat amber past a day and red past two - the 55-hour session must glow", () => {
    const openTone = (ms: number) =>
      supervisionStats(session({ createdAt: isoAgo(ms) }), NOW).find((x) => x.key === "open")?.tone;

    expect(openTone(3 * HOUR_MS)).toBe("normal");
    expect(openTone(30 * HOUR_MS)).toBe("warm");
    expect(openTone(55 * HOUR_MS)).toBe("hot");
  });
});

describe("the shared duration ladder", () => {
  it("climbs minutes to hours to days from a bare span", () => {
    expect(durationFromMs(0)).toBe("0m");
    expect(durationFromMs(12 * 60 * 1000)).toBe("12m");
    expect(durationFromMs(HOUR_MS + 4 * 60 * 1000)).toBe("1h 4m");
    expect(durationFromMs(2 * 24 * HOUR_MS + 3 * HOUR_MS)).toBe("2d 3h");
  });

  it("keeps durationLabel's contract intact after the refactor onto it", () => {
    expect(durationLabel(isoAgo(30 * 1000), NOW)).toBe("just now");
    expect(durationLabel(isoAgo(12 * 60 * 1000), NOW)).toBe("12m");
    expect(durationLabel("", NOW)).toBe("");
    expect(durationLabel("not-a-date", NOW)).toBe("");
  });
});
