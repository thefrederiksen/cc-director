import { describe, it, expect } from "vitest";
import {
  reachabilityFor,
  reachabilityLastSeen,
  REACHABILITY_ONLINE,
  REACHABILITY_WOBBLY,
  REACHABILITY_OFFLINE,
  type DirectorReachability,
} from "./fleetClient";

// The Cockpit-side join + label for the three-state (Online/Wobbly/Offline) rendering (issue #1215).

const directors: DirectorReachability[] = [
  { directorId: "d-online", state: REACHABILITY_ONLINE, lastSeenAgeSeconds: 0 },
  { directorId: "d-wobbly", state: REACHABILITY_WOBBLY, lastSeenAgeSeconds: 20, machineName: "M1" },
  { directorId: "d-offline", state: REACHABILITY_OFFLINE, lastSeenAgeSeconds: 3600 },
];

describe("reachabilityFor", () => {
  it("joins a session to its Director by directorId", () => {
    expect(reachabilityFor(directors, "d-wobbly")?.state).toBe(REACHABILITY_WOBBLY);
    expect(reachabilityFor(directors, "d-offline")?.state).toBe(REACHABILITY_OFFLINE);
  });

  it("returns undefined for an unknown or empty directorId, so the session renders as Online", () => {
    expect(reachabilityFor(directors, "nope")).toBeUndefined();
    expect(reachabilityFor(directors, "")).toBeUndefined();
    expect(reachabilityFor(directors, null)).toBeUndefined();
    expect(reachabilityFor([], "d-wobbly")).toBeUndefined();
  });
});

describe("reachabilityLastSeen", () => {
  it("is empty while Online (age 0 or missing)", () => {
    expect(reachabilityLastSeen(0)).toBe("");
    expect(reachabilityLastSeen(null)).toBe("");
    expect(reachabilityLastSeen(undefined)).toBe("");
  });

  it("reads in seconds, minutes, then hours", () => {
    expect(reachabilityLastSeen(20)).toBe("last seen 20s ago");
    expect(reachabilityLastSeen(90)).toBe("last seen 1m ago");
    expect(reachabilityLastSeen(7200)).toBe("last seen 2h ago");
  });
});
