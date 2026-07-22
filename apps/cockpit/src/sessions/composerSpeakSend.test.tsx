// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, fireEvent, waitFor, within } from "@testing-library/react";
import { useState } from "react";

// Behavioral regression for the Cockpit Speak Send-direct fix (PR #1975).
//
// The bug: pressing Send WHILE RECORDING in the Cockpit Speak box "basically sent nothing" on the
// hosted Gateway, because the recording-stage Send was wired to the phone's fire-and-forget durable
// pipeline (backgroundTranscribeAndSend -> the /dictation/* routes), which the hosted Gateway still
// resolves in the Local tenant partition (blocker #1884). Insert worked because it transcribes
// synchronously through the tenant-aware /wingman/utterance/* path and submits via POST /prompt.
//
// The fix (SessionComposer.tsx): stop passing the DictationDialog's onSendAudio prop, so a
// recording-stage Send falls back to the blocking commit-then-onSend path - the SAME synchronous
// transcribe-then-send Insert already uses. The RMS-meter unit tests do not exercise this; only a
// render of the real composer + real dialog, pressing Send while recording, proves the repaired path.
//
// This test presses Send while RECORDING and asserts the transcript (composed with the typed text at
// the snapshotted caret) is submitted via sendPrompt for the SELECTED session id, through the
// synchronous transcription path, and that the durable /dictation/* pipeline is NEVER touched.
//
// Revert-proof: re-add `onSendAudio={onDictateSendAudio}` to the DictationDialog mount (undo the fix)
// and a recording-stage Send calls backgroundTranscribeAndSend instead of sendPrompt - every
// assertion below flips, so the test reddens.

// vi.mock is hoisted above module scope, so the spies it references must be created in the same
// hoisted phase (vi.hoisted) rather than as ordinary consts.
const { sendPrompt, transcribeUtterance, backgroundTranscribeAndSend } = vi.hoisted(() => ({
  // The synchronous POST /prompt the fix routes Send-direct through.
  sendPrompt: vi.fn(async () => {}),
  // The tenant-aware /wingman/utterance/* transcription.
  transcribeUtterance: vi.fn(async () => "the dictated words"),
  // The durable background pipeline (POST /dictation/*) the fix routes Send-direct AWAY from.
  backgroundTranscribeAndSend: vi.fn(async () => {}),
}));

// The Gateway client boundary.
vi.mock("@devthrottle/client-core/api/client", () => ({
  sendPrompt,
  transcribeUtterance,
  enqueuePrompt: vi.fn(async () => []),
  uploadImage: vi.fn(async () => ""),
  gatewayErrorMessage: (e: unknown) => (e instanceof Error ? e.message : String(e)),
}));

// The durable background pipeline. This test asserts it is never invoked; reverting the fix makes the
// recording-stage Send call it.
vi.mock("@devthrottle/client-core/dictation/backgroundSend", () => ({
  backgroundTranscribeAndSend,
}));

// Mic + audio-decode boundaries jsdom cannot provide. The fake recorder fires onCaptureLive on start
// so the dialog flips to RECORDING (the state under test) without a real microphone, and returns a
// fixed clip on stop. A 1000 ms clip that decodes to 1.0 s means zero capture deficit, so the dialog
// commits instead of parking on a dropped-audio warning.
vi.mock("@devthrottle/client-core/dictation/recorder", () => {
  class MicRecorder {
    onCaptureLive: (() => void) | null = null;
    lastRecordedMs = 1000;
    async start() {
      this.onCaptureLive?.();
    }
    async stop() {
      return new Blob(["clip"], { type: "audio/webm" });
    }
    level() {
      return 0;
    }
    dispose() {}
  }
  return { MicRecorder, rmsLevel: () => 0 };
});
vi.mock("@devthrottle/client-core/dictation/wav", () => ({
  blobToWav16kMono: async () => ({ wav: new Blob(["wav"]), decodedSeconds: 1, sourceBytes: 1000 }),
}));
vi.mock("@devthrottle/client-core/dictation/readyCue", () => ({
  playReadyCue: () => {},
  primeCueAudio: () => {},
  releaseCueAudio: () => {},
  startThinkingCue: () => () => {},
  playYourTurnCue: () => {},
}));

import { SessionComposer } from "./SessionComposer";

// A parent that owns the composer text exactly like SessionDetail does, so onChange updates the value
// the composer reads back when it composes the dictation at the caret.
function Harness({ sessionId }: { sessionId?: string }) {
  const [value, setValue] = useState("");
  return (
    <SessionComposer sessionId={sessionId} value={value} onChange={setValue} onQueued={() => {}} />
  );
}

describe("Cockpit Speak Send-direct (recording-stage)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    // jsdom has no requestAnimationFrame; the dialog's display-only equalizer loop uses it. A no-op
    // that never calls back keeps the animation out of the test without affecting behaviour.
    globalThis.requestAnimationFrame = (() => 0) as typeof globalThis.requestAnimationFrame;
    globalThis.cancelAnimationFrame = (() => {}) as typeof globalThis.cancelAnimationFrame;
  });

  it("submits the transcript composed at the caret via sendPrompt for the selected session, not the durable /dictation path", async () => {
    render(<Harness sessionId="sess-42" />);

    // Type "AB" and drop the caret BETWEEN A and B, so a correct compose lands the dictation at the
    // caret ("A ... B"), not appended at the end.
    const textarea = screen.getByPlaceholderText(/Type a message/i) as HTMLTextAreaElement;
    fireEvent.change(textarea, { target: { value: "AB" } });
    textarea.selectionStart = 1;
    textarea.selectionEnd = 1;

    // Open the Speak dialog (snapshots the caret) and wait for it to reach RECORDING.
    fireEvent.click(screen.getByRole("button", { name: "Speak" }));
    const dialog = await screen.findByRole("dialog", { name: "Dictate" });
    await within(dialog).findByText("RECORDING");

    // Press Send WHILE RECORDING - the exact action that "sent nothing" before the fix.
    fireEvent.click(within(dialog).getByText("Send"));

    // The dictated words are transcribed synchronously and submitted, composed at the snapshotted
    // caret, to the SELECTED session, via POST /prompt (appendEnter true).
    await waitFor(() =>
      expect(sendPrompt).toHaveBeenCalledWith("sess-42", "A the dictated words B", true),
    );
    expect(transcribeUtterance).toHaveBeenCalledTimes(1);

    // The durable, Local-pinned /dictation/* pipeline that caused the bug is never touched.
    expect(backgroundTranscribeAndSend).not.toHaveBeenCalled();
  });
});
