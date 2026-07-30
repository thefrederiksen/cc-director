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
        // A real Response ALWAYS carries headers, and this double must too. gatewayFetch reads
        // X-DevThrottle-Fault on a 502/504 to tell "the Gateway could not be reached" from "the Gateway
        // answered, and the Director behind it did not" (issue #1153). A double without headers is not a
        // Response, and the `as unknown as Response` cast is what let it pretend to be one - so the omission
        // surfaced as a TypeError in the transport instead of a type error here.
        headers: new Headers(),
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

  // CONTRACT CHANGE (issue #2189): these used to assert the thrown message was the internal diagnostic
  // "GET handover failed: NNN". That string was the problem - it reached the user as a bare status number and
  // it discarded the reason the server had already written. The throw now CARRIES the server's sentence.
  it("throws GatewayError on a 404, carrying the server's own reason", async () => {
    vi.stubGlobal("fetch", mockFetch(404, { error: "That session could not be found." }));

    await expect(getHandover("missing")).rejects.toBeInstanceOf(GatewayError);
    await expect(getHandover("missing")).rejects.toThrow(/That session could not be found/);
    // The internal diagnostic must not survive onto the error a surface may render.
    await expect(getHandover("missing")).rejects.not.toThrow(/GET handover failed/);
  });

  it("throws GatewayError on a 502 (owning Director offline), as a sentence not a number", async () => {
    vi.stubGlobal("fetch", mockFetch(502, {}));

    // No reason in the body, so the fallback names the action - still never a bare status.
    await expect(getHandover("s1")).rejects.toBeInstanceOf(GatewayError);
    await expect(getHandover("s1")).rejects.toThrow(/handover information/);
    await expect(getHandover("s1")).rejects.not.toThrow(/GET handover failed/);
  });
});
