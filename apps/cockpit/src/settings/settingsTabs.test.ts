import { describe, expect, it } from "vitest";
import { visibleTabs, tabFromParam } from "./settingsTabs";

describe("visibleTabs", () => {
  it("shows the same three per-account tabs, notifications first (issue #2022)", () => {
    expect(visibleTabs().map((t) => t.id)).toEqual(["notifications", "ai", "carmode"]);
  });

  it("never includes a machine or Privacy tab", () => {
    const ids = visibleTabs().map((t) => t.id as string);
    expect(ids).not.toContain("machine");
    expect(ids).not.toContain("privacy");
  });
});

describe("tabFromParam", () => {
  it("resolves each tab id to itself", () => {
    for (const t of visibleTabs()) expect(tabFromParam(t.id)).toBe(t.id);
  });

  it("falls back to notifications for missing or unknown ids", () => {
    expect(tabFromParam(null)).toBe("notifications");
    expect(tabFromParam("nonsense")).toBe("notifications");
  });

  it("no longer resolves the retired machine/telemetry/privacy ids to a tab", () => {
    expect(tabFromParam("machine")).toBe("notifications");
    expect(tabFromParam("telemetry")).toBe("notifications");
    expect(tabFromParam("privacy")).toBe("notifications");
  });
});
