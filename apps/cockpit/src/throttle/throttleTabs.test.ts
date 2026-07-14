import { describe, expect, it } from "vitest";
import { TABS, DEFAULT_TAB, isThrottleTab } from "./throttleTabs";

describe("TABS", () => {
  it("lists the four tabs in reading order, Repos last", () => {
    expect(TABS.map((t) => t.key)).toEqual(["overview", "activity", "breakdown", "repos"]);
  });

  it("carries the Repos tab folded in from the retired standalone page", () => {
    expect(TABS.find((t) => t.key === "repos")?.label).toBe("Repos");
  });

  it("defaults to the tab that leads with the headline percentages", () => {
    expect(isThrottleTab(DEFAULT_TAB)).toBe(true);
    expect(DEFAULT_TAB).toBe("overview");
  });
});

describe("isThrottleTab", () => {
  it("accepts every tab that exists, so a stored tab is restored rather than reset", () => {
    for (const t of TABS) expect(isThrottleTab(t.key)).toBe(true);
  });

  // The regression this guards: Repos became a tab, and a validator that did not know about it would
  // reject the stored value and quietly drop the owner back on Overview every visit.
  it("accepts a stored repos tab", () => {
    expect(isThrottleTab("repos")).toBe(true);
  });

  it("rejects a tab that no longer exists, junk, and empty text", () => {
    expect(isThrottleTab("charts")).toBe(false);
    expect(isThrottleTab("Repos")).toBe(false);
    expect(isThrottleTab("")).toBe(false);
  });
});
