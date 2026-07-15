import { describe, expect, it } from "vitest";
import { formatSnoozeLength, snoozeDraftFrom, snoozeMinutesFrom } from "./snoozeFormat";

describe("formatSnoozeLength", () => {
  it("names the shipped lengths the way the agreed menu does", () => {
    expect(formatSnoozeLength(15)).toBe("15 minutes");
    expect(formatSnoozeLength(60)).toBe("1 hour");
    expect(formatSnoozeLength(240)).toBe("4 hours");
    expect(formatSnoozeLength(480)).toBe("8 hours");
  });

  it("says minute, not minutes, for a single minute", () => {
    expect(formatSnoozeLength(1)).toBe("1 minute");
  });

  it("keeps sub-hour lengths in minutes", () => {
    expect(formatSnoozeLength(59)).toBe("59 minutes");
  });

  it("names the remainder rather than rounding it away", () => {
    expect(formatSnoozeLength(90)).toBe("1 hour 30 minutes");
    expect(formatSnoozeLength(125)).toBe("2 hours 5 minutes");
  });

  it("moves up to days at a whole day", () => {
    expect(formatSnoozeLength(1440)).toBe("1 day");
    expect(formatSnoozeLength(2880)).toBe("2 days");
  });

  it("names the ceiling as a week of days", () => {
    expect(formatSnoozeLength(7 * 24 * 60)).toBe("7 days");
  });

  it("carries the hours left over after whole days", () => {
    expect(formatSnoozeLength(1440 + 120)).toBe("1 day 2 hours");
  });

  it("never renders a zero unit", () => {
    // 1 day 10 minutes must not read "1 day 0 hours", which looks like a bug.
    expect(formatSnoozeLength(1450)).toBe("1 day 10 minutes");
  });
});

describe("snoozeMinutesFrom", () => {
  it("converts each unit to minutes", () => {
    expect(snoozeMinutesFrom("30", "minutes")).toBe(30);
    expect(snoozeMinutesFrom("4", "hours")).toBe(240);
    expect(snoozeMinutesFrom("2", "days")).toBe(2880);
  });

  it("ignores surrounding spaces", () => {
    expect(snoozeMinutesFrom("  15 ", "minutes")).toBe(15);
  });

  it("rejects what the Gateway would reject, so Save stays disabled", () => {
    expect(snoozeMinutesFrom("0", "minutes")).toBeNull();
    expect(snoozeMinutesFrom("-3", "hours")).toBeNull();
    expect(snoozeMinutesFrom("8", "days")).toBeNull();
  });

  it("rejects anything that is not a whole number", () => {
    expect(snoozeMinutesFrom("", "minutes")).toBeNull();
    expect(snoozeMinutesFrom("   ", "minutes")).toBeNull();
    expect(snoozeMinutesFrom("abc", "minutes")).toBeNull();
    expect(snoozeMinutesFrom("1.5", "hours")).toBeNull();
  });

  it("accepts both ends of the range the Gateway allows", () => {
    expect(snoozeMinutesFrom("1", "minutes")).toBe(1);
    expect(snoozeMinutesFrom("7", "days")).toBe(10080);
  });
});

describe("snoozeDraftFrom", () => {
  it("opens a length in the largest unit that divides it evenly", () => {
    expect(snoozeDraftFrom(240)).toEqual({ count: "4", unit: "hours" });
    expect(snoozeDraftFrom(2880)).toEqual({ count: "2", unit: "days" });
    expect(snoozeDraftFrom(15)).toEqual({ count: "15", unit: "minutes" });
  });

  it("falls to minutes for a length no larger unit divides", () => {
    expect(snoozeDraftFrom(90)).toEqual({ count: "90", unit: "minutes" });
  });

  it("round-trips every shipped length back to itself", () => {
    for (const minutes of [15, 60, 240, 480]) {
      const draft = snoozeDraftFrom(minutes);
      expect(snoozeMinutesFrom(draft.count, draft.unit)).toBe(minutes);
    }
  });
});
