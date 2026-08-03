import { afterEach, describe, expect, it, vi } from "vitest";
import { bannerFromMachineErrors, getFleetDirectors, getInterrupted } from "./fleetClient";

// Sibling non-array guards for the fleet list reads (issue #1050). GET /directors and GET /interrupted
// are contracted to return JSON arrays; the Directors table and the Fleet interrupted-cards grouping
// call .map() on the result, so a malformed non-array 200 body must degrade to [] rather than throw
// "x.map is not a function" - the same guard getRecordings now applies.

function mockFetch(status: number, body: unknown) {
  return vi.fn(
    async (_input: RequestInfo | URL, _init?: RequestInit): Promise<Response> =>
      ({
        ok: status >= 200 && status < 300,
        status,
        json: async () => body,
      }) as unknown as Response,
  );
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe("getFleetDirectors", () => {
  it("returns the array for a well-formed array body", async () => {
    vi.stubGlobal("fetch", mockFetch(200, [{ directorId: "d1" }, { directorId: "d2" }]));
    const list = await getFleetDirectors();
    expect(list).toHaveLength(2);
  });

  it("returns [] for a non-array object body without throwing", async () => {
    vi.stubGlobal("fetch", mockFetch(200, { unexpected: true }));
    expect(await getFleetDirectors()).toEqual([]);
  });

  it("throws on a transport error (non-2xx)", async () => {
    vi.stubGlobal("fetch", mockFetch(500, {}));
    await expect(getFleetDirectors()).rejects.toThrow(/GET \/directors failed/);
  });
});

describe("getInterrupted", () => {
  it("returns the array for a well-formed array body", async () => {
    vi.stubGlobal("fetch", mockFetch(200, [{ sessionId: "s1", deadDirectorId: "d1", deadPid: 1, reportedByDirectorId: "d2" }]));
    const list = await getInterrupted();
    expect(list).toHaveLength(1);
  });

  it("returns [] for a non-array null body without throwing", async () => {
    vi.stubGlobal("fetch", mockFetch(200, null));
    expect(await getInterrupted()).toEqual([]);
  });

  it("throws on a transport error (non-2xx)", async () => {
    vi.stubGlobal("fetch", mockFetch(500, {}));
    await expect(getInterrupted()).rejects.toThrow(/GET \/interrupted failed/);
  });
});

// THE WARNING LINE MUST NOT GO SILENT AGAINST AN OLDER GATEWAY. Found by review. The Fleet Map stopped
// building its own banner from machineErrors and started printing the Gateway's folded sentence - so an
// older Gateway, which sends no such field, would have left the map with no outage warning at all. Absent
// and null are therefore different answers here: null is "nothing is wrong", absent is "cannot answer".
describe("bannerFromMachineErrors - the older-Gateway fallback", () => {
  it("says nothing when there are no errors", () => {
    expect(bannerFromMachineErrors([])).toBeNull();
  });

  it("names one unreachable director", () => {
    const line = bannerFromMachineErrors([
      { directorId: "d1", machineName: "SOREN_NORTH", error: "director not connected to the tunnel" },
    ]);
    expect(line).toContain("1 director could not be reached");
    expect(line).toContain("SOREN_NORTH");
    // Even the fallback refuses the old noun: these rows have always been per-Director.
    expect(line).not.toContain("machine could not be reached");
  });

  it("counts several as directors, not machines", () => {
    const line = bannerFromMachineErrors([
      { directorId: "d1", machineName: "SOREN_NORTH" },
      { directorId: "d2", machineName: "MAC_MINI" },
    ]);
    expect(line).toContain("2 directors could not be reached");
  });

  it("names an unidentified machine rather than dropping the row", () => {
    const line = bannerFromMachineErrors([{ directorId: "d1", machineName: "  " }]);
    expect(line).toContain("an unidentified machine");
  });
});
