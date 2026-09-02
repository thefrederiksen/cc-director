import { afterEach, describe, expect, it, vi } from "vitest";
import { createSession, GatewayError, getKnownRepositories } from "./client";

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("new session client", () => {
  const realFetch = globalThis.fetch;

  afterEach(() => {
    globalThis.fetch = realFetch;
    vi.restoreAllMocks();
  });

  it("loads and sorts the complete known-repository route for the selected Director", async () => {
    const fetchMock = vi.fn(async () => jsonResponse([
      { name: "Older", path: "/repositories/older", lastUsed: "2026-08-01T00:00:00Z" },
      { name: "Newest", path: "/repositories/newest", lastUsed: "2026-09-01T00:00:00Z" },
      { name: "Invalid", path: "", lastUsed: "2026-09-02T00:00:00Z" },
    ]));
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    const repositories = await getKnownRepositories("Director / one");

    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/directors/Director%20%2F%20one/known-repositories");
    expect(init.method).toBe("GET");
    expect(repositories.map((repository) => repository.name)).toEqual(["Newest", "Older"]);
  });

  it("surfaces a known-repository route failure instead of returning an empty list", async () => {
    globalThis.fetch = (async () => jsonResponse(
      { error: "repository storage unavailable" },
      503,
    )) as unknown as typeof fetch;

    await expect(getKnownRepositories("director")).rejects.toBeInstanceOf(GatewayError);
    await expect(getKnownRepositories("director")).rejects.toMatchObject({ status: 503 });
  });

  it("creates with the explicitly selected agent and leaves permission behavior to the Director", async () => {
    const fetchMock = vi.fn(async () => jsonResponse({ sessionId: "created-session" }, 201));
    globalThis.fetch = fetchMock as unknown as typeof fetch;

    await createSession("director-one", "  D:\\Repositories\\project  ", { agent: "RawCli" });

    const [url, init] = fetchMock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe("/directors/director-one/sessions");
    expect(init.method).toBe("POST");
    expect(JSON.parse(String(init.body))).toEqual({
      repoPath: "D:\\Repositories\\project",
      agent: "RawCli",
      wingmanEnabled: false,
    });
  });
});
