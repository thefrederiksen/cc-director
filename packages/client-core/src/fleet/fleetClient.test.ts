import { afterEach, describe, expect, it, vi } from "vitest";
import { getFleetDirectors, getInterrupted } from "./fleetClient";

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
