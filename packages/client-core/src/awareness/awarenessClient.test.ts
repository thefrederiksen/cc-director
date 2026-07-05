import { afterEach, describe, expect, it, vi } from "vitest";
import { generateRecap, getRecap, getTurnSummaries } from "./awarenessClient";

// Awareness Gateway reads (issue #974). These exercise the root-relative paths (Gateway-only-ingress),
// the 404-to-empty degrade on turn-summaries, and that a POST recap is issued for a fresh generation.

function mockFetch(status: number, body: unknown) {
  return vi.fn(async () =>
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

describe("getRecap", () => {
  it("reads the cached recap from the root-relative per-session path", async () => {
    const fetchMock = mockFetch(200, { sessionId: "s1", recap: "## What was done", status: "ok" });
    vi.stubGlobal("fetch", fetchMock);

    const recap = await getRecap("s1");

    expect(recap.status).toBe("ok");
    expect(recap.recap).toContain("What was done");
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe("/sessions/s1/recap");
    expect((init as RequestInit).method).toBe("GET");
  });

  it("returns the not_cached status (a 200 body, not an error) when nothing is cached", async () => {
    vi.stubGlobal("fetch", mockFetch(200, { sessionId: "s1", recap: "", status: "not_cached" }));
    const recap = await getRecap("s1");
    expect(recap.status).toBe("not_cached");
    expect(recap.recap).toBe("");
  });

  it("throws on a transport error (non-2xx)", async () => {
    vi.stubGlobal("fetch", mockFetch(500, {}));
    await expect(getRecap("s1")).rejects.toThrow(/GET recap failed/);
  });
});

describe("generateRecap", () => {
  it("issues a POST to the per-session recap path and returns the fresh recap", async () => {
    const fetchMock = mockFetch(200, { sessionId: "s1", recap: "fresh", status: "ok", model: "opus" });
    vi.stubGlobal("fetch", fetchMock);

    const recap = await generateRecap("s1");

    expect(recap.recap).toBe("fresh");
    expect(recap.model).toBe("opus");
    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe("/sessions/s1/recap");
    expect((init as RequestInit).method).toBe("POST");
  });
});

describe("getTurnSummaries", () => {
  it("reads the ordered summaries from the root-relative path", async () => {
    const fetchMock = mockFetch(200, {
      sessionId: "s1",
      summaries: [
        { generatedAt: "2026-07-05T10:00:00Z", headline: "first", needsUser: "no" },
        { generatedAt: "2026-07-05T10:05:00Z", headline: "second", needsUser: "question" },
      ],
    });
    vi.stubGlobal("fetch", fetchMock);

    const res = await getTurnSummaries("s1");

    expect(res.summaries).toHaveLength(2);
    expect(res.summaries[1].headline).toBe("second");
    expect(fetchMock.mock.calls[0][0]).toBe("/sessions/s1/turn-summaries");
  });

  it("degrades a 404 to an empty summary list (old Director / unknown session)", async () => {
    vi.stubGlobal("fetch", mockFetch(404, {}));
    const res = await getTurnSummaries("s1");
    expect(res.summaries).toEqual([]);
    expect(res.sessionId).toBe("s1");
  });

  it("throws on a non-404 transport error", async () => {
    vi.stubGlobal("fetch", mockFetch(500, {}));
    await expect(getTurnSummaries("s1")).rejects.toThrow(/GET turn-summaries failed/);
  });
});
