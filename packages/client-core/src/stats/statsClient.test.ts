import { describe, it, expect } from "vitest";
import {
  summarizeThrottle,
  formatShare,
  last24HourKeys,
  windowSeries,
  emptyInputHour,
  localHourLabel,
  safeTimeZone,
  type ThrottleData,
  type InputHour,
} from "./statsClient";

// The pure "Your Throttle" summary math (devthrottle-stats mission). The network read (getThrottle) is
// exercised through the app; here we lock the honest share arithmetic that both shells render.

function data(buckets: ThrottleData["buckets"]): ThrottleData {
  return {
    generatedAtUtc: "2026-07-11T00:00:00Z",
    timeZone: "UTC",
    buckets,
    hourlyTurns: [],
    concurrency: null,
    wingman: { turns: 0, sessions: 0 },
    notCaptured: [],
  };
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

describe("last24HourKeys", () => {
  it("returns 24 consecutive UTC hour keys ending at the current hour, oldest first", () => {
    const keys = last24HourKeys(new Date("2026-07-13T17:42:00Z"));
    expect(keys).toHaveLength(24);
    expect(keys[23]).toBe("2026-07-13T17"); // the hour containing "now"
    expect(keys[22]).toBe("2026-07-13T16");
    expect(keys[0]).toBe("2026-07-12T18"); // 23 hours earlier, across the day boundary
  });
});

describe("windowSeries", () => {
  it("aligns a sparse series onto the window and zero-fills the gaps", () => {
    const keys = last24HourKeys(new Date("2026-07-13T02:00:00Z"));
    const sparse: InputHour[] = [
      { hour: "2026-07-13T01", turns: 5, voiceTurns: 4, typedTurns: 1, characters: 200 },
    ];
    const windowed = windowSeries(sparse, keys, emptyInputHour);
    expect(windowed).toHaveLength(24);
    // The one populated hour lands in its slot; every other hour is a real zero entry.
    expect(windowed[23]).toEqual({ hour: "2026-07-13T02", turns: 0, voiceTurns: 0, typedTurns: 0, characters: 0 });
    expect(windowed[22]).toEqual(sparse[0]);
    expect(windowed[0].turns).toBe(0);
  });

  it("gives two different series the SAME aligned window so charts line up", () => {
    const keys = last24HourKeys(new Date("2026-07-13T10:00:00Z"));
    const a = windowSeries([{ hour: "2026-07-13T02", turns: 3, voiceTurns: 3, typedTurns: 0, characters: 9 }], keys, emptyInputHour);
    const b = windowSeries([{ hour: "2026-07-13T09", turns: 7, voiceTurns: 1, typedTurns: 6, characters: 40 }], keys, emptyInputHour);
    expect(a.map((h) => h.hour)).toEqual(b.map((h) => h.hour)); // identical hour axis -> aligned
  });
});

describe("localHourLabel", () => {
  it("formats a UTC hour key as the local 2-digit hour in the given zone", () => {
    // 17:00 UTC on a July date is 13:00 in New York (EDT, UTC-4) and 17:00 in UTC.
    expect(localHourLabel("2026-07-13T17", "UTC")).toBe("17");
    expect(localHourLabel("2026-07-13T17", "America/New_York")).toBe("13");
  });
});

describe("safeTimeZone", () => {
  it("passes a usable zone through", () => {
    expect(safeTimeZone("UTC")).toBe("UTC");
    expect(safeTimeZone("America/New_York")).toBe("America/New_York");
  });

  it("falls back to a usable zone for a bad or empty id (never throws)", () => {
    const fallback = safeTimeZone("Not/AZone");
    // Whatever it resolves to, it must be a zone Intl can actually format with.
    expect(() => new Intl.DateTimeFormat("en-US", { timeZone: fallback })).not.toThrow();
    expect(safeTimeZone("").length).toBeGreaterThan(0);
  });
});
