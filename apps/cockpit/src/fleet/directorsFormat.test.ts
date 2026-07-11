import { describe, expect, it } from "vitest";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import { ENDPOINT_STATE_UNREACHABLE_BY_NAME, type FleetDirector, type MachineError } from "@devthrottle/client-core/fleet/fleetClient";
import { directorStatus, epochOf, repoNamesOf } from "./directorsFormat";

function director(overrides: Partial<FleetDirector> = {}): FleetDirector {
  return {
    directorId: "dir-1",
    machineName: "SOREN_NORTH",
    user: "soren",
    version: "1.1.0",
    lastSeen: "2026-07-11T12:00:00Z",
    ...overrides,
  };
}

function machineError(): MachineError {
  return { directorId: "dir-1", error: "connection refused" } as MachineError;
}

function session(overrides: Partial<SessionDto> = {}): SessionDto {
  return { sessionId: "s1", directorId: "dir-1", repoPath: "C:\\repos\\devthrottle", ...overrides } as SessionDto;
}

describe("directorStatus", () => {
  it("reports OK for a healthy Director", () => {
    const status = directorStatus(director(), undefined);
    expect(status.label).toBe("OK");
    expect(status.className).toBe("dstat-ok");
    expect(status.rank).toBe(0);
  });

  it("reports unreachable-by-name with the highest rank", () => {
    const status = directorStatus(
      director({ advertisedEndpointState: ENDPOINT_STATE_UNREACHABLE_BY_NAME }),
      undefined,
    );
    expect(status.label).toBe("UNREACHABLE BY NAME");
    expect(status.rank).toBe(3);
  });

  it("reports a Gateway reachability error", () => {
    const status = directorStatus(director(), machineError());
    expect(status.label).toBe("UNREACHABLE");
    expect(status.className).toBe("dstat-warn");
    expect(status.rank).toBe(3);
    expect(status.title).toBe("connection refused");
  });

  it("reports a terminal-stream failure", () => {
    const status = directorStatus(director({ streamVerifyError: "ws upgrade failed" }), undefined);
    expect(status.label).toBe("TERMINAL STREAM DOWN");
    expect(status.rank).toBe(2);
  });

  it("prefers unreachable-by-name over a stream failure (precedence)", () => {
    const status = directorStatus(
      director({ advertisedEndpointState: ENDPOINT_STATE_UNREACHABLE_BY_NAME, streamVerifyError: "x" }),
      undefined,
    );
    expect(status.label).toBe("UNREACHABLE BY NAME");
  });

  it("ranks a healthy Director below an unhealthy one so a descending sort surfaces problems", () => {
    const ok = directorStatus(director(), undefined).rank;
    const bad = directorStatus(director(), machineError()).rank;
    expect(bad).toBeGreaterThan(ok);
  });
});

describe("epochOf", () => {
  it("parses a valid time to its epoch", () => {
    expect(epochOf("2026-07-11T12:00:00Z")).toBe(Date.parse("2026-07-11T12:00:00Z"));
  });

  it("returns 0 for an absent or unparseable time so it sorts oldest", () => {
    expect(epochOf(null)).toBe(0);
    expect(epochOf("")).toBe(0);
    expect(epochOf("nonsense")).toBe(0);
  });
});

describe("repoNamesOf", () => {
  it("collects distinct repository basenames", () => {
    const repos = repoNamesOf([
      session({ repoPath: "C:\\repos\\devthrottle" }),
      session({ repoPath: "D:\\ReposFred\\cc-consult" }),
      session({ repoPath: "C:\\repos\\devthrottle" }),
    ]);
    expect(repos).toEqual(["devthrottle", "cc-consult"]);
  });

  it("drops the empty placeholder so it is not a false search hit", () => {
    const repos = repoNamesOf([session({ repoPath: undefined }), session({ repoPath: "" })]);
    expect(repos).toEqual([]);
  });

  it("returns nothing for no sessions", () => {
    expect(repoNamesOf([])).toEqual([]);
  });
});
