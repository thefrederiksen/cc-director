import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The durable upload driver (issue #958). These tests exercise the /ingest contract order and the
// durability guarantees against a mocked store and a mocked fetch:
//   - register -> per-segment PUT (with the X-Chunk-Sha256 header) -> complete, in that order;
//   - a segment already confirmed on a prior pass is NEVER re-sent (the per-segment resume point);
//   - each confirmed segment is marked in the store IMMEDIATELY, before the next byte moves;
//   - a transport failure leaves the recording in "retry" with a plain reason - saved, never lost;
//   - the completeness gate's 409 re-arms EXACTLY the named indices and never loops complete;
//   - 202 on complete is the ONLY terminal success: the local copy is deleted then, and only then;
//   - a recording in "uploaded" but not completed goes straight to complete without re-sending audio;
//   - resumePendingRecordingUploads drives every recording with upload work left - uploading is
//     automatic, so even a legacy "ready" row never waits for a Send press (devthrottle_internal#966).

vi.mock("../api/client", () => ({ authHeaders: () => ({ Authorization: "Bearer test" }) }));
vi.mock("./recordingStore", () => ({
  getRecording: vi.fn(),
  listChunks: vi.fn(),
  listRecordings: vi.fn(),
  markChunkUploaded: vi.fn(),
  saveRecording: vi.fn(),
  deleteRecording: vi.fn(),
}));

import {
  buildManifest,
  driveRecordingUpload,
  extForCodec,
  resumePendingRecordingUploads,
  sha256Hex,
} from "./ingestUpload";
import {
  deleteRecording,
  getRecording,
  listChunks,
  listRecordings,
  markChunkUploaded,
  saveRecording,
  type LocalChunk,
  type LocalRecording,
} from "./recordingStore";

function makeRecording(overrides: Partial<LocalRecording> = {}): LocalRecording {
  return {
    recordingId: "rec-1",
    title: "Test recording",
    deviceId: "device-1",
    startedAt: "2026-07-27T10:00:00Z",
    endedAt: "2026-07-27T10:02:00Z",
    codec: "webm-opus",
    sampleRateHz: 48000,
    channels: 1,
    state: "queued",
    completed: false,
    segments: 2,
    durationMs: 120000,
    notes: [{ tMs: 5000, text: "a note" }],
    createdAt: 1,
    ...overrides,
  };
}

function makeChunk(index: number, uploaded = false): LocalChunk {
  return {
    recordingId: "rec-1",
    index,
    blob: new Blob([`audio-${index}`], { type: "audio/webm" }),
    startMs: index * 60000,
    durationMs: 60000,
    bytes: 8,
    sha256: `hash-${index}`,
    uploaded,
  };
}

function okJson(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

let fetchMock: ReturnType<typeof vi.fn>;

beforeEach(() => {
  fetchMock = vi.fn();
  vi.stubGlobal("fetch", fetchMock);
  vi.mocked(getRecording).mockReset();
  vi.mocked(listChunks).mockReset();
  vi.mocked(listRecordings).mockReset();
  vi.mocked(markChunkUploaded).mockReset().mockResolvedValue(undefined);
  vi.mocked(saveRecording).mockReset().mockResolvedValue(undefined);
  vi.mocked(deleteRecording).mockReset().mockResolvedValue(undefined);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("sha256Hex", () => {
  it("produces the lowercase hex digest the server computes (known vector)", async () => {
    // SHA-256("abc") - the classic FIPS 180-2 test vector.
    const hex = await sha256Hex(new Blob(["abc"]));
    expect(hex).toBe("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
  });
});

describe("extForCodec", () => {
  it("mirrors the server's CodecToExt mapping", () => {
    expect(extForCodec("webm-opus")).toBe("webm");
    expect(extForCodec("aac-m4a")).toBe("m4a");
    expect(extForCodec("ogg-opus")).toBe("ogg");
    expect(extForCodec("unknown")).toBe("m4a");
  });
});

describe("buildManifest", () => {
  it("mirrors the server RecordingManifest shape, with index-named files and the notes", () => {
    const manifest = buildManifest(makeRecording(), [makeChunk(0), makeChunk(1)]);
    expect(manifest).toMatchObject({
      recordingId: "rec-1",
      title: "Test recording",
      codec: "webm-opus",
      notes: [{ tMs: 5000, text: "a note" }],
    });
    const chunks = manifest.chunks as Array<Record<string, unknown>>;
    expect(chunks[0]).toMatchObject({ index: 0, file: "0000.webm", sha256: "hash-0", bytes: 8 });
    expect(chunks[1]).toMatchObject({ index: 1, file: "0001.webm" });
  });
});

describe("driveRecordingUpload", () => {
  it("drives register -> every unsent segment PUT (sha header) -> complete, and deletes the local copy only on 202", async () => {
    const rec = makeRecording();
    vi.mocked(getRecording).mockResolvedValue(rec);
    vi.mocked(listChunks).mockResolvedValue([makeChunk(0), makeChunk(1)]);
    fetchMock
      .mockResolvedValueOnce(okJson({ ok: true })) // register
      .mockResolvedValueOnce(okJson({ ok: true })) // chunk 0
      .mockResolvedValueOnce(okJson({ ok: true })) // chunk 1
      .mockResolvedValueOnce(okJson({ state: "queued" }, 202)); // complete

    const outcome = await driveRecordingUpload("rec-1");

    expect(outcome).toBe("delivered");
    const urls = fetchMock.mock.calls.map((c) => c[0] as string);
    expect(urls).toEqual([
      "/ingest/recording",
      "/ingest/recording/rec-1/chunk/0",
      "/ingest/recording/rec-1/chunk/1",
      "/ingest/recording/rec-1/complete",
    ]);
    const putInit = fetchMock.mock.calls[1][1] as RequestInit;
    expect((putInit.headers as Record<string, string>)["X-Chunk-Sha256"]).toBe("hash-0");
    expect(markChunkUploaded).toHaveBeenCalledWith("rec-1", 0, true);
    expect(markChunkUploaded).toHaveBeenCalledWith("rec-1", 1, true);
    expect(deleteRecording).toHaveBeenCalledWith("rec-1");
  });

  it("never re-sends a segment already confirmed on a prior pass", async () => {
    vi.mocked(getRecording).mockResolvedValue(makeRecording({ state: "retry" }));
    vi.mocked(listChunks).mockResolvedValue([makeChunk(0, true), makeChunk(1)]);
    fetchMock
      .mockResolvedValueOnce(okJson({ ok: true })) // register
      .mockResolvedValueOnce(okJson({ ok: true })) // chunk 1 only
      .mockResolvedValueOnce(okJson({ state: "queued" }, 202));

    await driveRecordingUpload("rec-1");

    const urls = fetchMock.mock.calls.map((c) => c[0] as string);
    expect(urls).toContain("/ingest/recording/rec-1/chunk/1");
    expect(urls).not.toContain("/ingest/recording/rec-1/chunk/0");
  });

  it("marks each confirmed segment in the store before moving to the next byte", async () => {
    vi.mocked(getRecording).mockResolvedValue(makeRecording());
    vi.mocked(listChunks).mockResolvedValue([makeChunk(0), makeChunk(1)]);
    fetchMock
      .mockResolvedValueOnce(okJson({ ok: true })) // register
      .mockResolvedValueOnce(okJson({ ok: true })) // chunk 0
      .mockRejectedValueOnce(new TypeError("network down")); // chunk 1 dies

    const outcome = await driveRecordingUpload("rec-1");

    expect(outcome).toBe("retry");
    // The win on segment 0 was persisted even though the pass then failed.
    expect(markChunkUploaded).toHaveBeenCalledWith("rec-1", 0, true);
    expect(markChunkUploaded).not.toHaveBeenCalledWith("rec-1", 1, true);
  });

  it("leaves a failed pass saved-and-retryable with a plain reason, never deleted", async () => {
    const rec = makeRecording();
    vi.mocked(getRecording).mockResolvedValue(rec);
    vi.mocked(listChunks).mockResolvedValue([makeChunk(0)]);
    fetchMock.mockRejectedValue(new TypeError("network down"));

    const outcome = await driveRecordingUpload("rec-1");

    expect(outcome).toBe("retry");
    expect(deleteRecording).not.toHaveBeenCalled();
    const saved = vi.mocked(saveRecording).mock.calls.map((c) => c[0]);
    const last = saved[saved.length - 1];
    expect(last.state).toBe("retry");
    expect(last.lastError).toContain("saved on this phone");
  });

  it("re-arms exactly the indices the completeness gate named on 409 and does not loop complete", async () => {
    vi.mocked(getRecording).mockResolvedValue(makeRecording());
    vi.mocked(listChunks).mockResolvedValue([makeChunk(0, true), makeChunk(1, true), makeChunk(2, true)]);
    // state queued but all chunks marked uploaded -> register runs, no PUTs, then complete gets 409.
    fetchMock
      .mockResolvedValueOnce(okJson({ ok: true })) // register
      .mockResolvedValueOnce(okJson({ state: "incomplete", missingOrBadIndices: [1, 7] }, 409)); // complete

    const outcome = await driveRecordingUpload("rec-1");

    expect(outcome).toBe("retry");
    // Segment 1 is local and named -> re-armed. Segment 7 was never local -> not invented.
    expect(markChunkUploaded).toHaveBeenCalledWith("rec-1", 1, false);
    expect(markChunkUploaded).not.toHaveBeenCalledWith("rec-1", 7, false);
    // No second complete call in the same pass.
    const completeCalls = fetchMock.mock.calls.filter((c) => (c[0] as string).endsWith("/complete"));
    expect(completeCalls).toHaveLength(1);
  });

  it("resumes an uploaded-but-not-completed recording straight at the complete call - no audio re-sent", async () => {
    vi.mocked(getRecording).mockResolvedValue(makeRecording({ state: "uploaded", completed: false }));
    vi.mocked(listChunks).mockResolvedValue([makeChunk(0, true)]);
    fetchMock.mockResolvedValueOnce(okJson({ state: "queued" }, 202)); // complete only

    const outcome = await driveRecordingUpload("rec-1");

    expect(outcome).toBe("delivered");
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock.mock.calls[0][0]).toBe("/ingest/recording/rec-1/complete");
  });

  it("refuses a zero-segment recording loudly instead of sending what the gate would bounce", async () => {
    vi.mocked(getRecording).mockResolvedValue(makeRecording());
    vi.mocked(listChunks).mockResolvedValue([]);

    const outcome = await driveRecordingUpload("rec-1");

    expect(outcome).toBe("retry");
    expect(fetchMock).not.toHaveBeenCalled();
    const saved = vi.mocked(saveRecording).mock.calls.map((c) => c[0]);
    expect(saved[saved.length - 1].lastError).toContain("no audio segments");
  });

  it("guards against concurrent drives of the same recording", async () => {
    vi.mocked(getRecording).mockResolvedValue(makeRecording());
    vi.mocked(listChunks).mockResolvedValue([makeChunk(0)]);
    let releaseRegister: (r: Response) => void = () => undefined;
    fetchMock
      .mockImplementationOnce(() => new Promise<Response>((resolve) => (releaseRegister = resolve)))
      .mockResolvedValueOnce(okJson({ ok: true }))
      .mockResolvedValueOnce(okJson({ state: "queued" }, 202));

    const first = driveRecordingUpload("rec-1");
    const second = await driveRecordingUpload("rec-1");
    expect(second).toBe("already-driving");

    // Release the register call only once the first drive has actually reached it.
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalled());
    releaseRegister(okJson({ ok: true }));
    await first;
  });
});

describe("resumePendingRecordingUploads", () => {
  it("drives every recording with upload work left, including a legacy 'ready' row - uploading is automatic (devthrottle_internal#966)", async () => {
    const queued = makeRecording({ recordingId: "queued-1", state: "queued" });
    const ready = makeRecording({ recordingId: "ready-1", state: "ready" });
    const stranded = makeRecording({ recordingId: "stranded-1", state: "uploaded", completed: false });
    vi.mocked(listRecordings).mockResolvedValue([queued, ready, stranded]);
    vi.mocked(getRecording).mockImplementation(async (id) =>
      id === "queued-1" ? queued : id === "stranded-1" ? stranded : ready,
    );
    vi.mocked(listChunks).mockResolvedValue([makeChunk(0, true)]);
    fetchMock.mockResolvedValue(okJson({ state: "queued" }, 202));

    await resumePendingRecordingUploads();

    const askedFor = vi.mocked(getRecording).mock.calls.map((c) => c[0]);
    expect(askedFor).toContain("queued-1");
    expect(askedFor).toContain("stranded-1");
    expect(askedFor).toContain("ready-1");
  });
});
