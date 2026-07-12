import { afterEach, describe, expect, it, vi } from "vitest";
import { carModeTurn, postCarModeTelemetry, postCarModeWarmup, speakCarModeText, transcribeCarModeAudio } from "./carModeApi";
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

  it("parses the server turnId and per-stage timing when present", async () => {
    vi.stubGlobal("fetch", mockFetch(200, {
      turnId: "abc123",
      spoken: "You have two sessions waiting.",
      timing: {
        totalMs: 812.5,
        modelCallCount: 2,
        modelMsTotal: 760,
        modelMs: [400, 360],
        fleetReadCount: 1,
        fleetReadMsTotal: 30,
        rounds: 2,
      },
    }));
    const result = await carModeTurn("who needs me");
    expect(result.turnId).toBe("abc123");
    expect(result.timing?.fleetReadCount).toBe(1);
    expect(result.timing?.modelMs).toEqual([400, 360]);
  });

  it("defaults turnId to empty and timing to null when the server omits them", async () => {
    vi.stubGlobal("fetch", mockFetch(200, { spoken: "Hi there." }));
    const result = await carModeTurn("hello");
    expect(result.turnId).toBe("");
    expect(result.timing).toBeNull();
  });
});

describe("postCarModeTelemetry", () => {
  const record = {
    turnId: "t1", pauseToTranscribeMs: 900, transcodeMs: 120, brainMs: 1200, ttsMs: 300, firstAudioMs: 350,
    totalTurnMs: 2450, chunks: 1, playMs: 4200, completed: true, serverTotalMs: 1100, modelCallCount: 1,
    modelMsTotal: 1050, modelMs: [1050], fleetReadCount: 0, fleetReadMsTotal: 0, rounds: 1, commandChars: 20,
    replyChars: 40, actionsCount: 0, pendingConfirmation: false,
  };

  it("posts the record to /carmode/telemetry", async () => {
    const fetchMock = mockFetch(200, { recorded: true, held: 1 });
    vi.stubGlobal("fetch", fetchMock);
    await postCarModeTelemetry(record);
    expect(fetchMock).toHaveBeenCalledWith("/carmode/telemetry", expect.objectContaining({ method: "POST" }));
  });

  it("never throws when the post fails (best-effort observability)", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => { throw new Error("network down"); }));
    await expect(postCarModeTelemetry(record)).resolves.toBeUndefined();
  });
});

describe("postCarModeWarmup", () => {
  it("posts to /carmode/warmup", async () => {
    const fetchMock = mockFetch(200, { warmed: true });
    vi.stubGlobal("fetch", fetchMock);
    await postCarModeWarmup();
    expect(fetchMock).toHaveBeenCalledWith("/carmode/warmup", expect.objectContaining({ method: "POST" }));
  });

  it("never throws when the warmup ping fails (best-effort)", async () => {
    vi.stubGlobal("fetch", vi.fn(async () => { throw new Error("down"); }));
    await expect(postCarModeWarmup()).resolves.toBeUndefined();
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
