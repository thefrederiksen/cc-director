import { beforeEach, describe, expect, it, vi } from "vitest";

// backgroundTranscribeAndSend drives the server-owned upload+transcribe+inject and reports the
// outcome back to the host. These tests lock the caveat-#2 behavior (issue #1056): when the turn is
// not confirmed but the audio was saved durably, the user is told the dictation is HELD and will
// retry - not a raw technical error and not silence.

vi.mock("../api/client", () => ({ uploadDictationToSession: vi.fn() }));
vi.mock("./pendingStore", () => ({
  savePending: vi.fn(),
  deletePending: vi.fn(),
  prunePending: vi.fn(),
}));

import { uploadDictationToSession } from "../api/client";
import { backgroundTranscribeAndSend, type CapturedUtterance } from "./backgroundSend";
import { deletePending, savePending } from "./pendingStore";

const captured: CapturedUtterance = { blob: new Blob(["x"]), recordedMs: 1000, prefixText: "" };

describe("backgroundTranscribeAndSend", () => {
  beforeEach(() => vi.clearAllMocks());

  it("surfaces the SPECIFIC held-and-will-retry reason when the turn is not confirmed but the audio was saved", async () => {
    vi.mocked(savePending).mockResolvedValue(undefined);
    // uploadDictationToSession has already humanized the failure (transcriptionFailureMessage), so the
    // outcome.error carries the specific reason - here, a server-side transcription-service outage.
    vi.mocked(uploadDictationToSession).mockResolvedValue({
      terminal: false, submitted: false, movedOn: false, transcript: "",
      error: "The transcription service is temporarily unavailable. Your recording is saved and will retry.",
    });
    const onError = vi.fn();
    const onFailed = vi.fn();

    await backgroundTranscribeAndSend("sid", captured, { onError, onFailed });

    expect(onError).toHaveBeenCalledTimes(1);
    // The user sees the specific cause (the transcription service is down), not a blanket "session may be
    // busy" guess, and is told the recording is held.
    expect(onError.mock.calls[0][0]).toContain("temporarily unavailable");
    expect(onError.mock.calls[0][0]).toContain("saved and will retry");
    expect(onError.mock.calls[0][0]).not.toContain("session may be busy");
    expect(onFailed).toHaveBeenCalledTimes(1);
    // The durable record is kept so resume-on-load re-drives it.
    expect(deletePending).not.toHaveBeenCalled();
  });

  it("deletes the durable record and reports nothing on a terminal (submitted) outcome", async () => {
    vi.mocked(savePending).mockResolvedValue(undefined);
    vi.mocked(uploadDictationToSession).mockResolvedValue({
      terminal: true, submitted: true, movedOn: false, transcript: "hi",
    });
    const onError = vi.fn();

    await backgroundTranscribeAndSend("sid", captured, { onError });

    expect(onError).not.toHaveBeenCalled();
    expect(deletePending).toHaveBeenCalledTimes(1);
  });

  it("surfaces the raw error when there is no durable store to hold the audio", async () => {
    vi.mocked(savePending).mockRejectedValue(new Error("no indexeddb"));
    vi.mocked(uploadDictationToSession).mockResolvedValue({
      terminal: false, submitted: false, movedOn: false, transcript: "", error: "network down",
    });
    const onError = vi.fn();

    await backgroundTranscribeAndSend("sid", captured, { onError });

    expect(onError).toHaveBeenCalledWith("network down");
  });
});
