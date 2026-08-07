import { afterEach, describe, expect, it, vi } from "vitest";
import { listMissions } from "./missions";

// GET /missions is the read that makes a mission with no sessions on it visible at all. What matters here
// is that a BROKEN answer never reaches the screen looking like an EMPTY one: "the Gateway is answering
// wrongly" and "you have no missions" are different facts, and the second is the one that renders as a
// calm, complete-looking page.

function jsonResponse(body: unknown, status = 200): Response {
  return {
    ok: status >= 200 && status < 300,
    status,
    json: async () => body,
  } as unknown as Response;
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function stubFetch(res: Response) {
  const fetchMock = vi.fn().mockResolvedValue(res);
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

describe("listMissions", () => {
  it("returns the missions the Gateway sent", async () => {
    stubFetch(
      jsonResponse([
        { missionId: "m1", missionName: "Release 2.0.1" },
        { missionId: "m2", missionName: "Banya", parentMissionId: null },
      ]),
    );

    const missions = await listMissions();

    expect(missions.map((m) => m.missionName)).toEqual(["Release 2.0.1", "Banya"]);
  });

  it("returns an empty list when the account genuinely has no missions", async () => {
    stubFetch(jsonResponse([]));
    await expect(listMissions()).resolves.toEqual([]);
  });

  it("THROWS on a 200 whose body is not an array, rather than reporting no missions", async () => {
    stubFetch(jsonResponse({ missions: [{ missionId: "m1", missionName: "Release 2.0.1" }] }));
    await expect(listMissions()).rejects.toThrow(/shape this app cannot read/i);
  });

  it("THROWS on a null body rather than reporting no missions", async () => {
    stubFetch(jsonResponse(null));
    await expect(listMissions()).rejects.toThrow(/shape this app cannot read/i);
  });

  it("THROWS when a mission has no id - it could never be joined or attached to", async () => {
    stubFetch(jsonResponse([{ missionId: "", missionName: "Nameless" }]));
    await expect(listMissions()).rejects.toThrow(/no id/i);

    stubFetch(jsonResponse([{ missionName: "Nameless" }]));
    await expect(listMissions()).rejects.toThrow(/no id/i);
  });

  it("throws on a non-2xx", async () => {
    stubFetch(jsonResponse({ error: "nope" }, 403));
    await expect(listMissions()).rejects.toBeInstanceOf(Error);
  });
});
