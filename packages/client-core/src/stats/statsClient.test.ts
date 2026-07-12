import { describe, it, expect } from "vitest";
import { summarizeThrottle, formatShare, type ThrottleData } from "./statsClient";

// The pure "Your Throttle" summary math (devthrottle-stats mission). The network read (getThrottle) is
// exercised through the app; here we lock the honest share arithmetic that both shells render.

function data(buckets: ThrottleData["buckets"]): ThrottleData {
  return { generatedAtUtc: "2026-07-11T00:00:00Z", buckets, notCaptured: [] };
}

describe("summarizeThrottle", () => {
  it("computes turn totals and shares across modality and surface", () => {
    const s = summarizeThrottle(
      data([
        { modality: "voice", surface: "phone", turns: 3, characters: 900 },
        { modality: "typed", surface: "desktop", turns: 1, characters: 100 },
      ]),
    );
    expect(s.totalTurns).toBe(4);
    expect(s.totalCharacters).toBe(1000);
    expect(s.voiceTurns).toBe(3);
    expect(s.typedTurns).toBe(1);
    expect(s.turnsBySurface.phone).toBe(3);
    expect(s.turnsBySurface.desktop).toBe(1);
    expect(s.voiceShare).toBeCloseTo(0.75);
    expect(s.phoneShare).toBeCloseTo(0.75);
    expect(s.hasData).toBe(true);
  });

  it("reports null shares (not 0%) when no turns are counted yet", () => {
    const s = summarizeThrottle(data([]));
    expect(s.totalTurns).toBe(0);
    expect(s.voiceShare).toBeNull();
    expect(s.phoneShare).toBeNull();
    expect(s.hasData).toBe(false);
  });

  it("counts character-only volume as data even with zero turns", () => {
    // Raw terminal keystrokes are character volume with no turn - the tally still has data to show.
    const s = summarizeThrottle(
      data([{ modality: "typed", surface: "desktop", turns: 0, characters: 42 }]),
    );
    expect(s.totalTurns).toBe(0);
    expect(s.totalCharacters).toBe(42);
    expect(s.voiceShare).toBeNull();
    expect(s.hasData).toBe(true);
  });
});

describe("formatShare", () => {
  it("renders a fraction as a whole-number percent", () => {
    expect(formatShare(0.75)).toBe("75%");
    expect(formatShare(0)).toBe("0%");
    expect(formatShare(1)).toBe("100%");
  });

  it("renders no-data as an ASCII placeholder, never a fabricated 0%", () => {
    expect(formatShare(null)).toBe("n/a");
  });
});
