import { describe, expect, it } from "vitest";
import { isSettingsDirty, prettyPrintSettings } from "./settingsEditor";

// Regression tests for the Director settings editor dirty tracking (issue #1255): Save must be disabled
// until the text actually differs from what was loaded, and Reload must know when there are edits to
// warn about.

describe("prettyPrintSettings", () => {
  it("pretty-prints valid JSON with two-space indentation", () => {
    expect(prettyPrintSettings('{"a":1,"b":2}')).toBe('{\n  "a": 1,\n  "b": 2\n}');
  });

  it("keeps invalid JSON verbatim so nothing is lost", () => {
    expect(prettyPrintSettings("not json {")).toBe("not json {");
  });

  it("is idempotent - re-printing already-pretty text leaves a clean editor", () => {
    const once = prettyPrintSettings('{"a":1}');
    expect(prettyPrintSettings(once)).toBe(once);
    // A freshly-loaded, untouched editor is therefore clean against its own baseline.
    expect(isSettingsDirty(once, once)).toBe(false);
  });
});

describe("isSettingsDirty", () => {
  it("is clean when the text equals the baseline", () => {
    expect(isSettingsDirty('{\n  "a": 1\n}', '{\n  "a": 1\n}')).toBe(false);
  });

  it("is dirty on any content change", () => {
    expect(isSettingsDirty('{\n  "a": 2\n}', '{\n  "a": 1\n}')).toBe(true);
  });

  it("is dirty on a whitespace-only change (a real edit the person may want to keep)", () => {
    expect(isSettingsDirty('{"a":1} ', '{"a":1}')).toBe(true);
  });
});
