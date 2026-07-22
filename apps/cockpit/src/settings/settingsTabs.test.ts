import { describe, expect, it } from "vitest";
import { TABS, tabFromParam } from "./settingsTabs";

describe("tabFromParam", () => {
  it("resolves each real tab id to itself", () => {
    for (const t of TABS) {
      expect(tabFromParam(t.id)).toBe(t.id);
    }
  });

  it("falls back to This machine when the parameter is missing or unknown", () => {
    expect(tabFromParam(null)).toBe("machine");
    expect(tabFromParam("")).toBe("machine");
    expect(tabFromParam("nonsense")).toBe("machine");
  });
});

describe("TABS", () => {
  it("orders the tabs machine-first, with the scopes grouped after it", () => {
    expect(TABS.map((t) => t.id)).toEqual(["machine", "notifications", "ai", "carmode", "training"]);
  });

  it("has no duplicate ids", () => {
    expect(new Set(TABS.map((t) => t.id)).size).toBe(TABS.length);
  });
});
