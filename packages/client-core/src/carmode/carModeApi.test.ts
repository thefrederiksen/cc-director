import { afterEach, describe, expect, it, vi } from "vitest";
import { carModeTurn, speakCarModeText, transcribeCarModeAudio } from "./carModeApi";
import { CreditsError, GatewayError } from "../api/client";

// The Car Mode Gateway calls must fail LOUD and SPECIFIC (mission decision 8): a money refusal (402) is
// the shared CreditsError, and any other non-2xx is a GatewayError carrying the status - never a silent
// success or a swallowed error. These stub fetch to prove the mapping without a network.

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

describe("transcribeCarModeAudio", () => {
  it("returns the trimmed transcript on success", async () => {
    vi.stubGlobal("fetch", mockFetch(200, { transcript: "  how many need me  " }));
    const text = await transcribeCarModeAudio(new Blob(["x"]));
    expect(text).toBe("how many need me");
  });

  it("maps 402 to the shared CreditsError", async () => {
    vi.stubGlobal("fetch", mockFetch(402, { state: "NeedsCredits", text: "Add credits." }));
    await expect(transcribeCarModeAudio(new Blob(["x"]))).rejects.toBeInstanceOf(CreditsError);
  });

  it("maps another non-2xx to a GatewayError with the status", async () => {
    vi.stubGlobal("fetch", mockFetch(502, { error: "upstream down" }));
    await expect(transcribeCarModeAudio(new Blob(["x"]))).rejects.toMatchObject({ status: 502 });
  });
});

describe("speakCarModeText", () => {
  it("returns the audio blob on success", async () => {
    const audio = new Blob(["mp3"], { type: "audio/mpeg" });
    vi.stubGlobal("fetch", mockFetch(200, {}, audio));
    const clip = await speakCarModeText("hello");
    expect(clip).toBe(audio);
  });

  it("maps 402 to the shared CreditsError", async () => {
    vi.stubGlobal("fetch", mockFetch(402, { state: "CapReached" }));
    await expect(speakCarModeText("hi")).rejects.toBeInstanceOf(CreditsError);
  });

  it("maps another non-2xx to a GatewayError", async () => {
    vi.stubGlobal("fetch", mockFetch(500, { error: "tts failed" }));
    await expect(speakCarModeText("hi")).rejects.toBeInstanceOf(GatewayError);
  });
});

describe("carModeTurn", () => {
  it("parses the spoken reply, actions, and pendingConfirmation", async () => {
    vi.stubGlobal("fetch", mockFetch(200, {
      spoken: "  Deleting Old Worker is permanent. Confirm?  ",
      actions: [{ tool: "message_session", summary: "Messaged X" }],
      pendingConfirmation: true,
    }));
    const result = await carModeTurn("delete old worker");
    expect(result.spoken).toBe("Deleting Old Worker is permanent. Confirm?");
    expect(result.actions).toHaveLength(1);
    expect(result.pendingConfirmation).toBe(true);
  });

  it("defaults actions to an empty array and pendingConfirmation to false", async () => {
    vi.stubGlobal("fetch", mockFetch(200, { spoken: "Nothing needs you." }));
    const result = await carModeTurn("who needs me");
    expect(result.actions).toEqual([]);
    expect(result.pendingConfirmation).toBe(false);
  });

  it("maps 402 to the shared CreditsError", async () => {
    vi.stubGlobal("fetch", mockFetch(402, { state: "NeedsCredits" }));
    await expect(carModeTurn("hi")).rejects.toBeInstanceOf(CreditsError);
  });

  it("maps another non-2xx to a GatewayError with the status", async () => {
    vi.stubGlobal("fetch", mockFetch(502, { error: "car mode down" }));
    await expect(carModeTurn("hi")).rejects.toMatchObject({ status: 502 });
  });
});
