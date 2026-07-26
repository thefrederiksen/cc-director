import { describe, expect, it } from "vitest";
import { visibleTabs, tabFromParam } from "./tabs";

describe("visibleTabs", () => {
  it("shows the four per-account tabs, notifications first", () => {
    expect(visibleTabs().map((t) => t.id)).toEqual(["notifications", "ai", "transcription", "carmode"]);
  });

  // The point of moving this list into client-core: BOTH shells read it, so a tab that exists on the
  // desktop exists on the phone by construction. Transcription is the tab that was missing from both
  // Settings pages while its checks lived on separate screens.
  it("includes Transcription, so the dictation checks have a home in Settings on every surface", () => {
    expect(visibleTabs().map((t) => t.id)).toContain("transcription");
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
