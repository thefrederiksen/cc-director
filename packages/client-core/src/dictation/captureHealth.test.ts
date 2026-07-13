import { describe, expect, it } from "vitest";
import { captureLossWarning, deficitFraction } from "./captureHealth";

// captureLossWarning is what turns the silent capture-health measurement (issue #863) into a
// user-visible warning in the dictation dialog: a material deficit means words may be missing from
// the transcript, and the dialog parks instead of auto-committing. The threshold must match the
// console log's, and small codec slack must never nag the user.

describe("deficitFraction", () => {
  it("is 0 for a healthy capture and clamps codec padding at 0", () => {
    expect(deficitFraction({ recordedMs: 10000, decodedSeconds: 10, sourceBytes: 1 })).toBe(0);
    // Decoded slightly LONGER than the wall-clock (codec priming/padding) must not go negative.
    expect(deficitFraction({ recordedMs: 10000, decodedSeconds: 10.4, sourceBytes: 1 })).toBe(0);
  });

  it("measures the missing fraction of the recording wall-clock", () => {
    // Recorded 52s, decoded 39s: a quarter of the audio never made it.
    expect(deficitFraction({ recordedMs: 52000, decodedSeconds: 39, sourceBytes: 1 })).toBeCloseTo(0.25);
  });
});

describe("captureLossWarning", () => {
  it("returns null for a healthy capture and for normal codec slack (under the threshold)", () => {
    expect(captureLossWarning({ recordedMs: 10000, decodedSeconds: 10, sourceBytes: 1 })).toBeNull();
    // 5% short is codec slack, not loss.
    expect(captureLossWarning({ recordedMs: 10000, decodedSeconds: 9.5, sourceBytes: 1 })).toBeNull();
  });

  it("names roughly how many seconds went missing on a material loss", () => {
    // Recorded 52s, decoded 38s: ~14 seconds missing.
    const warning = captureLossWarning({ recordedMs: 52000, decodedSeconds: 38, sourceBytes: 1 });
    expect(warning).not.toBeNull();
    expect(warning).toContain("About 14 seconds");
    expect(warning).toContain("was not captured");
  });

  it("uses the singular for a one-second loss and reports at least one second", () => {
    // 20% of a 4s recording is 0.8s - rounds up to the minimum of 1 second, singular.
    const warning = captureLossWarning({ recordedMs: 4000, decodedSeconds: 3.2, sourceBytes: 1 });
    expect(warning).not.toBeNull();
    expect(warning).toContain("About 1 second ");
  });
});
