import { describe, expect, it } from "vitest";
import { buildSnoozeMenu } from "./snoozeMenu";
import type { SnoozeOptions } from "./snoozeOptions";

// These expectations are deliberately the SAME strings pinned by the C# SnoozeMenuModelTests. The two
// implementations have no shared runtime, so this pair of suites is the only thing keeping the desktop
// menu and the Cockpit/phone menus reading identically. Change one side without the other and one of
// them goes red.

const shipped: SnoozeOptions = { presets: [15, 60, 240, 480], defaultMinutes: 60, maxPresets: 5 };

describe("buildSnoozeMenu", () => {
  it("names the length the plain item will use", () => {
    expect(buildSnoozeMenu(false, shipped).toggleHeader).toBe("Snooze  (1 hour)");
  });

  it("becomes Unsnooze for a snoozed session", () => {
    expect(buildSnoozeMenu(true, shipped).toggleHeader).toBe("Unsnooze");
  });

  it("offers every length in the Gateway's order, marking the default", () => {
    const menu = buildSnoozeMenu(false, shipped);
    expect(menu.choices.map((c) => c.minutes)).toEqual([15, 60, 240, 480]);
    expect(menu.choices.map((c) => c.header)).toEqual([
      "15 minutes",
      "1 hour  (default)",
      "4 hours",
      "8 hours",
    ]);
  });

  it("keeps the marked row and the plain item naming the same length", () => {
    const menu = buildSnoozeMenu(false, shipped);
    const marked = menu.choices.filter((c) => c.header.includes("(default)"));
    expect(marked).toHaveLength(1);
    expect(marked[0].minutes).toBe(60);
    expect(menu.toggleHeader).toContain("1 hour");
  });

  it("still offers the submenu while snoozed, so a length changes in one step", () => {
    expect(buildSnoozeMenu(true, shipped).choices).not.toHaveLength(0);
  });

  it("claims no length when the lengths are unknown", () => {
    // Null = this client has not read the Gateway's lengths. The click still works (the Gateway applies
    // the default), so the item must not name a length it does not know.
    const menu = buildSnoozeMenu(false, null);
    expect(menu.toggleHeader).toBe("Snooze");
    expect(menu.toggleHeader).not.toContain("(");
  });

  it("offers no submenu rather than an invented one", () => {
    // The one genuinely bad outcome would be showing plausible lengths that are not the user's.
    expect(buildSnoozeMenu(false, null).choices).toEqual([]);
    expect(buildSnoozeMenu(true, null).choices).toEqual([]);
  });

  it("treats an empty list from the Gateway as unknown", () => {
    const empty: SnoozeOptions = { presets: [], defaultMinutes: 60, maxPresets: 5 };
    const menu = buildSnoozeMenu(false, empty);
    expect(menu.toggleHeader).toBe("Snooze");
    expect(menu.choices).toEqual([]);
  });

  it("marks and offers a single length", () => {
    const one: SnoozeOptions = { presets: [90], defaultMinutes: 90, maxPresets: 5 };
    const menu = buildSnoozeMenu(false, one);
    expect(menu.toggleHeader).toBe("Snooze  (1 hour 30 minutes)");
    expect(menu.choices).toEqual([{ header: "1 hour 30 minutes  (default)", minutes: 90 }]);
  });

  it("names a custom default on the plain item", () => {
    const custom: SnoozeOptions = { presets: [15, 60, 240, 480], defaultMinutes: 480, maxPresets: 5 };
    expect(buildSnoozeMenu(false, custom).toggleHeader).toBe("Snooze  (8 hours)");
  });
});
