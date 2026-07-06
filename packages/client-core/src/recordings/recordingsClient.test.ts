import { afterEach, describe, expect, it, vi } from "vitest";
import { getRecordings } from "./recordingsClient";

// Non-array robustness for the transcripts list (issue #1050). The /ingest/recordings contract is a
// JSON array, but a malformed or unexpected 200 body (an object, a bare null, a string) must never
// reach TranscriptsView as a non-array - it does list.map() and would throw "r.map is not a function".
// getRecordings must degrade any non-array body to an empty list instead of throwing.

// Mirror the real fetch signature (input, init) so the mock's recorded call arguments are typed as a
// two-element tuple (matches the awarenessClient test helper, issue #1010).
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

describe("getRecordings", () => {
  it("returns the array as-is for a well-formed array body", async () => {
    const rows = [
      { recordingId: "r1", title: "one", startedAt: "2026-07-06T10:00:00Z", state: "transcribed" },
      { recordingId: "r2", title: "two", startedAt: "2026-07-06T10:05:00Z", state: "filed" },
    ];
    vi.stubGlobal("fetch", mockFetch(200, rows));

    const list = await getRecordings();

    expect(Array.isArray(list)).toBe(true);
    expect(list).toHaveLength(2);
    expect(list[1].recordingId).toBe("r2");
  });

  it("returns [] for a non-array object body without throwing", async () => {
    vi.stubGlobal("fetch", mockFetch(200, { error: "unexpected shape" }));
    const list = await getRecordings();
    expect(list).toEqual([]);
  });

  it("returns [] for a null body without throwing", async () => {
    vi.stubGlobal("fetch", mockFetch(200, null));
    const list = await getRecordings();
    expect(list).toEqual([]);
  });

  it("returns [] for a string body without throwing", async () => {
    vi.stubGlobal("fetch", mockFetch(200, "not an array"));
    const list = await getRecordings();
    expect(list).toEqual([]);
  });

  it("throws on a transport error (non-2xx)", async () => {
    vi.stubGlobal("fetch", mockFetch(500, {}));
    await expect(getRecordings()).rejects.toThrow(/GET \/ingest\/recordings failed/);
  });
});
