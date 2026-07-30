import { afterEach, describe, expect, it, vi } from "vitest";
import { postBrainWarmup, speakText } from "./brainApi";
import { CreditsError, GatewayError } from "../api/client";

// The fleet brain's browser calls must fail LOUD and SPECIFIC: a money refusal (402) is the shared
// CreditsError, and any other non-2xx is a GatewayError carrying the status - never a silent success or a
// swallowed error. These stub fetch to prove the mapping without a network.
//
// The transcribe / turn / help / diagnostics calls that used to be tested here went with Car Mode. What
// remains is what the Assistant uses, checked for the same two properties it always was.

function mockFetch(status: number, body: unknown, blob?: Blob) {
  return vi.fn(
    async (): Promise<Response> =>
      ({
        ok: status >= 200 && status < 300,
        status,
        json: async () => body,
        blob: async () => blob ?? new Blob([]),
      }) as unknown as Response,
  );
}

afterEach(() => vi.restoreAllMocks());

describe("speakText", () => {
  it("returns the audio blob on success", async () => {
    const audio = new Blob(["mp3"], { type: "audio/mpeg" });
    vi.stubGlobal("fetch", mockFetch(200, {}, audio));
    const clip = await speakText("hello");
    expect(clip).toBe(audio);
  });

  it("maps 402 to the shared CreditsError", async () => {
    vi.stubGlobal("fetch", mockFetch(402, { state: "CapReached" }));
    await expect(speakText("hi")).rejects.toBeInstanceOf(CreditsError);
  });

  it("maps another non-2xx to a GatewayError", async () => {
    vi.stubGlobal("fetch", mockFetch(500, { error: "tts failed" }));
    await expect(speakText("hi")).rejects.toBeInstanceOf(GatewayError);
  });
});

describe("postBrainWarmup", () => {
  it("posts to the brain's own warmup door", async () => {
    const fetchMock = mockFetch(200, { warmed: true });
    vi.stubGlobal("fetch", fetchMock);

    await postBrainWarmup();

    const [url] = (fetchMock as unknown as { mock: { calls: unknown[][] } }).mock.calls[0];
    expect(url).toBe("/brain/warmup");
  });

  // Best-effort: losing a warmup ping must never disrupt a turn, so a failure is swallowed here rather than
  // thrown into the caller's loop.
  it("never throws when the warmup ping fails", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => { throw new Error("offline"); }));
    await expect(postBrainWarmup()).resolves.toBeUndefined();
  });
});
