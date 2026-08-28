import { describe, expect, it } from "vitest";
import { summarise, toAvailability, type PlatformSpeechProbe } from "./probe";

function probe(over: Partial<PlatformSpeechProbe> = {}): PlatformSpeechProbe {
  return {
    checkedAt: "2026-08-28T20:00:00.000Z",
    userAgent: "test",
    hasRecogniser: true,
    prefixed: false,
    hasAvailabilityQuery: true,
    acceptsProcessLocally: true,
    languages: [],
    ...over,
  };
}

describe("toAvailability", () => {
  it("passes the four specified states through", () => {
    expect(toAvailability("available")).toBe("available");
    expect(toAvailability("downloadable")).toBe("downloadable");
    expect(toAvailability("downloading")).toBe("downloading");
    expect(toAvailability("unavailable")).toBe("unavailable");
  });

  it("understands the older boolean answer", () => {
    expect(toAvailability(true)).toBe("available");
    expect(toAvailability(false)).toBe("unavailable");
  });

  it("calls anything else an error rather than guessing", () => {
    expect(toAvailability("yes")).toBe("error");
    expect(toAvailability(undefined)).toBe("error");
    expect(toAvailability(null)).toBe("error");
  });
});

describe("summarise", () => {
  it("says so when there is no recogniser at all", () => {
    expect(summarise(probe({ hasRecogniser: false }))).toMatch(/No speech recogniser/);
  });

  it("rejects the old server-backed recogniser for all-day listening", () => {
    expect(summarise(probe({ hasAvailabilityQuery: false }))).toMatch(/sends audio to a server/);
  });

  it("reports the languages that are ready now", () => {
    const message = summarise(
      probe({
        languages: [
          { language: "en-US", onDevice: "available", anywhere: "available" },
          { language: "da-DK", onDevice: "unavailable", anywhere: "available" },
        ],
      }),
    );
    expect(message).toMatch(/en-US/);
    expect(message).not.toMatch(/da-DK/);
  });

  // Treating "the model is not here yet" the same as "this will never work" would write off a device
  // that only needed to be asked to fetch it.
  it("keeps downloadable apart from unavailable", () => {
    const message = summarise(
      probe({ languages: [{ language: "en-US", onDevice: "downloadable", anywhere: "available" }] }),
    );
    expect(message).toMatch(/not here yet/);
  });

  it("says plainly when nothing works on the device", () => {
    const message = summarise(
      probe({ languages: [{ language: "en-US", onDevice: "unavailable", anywhere: "available" }] }),
    );
    expect(message).toMatch(/A model has to do the listening/);
  });
});
