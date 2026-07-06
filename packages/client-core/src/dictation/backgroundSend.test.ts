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

  it("tells the user the dictation is held (not lost) when the turn is not confirmed but the audio was saved", async () => {
    vi.mocked(savePending).mockResolvedValue(undefined);
    vi.mocked(uploadDictationToSession).mockResolvedValue({
      terminal: false, submitted: false, movedOn: false, transcript: "", error: "submit to session failed",
    });
    const onError = vi.fn();
    const onFailed = vi.fn();

    await backgroundTranscribeAndSend("sid", captured, { onError, onFailed });

    expect(onError).toHaveBeenCalledTimes(1);
    expect(onError.mock.calls[0][0]).toContain("saved and will retry");
    // The raw server error is NOT what the user sees in the held case.
    expect(onError.mock.calls[0][0]).not.toContain("submit to session failed");
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
