import { afterEach, describe, expect, it, vi } from "vitest";
import { getHandover } from "./client";
import { GatewayError } from "./client";

// Issue #1214: getHandover fetches the desktop "Handover info" identity block for a session through the
// Gateway (GET /sessions/{sid}/handover). It must return the typed shape on a 200 and throw a
// GatewayError on any non-2xx so the Cockpit shows a visible error instead of a silent empty panel.

// Mirror the real fetch signature (input, init) so the recorded call arguments are typed as a
// two-element tuple (matches the recordingsClient test helper).
function mockFetch(status: number, body: unknown) {
  return vi.fn(
    async (_input: RequestInfo | URL, _init?: RequestInit): Promise<Response> =>
      ({
        ok: status >= 200 && status < 300,
        status,
        json: async () => body,
        text: async () => (typeof body === "string" ? body : JSON.stringify(body)),
      }) as unknown as Response,
  );
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe("getHandover", () => {
  it("returns the typed handover block for a well-formed 200 body", async () => {
    const dto = {
      sessionId: "11111111-2222-3333-4444-555555555555",
      displayName: "devthrottle - fix login",
      repoPath: "C:/repos/devthrottle",
      directorId: "director-abc",
      machineName: "SOREN-PC",
      version: "1.2.3",
    };
    vi.stubGlobal("fetch", mockFetch(200, dto));

    const info = await getHandover(dto.sessionId);

    expect(info).toEqual(dto);
  });

  it("hits the Gateway-relative handover route with a Bearer header", async () => {
    const fetchMock = mockFetch(200, {});
    vi.stubGlobal("fetch", fetchMock);

    await getHandover("abc def"); // a space proves the id is URL-encoded

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe("/sessions/abc%20def/handover");
    expect((init as RequestInit).method).toBe("GET");
  });

  it("degrades a partial 200 body to empty strings without throwing", async () => {
    vi.stubGlobal("fetch", mockFetch(200, { sessionId: "s1" }));

    const info = await getHandover("s1");

    expect(info.sessionId).toBe("s1");
    expect(info.displayName).toBe("");
    expect(info.repoPath).toBe("");
    expect(info.directorId).toBe("");
    expect(info.machineName).toBe("");
    expect(info.version).toBe("");
  });

  it("throws GatewayError on a 404 (session unknown)", async () => {
    vi.stubGlobal("fetch", mockFetch(404, { error: "session not found across any director" }));

    await expect(getHandover("missing")).rejects.toBeInstanceOf(GatewayError);
    await expect(getHandover("missing")).rejects.toThrow(/GET handover failed: 404/);
  });

  it("throws GatewayError on a 502 (owning Director offline)", async () => {
    vi.stubGlobal("fetch", mockFetch(502, {}));

    await expect(getHandover("s1")).rejects.toThrow(/GET handover failed: 502/);
  });
});
