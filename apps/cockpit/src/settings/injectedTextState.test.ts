import { describe, expect, it } from "vitest";
import { bannerFor, fleetCommandsWarning, validateTemplate } from "./injectedTextState";

describe("validateTemplate", () => {
  it("accepts a template with no conditional markers", () => {
    expect(validateTemplate("just my words, [SESSION_ID]")).toBeNull();
  });

  it("accepts a balanced [IF_SIGNED_IN] block", () => {
    expect(validateTemplate("before\n[IF_SIGNED_IN]\nhi [USER_NAME]\n[END_IF]\nafter")).toBeNull();
  });

  it("rejects an unclosed [IF_SIGNED_IN]", () => {
    expect(validateTemplate("[IF_SIGNED_IN]\nhello")).toContain("never closed");
  });

  it("rejects an [END_IF] with no opener", () => {
    expect(validateTemplate("hello\n[END_IF]")).toContain("no matching");
  });

  it("rejects nested blocks", () => {
    expect(validateTemplate("[IF_SIGNED_IN]\n[IF_SIGNED_IN]\nx\n[END_IF]\n[END_IF]")).toContain("nested");
  });

  it("accepts an empty template (inject nothing is the user's right)", () => {
    expect(validateTemplate("")).toBeNull();
  });
});

describe("fleetCommandsWarning", () => {
  it("warns when a non-empty custom text drops the fleet commands", () => {
    expect(fleetCommandsWarning("only my words")).toContain("cc-devthrottle");
  });

  it("is silent when the fleet commands are present", () => {
    expect(fleetCommandsWarning("run cc-devthrottle session list")).toBeNull();
  });

  it("is silent for empty text - injecting nothing is a clearer state than a warning", () => {
    expect(fleetCommandsWarning("   ")).toBeNull();
  });
});

describe("bannerFor", () => {
  it("editing-unsaved wins over the saved choice", () => {
    expect(bannerFor(true, true).tone).toBe("editing");
    expect(bannerFor(false, true).tone).toBe("editing");
  });

  it("reports ours when ours is live", () => {
    expect(bannerFor(false, false).tone).toBe("ours");
  });

  it("reports yours when the user's text is live", () => {
    expect(bannerFor(true, false).tone).toBe("yours");
  });
});
