import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The durable Send pipeline + background retry driver (issue #1006, strengthened for #1182, parking for
// #1184).
//
// These tests exercise the durable behavior against a mocked API client and a mocked durable store, and
// assert on the REAL status store (status.ts is not mocked) so the published phases are checked exactly as
// the UI reads them. The core guarantees under test:
//   - persist to the durable store BEFORE any network work;
//   - a delivered clip is removed from the store on the terminal submitted outcome (no accumulation);
//   - a held (non-terminal) outcome KEEPS the audio and publishes a held, retryable status - never a loss;
//   - resume-on-load re-drives every pending clip from the durable copy with resumed=true;
//   - the in-flight guard makes concurrent triggers drive a clip at most once (no double injection);
//   - durable storage being unavailable is a clear, loud failure, not a silent one-shot send;
//   - a genuinely permanent failure PARKS the clip (keeps the audio, stops the auto-loop) and only an
//     explicit Retry re-drives it, while transient failures still auto-retry (issue #1184).

vi.mock("../api/client", () => ({ uploadDictationToSession: vi.fn() }));
vi.mock("./pendingStore", () => ({
  savePending: vi.fn(),
  deletePending: vi.fn(),
  getPending: vi.fn(),
  listPending: vi.fn(),
}));

import { uploadDictationToSession, type DictationSubmitResult } from "../api/client";
import {
  backgroundTranscribeAndSend,
  resumePendingDictations,
  retryPendingDictation,
  type CapturedUtterance,
} from "./backgroundSend";
import { deletePending, getPending, listPending, savePending, type PendingDictation } from "./pendingStore";
import { allDictationStatuses, clearDictationStatus } from "./status";

const captured: CapturedUtterance = { blob: new Blob(["x"]), recordedMs: 1000, prefixText: "" };

const SUBMITTED: DictationSubmitResult = { terminal: true, submitted: true, movedOn: false, transcript: "hi" };
const HELD: DictationSubmitResult = {
  terminal: false,
  submitted: false,
  movedOn: false,
  transcript: "",
  error: "The transcription service is temporarily unavailable - your recording is saved and will keep trying.",
};
const PERMANENT: DictationSubmitResult = {
  terminal: false,
  submitted: false,
  movedOn: false,
  transcript: "",
  permanent: true,
  permanentReason: "audio-too-large",
};

function statusFor(uploadId: string) {
  return allDictationStatuses().find((s) => s.uploadId === uploadId);
}

function makeRecord(id: string): PendingDictation {
  return {
    id,
    sessionId: "sid",
    blob: new Blob(["x"]),
    recordedMs: 1000,
    before: "",
    after: "",
    prefix: "",
    baselineBufferBytes: 0,
    createdAt: Date.now(),
  };
}

const flush = async () => {
  for (let i = 0; i < 6; i++) await Promise.resolve();
};

beforeEach(() => {
  vi.clearAllMocks();
  vi.useFakeTimers();
  for (const s of allDictationStatuses()) clearDictationStatus(s.uploadId);
  vi.mocked(savePending).mockResolvedValue(undefined);
  vi.mocked(deletePending).mockResolvedValue(undefined);
  vi.mocked(getPending).mockResolvedValue(null);
  vi.mocked(listPending).mockResolvedValue([]);
});

afterEach(() => {
  vi.useRealTimers();
});

describe("backgroundTranscribeAndSend", () => {
  it("persists the audio to the durable store BEFORE any network work", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);

    await backgroundTranscribeAndSend("sid", captured);

    expect(savePending).toHaveBeenCalledTimes(1);
    // savePending must run before the first upload call.
    const saveOrder = vi.mocked(savePending).mock.invocationCallOrder[0];
    const uploadOrder = vi.mocked(uploadDictationToSession).mock.invocationCallOrder[0];
    expect(saveOrder).toBeLessThan(uploadOrder);
    // The persisted record carries the recorded audio.
    expect(vi.mocked(savePending).mock.calls[0][0].blob).toBe(captured.blob);
  });

  it("removes the durable copy on a terminal submitted outcome (the queue does not accumulate)", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);

    await backgroundTranscribeAndSend("sid", captured);

    const savedId = vi.mocked(savePending).mock.calls[0][0].id;
    expect(deletePending).toHaveBeenCalledWith(savedId);
    expect(statusFor(savedId)?.phase).toBe("done");
  });

  it("keeps the audio and publishes a held, retryable status on a non-terminal outcome - never deletes it", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValue(HELD);

    await backgroundTranscribeAndSend("sid", captured);

    const savedId = vi.mocked(savePending).mock.calls[0][0].id;
    expect(deletePending).not.toHaveBeenCalled();
    const status = statusFor(savedId);
    expect(status?.phase).toBe("held");
    expect(status?.retryable).toBe(true);
    // The held copy is honest: saved and will keep trying, never "was not transcribed".
    expect(status?.error).toContain("saved");
    expect(status?.error).not.toContain("was not transcribed");
  });

  it("passes resumed=false on the very first immediate send", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);

    await backgroundTranscribeAndSend("sid", captured);

    expect(vi.mocked(uploadDictationToSession).mock.calls[0][0].resumed).toBe(false);
  });

  it("fails loudly (no silent one-shot) when durable storage is unavailable", async () => {
    vi.mocked(savePending).mockRejectedValue(new Error("indexedDB unavailable"));
    const onError = vi.fn();
    const onFailed = vi.fn();

    await backgroundTranscribeAndSend("sid", captured, { onError, onFailed });

    // No upload was attempted, and the failure is surfaced clearly.
    expect(uploadDictationToSession).not.toHaveBeenCalled();
    expect(onError).toHaveBeenCalledTimes(1);
    expect(onFailed).toHaveBeenCalledTimes(1);
    const all = allDictationStatuses();
    expect(all[0].phase).toBe("failed");
    expect(all[0].retryable).toBe(false);
  });
});

describe("resumePendingDictations", () => {
  it("re-drives every pending clip from the durable copy with resumed=true, deleting delivered ones", async () => {
    const a = makeRecord("id-a");
    const b = makeRecord("id-b");
    vi.mocked(listPending).mockResolvedValue([a, b]);
    vi.mocked(uploadDictationToSession).mockResolvedValue(SUBMITTED);

    await resumePendingDictations();

    expect(uploadDictationToSession).toHaveBeenCalledTimes(2);
    for (const call of vi.mocked(uploadDictationToSession).mock.calls) {
      expect(call[0].resumed).toBe(true);
    }
    expect(deletePending).toHaveBeenCalledWith("id-a");
    expect(deletePending).toHaveBeenCalledWith("id-b");
  });

  it("keeps a clip that still cannot be delivered (held, not dropped)", async () => {
    const a = makeRecord("id-held");
    vi.mocked(listPending).mockResolvedValue([a]);
    vi.mocked(uploadDictationToSession).mockResolvedValue(HELD);

    await resumePendingDictations();

    expect(deletePending).not.toHaveBeenCalled();
    expect(statusFor("id-held")?.phase).toBe("held");
  });
});

describe("in-flight guard (idempotency: never inject twice)", () => {
  it("drives a clip at most once when two triggers fire concurrently for the same upload id", async () => {
    const rec = makeRecord("id-dup");
    vi.mocked(getPending).mockResolvedValue(rec);
    let resolveUpload!: (r: DictationSubmitResult) => void;
    vi.mocked(uploadDictationToSession).mockImplementation(
      () => new Promise<DictationSubmitResult>((res) => { resolveUpload = res; }),
    );

    // Two "Upload now" triggers race for the same clip.
    const p1 = retryPendingDictation("id-dup");
    const p2 = retryPendingDictation("id-dup");
    await flush();

    // The in-flight guard let exactly one attempt start.
    expect(uploadDictationToSession).toHaveBeenCalledTimes(1);

    resolveUpload(SUBMITTED);
    await Promise.all([p1, p2]);

    expect(deletePending).toHaveBeenCalledTimes(1);
  });
});

describe("retryPendingDictation (Upload now)", () => {
  it("clears a stale status when the durable record is already gone", async () => {
    vi.mocked(getPending).mockResolvedValue(null);
    // Seed a lingering held status for a clip that has since been delivered/abandoned.
    const { publishDictationStatus } = await import("./status");
    publishDictationStatus({ sessionId: "sid", uploadId: "gone", phase: "held", retryable: true });

    await retryPendingDictation("gone");

    expect(statusFor("gone")).toBeUndefined();
    expect(uploadDictationToSession).not.toHaveBeenCalled();
  });
});

describe("parking permanently-failed clips (#1184)", () => {
  it("parks on a permanent outcome: persists the parked reason, keeps the audio, shows the saved-and-retryable message, and STOPS the auto-loop", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValue(PERMANENT);

    await backgroundTranscribeAndSend("sid", captured);

    const savedId = vi.mocked(savePending).mock.calls[0][0].id;
    // The audio is never discarded.
    expect(deletePending).not.toHaveBeenCalled();
    // The record was persisted with the allow-listed parked reason so every auto-trigger can skip it.
    const parkedSave = vi.mocked(savePending).mock.calls.find((c) => c[0].parkedReason);
    expect(parkedSave?.[0].parkedReason).toBe("audio-too-large");
    // The status is parked, retryable, with the exact saved-and-retryable size wording.
    const status = statusFor(savedId);
    expect(status?.phase).toBe("parked");
    expect(status?.retryable).toBe(true);
    expect(status?.error).toBe(
      "This recording is too long to transcribe right now; it is saved on your device and you can retry it.",
    );
    // The forever-loop is gone: advancing well past the hard-retry window triggers no further attempt.
    const callsBefore = vi.mocked(uploadDictationToSession).mock.calls.length;
    await vi.advanceTimersByTimeAsync(30_000);
    expect(vi.mocked(uploadDictationToSession).mock.calls.length).toBe(callsBefore);
  });

  it("does NOT auto-drive a parked clip on app load - it only republishes the parked status", async () => {
    const parked: PendingDictation = { ...makeRecord("id-parked"), parkedReason: "audio-too-large" };
    vi.mocked(listPending).mockResolvedValue([parked]);

    await resumePendingDictations();

    expect(uploadDictationToSession).not.toHaveBeenCalled();
    expect(deletePending).not.toHaveBeenCalled();
    expect(statusFor("id-parked")?.phase).toBe("parked");
  });

  it("an explicit Retry reactivates a parked clip (clears parkedReason) and re-drives it from the on-device copy", async () => {
    const parked: PendingDictation = { ...makeRecord("id-retry"), parkedReason: "audio-too-large" };
    vi.mocked(getPending).mockResolvedValue(parked);
    vi.mocked(uploadDictationToSession).mockResolvedValue(HELD); // still cannot succeed yet, but it re-drove

    await retryPendingDictation("id-retry");

    // The durable record was reactivated (parked reason cleared) so the triggers stop skipping it.
    const reactivate = vi.mocked(savePending).mock.calls.find((c) => c[0].id === "id-retry");
    expect(reactivate).toBeTruthy();
    expect(reactivate?.[0].parkedReason).toBeUndefined();
    // And the explicit retry actually re-drove the clip.
    expect(uploadDictationToSession).toHaveBeenCalledTimes(1);
  });

  it("a transient failure still auto-retries and is never parked (no regression)", async () => {
    vi.mocked(uploadDictationToSession).mockResolvedValue(HELD);

    await backgroundTranscribeAndSend("sid", captured);

    const savedId = vi.mocked(savePending).mock.calls[0][0].id;
    expect(statusFor(savedId)?.phase).toBe("held");
    // A transient outcome never parks the record.
    const parkedSave = vi.mocked(savePending).mock.calls.find((c) => c[0].parkedReason);
    expect(parkedSave).toBeUndefined();
  });
});
