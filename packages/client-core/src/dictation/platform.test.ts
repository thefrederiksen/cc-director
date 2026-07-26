import { describe, expect, it } from "vitest";
import { classifyPlatform } from "./platform";

// The platform bucket rides on every quality sample so the report can say "your phone microphone
// beats your Windows headset". The rule worth pinning: a device that cannot be determined is
// "unknown", never a guessed bucket - a wrong bucket poisons the comparison silently, and the raw
// evidence must always come along so a wrong bucket can be diagnosed rather than argued about.

describe("classifyPlatform", () => {
  it("classifies from Client-Hints when the browser has them", () => {
    expect(classifyPlatform({ userAgentData: { platform: "Windows", mobile: false } }).platform).toBe("windows");
    expect(classifyPlatform({ userAgentData: { platform: "macOS", mobile: false } }).platform).toBe("mac");
    expect(classifyPlatform({ userAgentData: { platform: "Android", mobile: true } }).platform).toBe("mobile");
  });

  it("trusts the mobile flag over the platform string", () => {
    // A Windows tablet PWA declaring itself mobile IS the phone/tablet surface for this report.
    expect(classifyPlatform({ userAgentData: { platform: "Windows", mobile: true } }).platform).toBe("mobile");
  });

  it("classifies an unfamiliar Client-Hints platform as unknown rather than guessing", () => {
    const result = classifyPlatform({ userAgentData: { platform: "Chrome OS", mobile: false } });
    expect(result.platform).toBe("unknown");
    expect(result.platformRaw).toContain("Chrome OS");
  });

  it("falls back to the user agent string on browsers without Client-Hints", () => {
    expect(
      classifyPlatform({
        userAgent: "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15",
        platform: "iPhone",
      }).platform,
    ).toBe("mobile");
    expect(
      classifyPlatform({
        userAgent: "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15",
        platform: "MacIntel",
        maxTouchPoints: 0,
      }).platform,
    ).toBe("mac");
    expect(
      classifyPlatform({
        userAgent: "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Gecko/20100101 Firefox/128.0",
        platform: "Win32",
      }).platform,
    ).toBe("windows");
  });

  it("tells an iPad apart from a Mac by its touch screen", () => {
    // iPadOS Safari reports a desktop Mac user agent; the multi-touch screen is the tell.
    expect(
      classifyPlatform({
        userAgent: "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15",
        platform: "MacIntel",
        maxTouchPoints: 5,
      }).platform,
    ).toBe("mobile");
  });

  it("reports unknown with the evidence when nothing identifies the platform", () => {
    const result = classifyPlatform({ userAgent: "SomethingExotic/1.0", platform: "Haiku" });
    expect(result.platform).toBe("unknown");
    expect(result.platformRaw).toContain("SomethingExotic");
  });

  it("caps the raw evidence so it cannot become a place to park arbitrary text", () => {
    const result = classifyPlatform({ userAgent: "x".repeat(5000) });
    expect(result.platformRaw.length).toBeLessThanOrEqual(160);
  });
});
