// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, within, cleanup } from "@testing-library/react";
import { useState } from "react";

// Behavioral regression for the Cockpit Speak Send-direct path - REWIRED to the fire-and-forget
// pipeline (mobile parity), reversing the PR #1975 workaround.
//
// History, in order, because this file has pinned OPPOSITE behaviors at different times and the next
// reader must know why the current one is right:
//
//   1. Originally the recording-stage Send used the phone's durable pipeline
//      (backgroundTranscribeAndSend -> the /dictation/* routes). On the HOSTED Gateway those routes
//      then resolved sessions in the Local tenant partition (blocker #1884), so a hosted Send held
//      forever with no status - "Send basically sent nothing".
//   2. PR #1975 unplugged onSendAudio so Send fell back to the blocking transcribe-then-sendPrompt
//      path, and THIS TEST pinned "the durable pipeline is never touched".
//   3. The dictation upload family is now tenant-partitioned end to end (DictationTenantGate, PR
//      #1945; per-tenant transcript storage #2093) and the phone sends through it on hosted daily.
//      The Cockpit is rewired onto it (onSendAudio passed again), mounts the shared
//      DictationStatusStrip for feedback, and resumes pending dictations on load (AppShell). So the
//      pin flips: a recording-stage Send must hand the CAPTURED AUDIO to the background pipeline and
//      release the screen immediately - it must NOT block on a synchronous transcription.
//
// Revert-proof both ways: remove `onSendAudio={onDictateSendAudio}` from the DictationDialog mount
// (re-introduce the #1975 workaround) and the recording-stage Send blocks through transcribeUtterance
// + sendPrompt instead - every assertion below flips, so the test reddens. The PAUSED-stage test pins
// the path that deliberately did NOT change.

// vi.mock is hoisted above module scope, so the spies it references must be created in the same
// hoisted phase (vi.hoisted) rather than as ordinary consts.
const { sendPrompt, transcribeUtterance, backgroundTranscribeAndSend, listSessions } = vi.hoisted(() => ({
  // The synchronous POST /prompt path: PAUSED-stage Send (text already in hand) and Insert use it.
  sendPrompt: vi.fn(async () => {}),
  // The synchronous /wingman/utterance/* transcription: the Pause checkpoint and Insert use it.
  transcribeUtterance: vi.fn(async () => ({ text: "the dictated words", deliveryId: "utt-77" })),
  // The durable background pipeline (POST /dictation/*) the recording-stage Send now rides.
  backgroundTranscribeAndSend: vi.fn(async () => {}),
  // The roster read the Speak press snapshots its moved-on baseline from (issue #2478).
  listSessions: vi.fn(async () => [{ sessionId: "sess-42", totalBufferBytes: 4321 }]),
}));

// The Gateway client boundary.
vi.mock("@devthrottle/client-core/api/client", () => ({
  sendPrompt,
  transcribeUtterance,
  listSessions,
  enqueuePrompt: vi.fn(async () => []),
  uploadImage: vi.fn(async () => ""),
  gatewayErrorMessage: (e: unknown) => (e instanceof Error ? e.message : String(e)),
}));

// The durable background pipeline boundary. The recording-stage Send must call
// backgroundTranscribeAndSend; the other exports are what the shared DictationStatusStrip (mounted by
// the composer now) imports from the same module.
vi.mock("@devthrottle/client-core/dictation/backgroundSend", () => ({
  backgroundTranscribeAndSend,
  resumePendingDictations: vi.fn(async () => {}),
  abandonPendingDictation: vi.fn(async () => {}),
  dismissDictationStatus: vi.fn(async () => {}),
  retryDroppedDictation: vi.fn(async () => {}),
  retryPendingDictation: vi.fn(async () => {}),
  sendDroppedDictationAnyway: vi.fn(async () => {}),
}));

// Mic + audio-decode boundaries jsdom cannot provide. The fake recorder fires onCaptureLive on start
// so the dialog flips to RECORDING (the state under test) without a real microphone, and returns a
// fixed clip on stop. A 1000 ms clip that decodes to 1.0 s means zero capture deficit, so the dialog
// commits instead of parking on a dropped-audio warning.
vi.mock("@devthrottle/client-core/dictation/recorder", () => {
  class MicRecorder {
    onCaptureLive: (() => void) | null = null;
    lastRecordedMs = 1000;
    deviceLabel = "Fake Microphone";
    deviceId = "fake-mic";
    async start() {
      this.onCaptureLive?.();
    }
    async stop() {
      return new Blob(["clip"], { type: "audio/webm" });
    }
    level() {
      return 0;
    }
    // The liveness clocks the dialog's animation loop reads. Zero = a healthy microphone, so the live
    // capture alarm stays quiet and this file keeps testing what it is about (the Send path). They are
    // here rather than omitted because the loop calls them on every frame: a fake without them is one
    // scheduled animation frame away from throwing, and the failure would look like a Send bug.
    msSinceLastAudio() {
      return 0;
    }
    msSinceMeterMoved() {
      return 0;
    }
    dispose() {}
  }
  return { MicRecorder, rmsLevel: () => 0 };
});
vi.mock("@devthrottle/client-core/dictation/wav", () => ({
  blobToWav16kMono: async () => ({
    wav: new Blob(["wav"]),
    decodedSeconds: 1,
    sourceBytes: 1000,
    nativeSamples: new Float32Array(0),
    nativeSampleRate: 16000,
  }),
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

// Type "AB" into the composer, drop the caret BETWEEN A and B, open Speak, and wait for RECORDING.
// The caret placement is what proves the compose lands the dictation at the caret, not at the end.
async function typeAndOpenRecordingDialog(): Promise<HTMLElement> {
  const textarea = screen.getByPlaceholderText(/Type a message/i) as HTMLTextAreaElement;
  fireEvent.change(textarea, { target: { value: "AB" } });
  textarea.selectionStart = 1;
  textarea.selectionEnd = 1;
  fireEvent.click(screen.getByRole("button", { name: "Speak" }));
  const dialog = await screen.findByRole("dialog", { name: "Dictate" });
  await within(dialog).findByText("RECORDING");
  return dialog;
}

describe("Cockpit Speak Send-direct (recording-stage)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    // jsdom has no requestAnimationFrame; the dialog's display-only equalizer loop uses it. A no-op
    // that never calls back keeps the animation out of the test without affecting behaviour.
    globalThis.requestAnimationFrame = (() => 0) as typeof globalThis.requestAnimationFrame;
    globalThis.cancelAnimationFrame = (() => {}) as typeof globalThis.cancelAnimationFrame;
  });

  // Two renders share this file; without vitest globals RTL does not auto-clean between them.
  afterEach(() => cleanup());

  it("hands the captured audio to the background pipeline with the typed text split at the caret, and releases the screen at once", async () => {
    render(<Harness sessionId="sess-42" />);
    const dialog = await typeAndOpenRecordingDialog();

    // Press Send WHILE RECORDING - the fire-and-forget action under test.
    fireEvent.click(within(dialog).getByText("Send"));

    // The captured audio goes to the durable background pipeline for the SELECTED session, with the
    // typed text split at the snapshotted caret so the Gateway submits "A <dictation> B".
    await waitFor(() => expect(backgroundTranscribeAndSend).toHaveBeenCalledTimes(1));
    const [sid, captured, opts] = backgroundTranscribeAndSend.mock.calls[0] as unknown as [
      string,
      { blob: Blob; recordedMs: number },
      { composeParts?: { before: string; after: string }; baselineBufferBytes?: Promise<number | undefined> },
    ];
    expect(sid).toBe("sess-42");
    expect(captured.blob).toBeInstanceOf(Blob);
    expect(captured.recordedMs).toBe(1000);
    expect(opts.composeParts).toEqual({ before: "A", after: "B" });
    // The moved-on guard's baseline (issue #2478): the session's terminal-byte position, whose roster
    // read the Speak press STARTED - handed to the pipeline as a promise it awaits, so a quick Send
    // waits for the answer instead of racing it. Resolves above zero, so the Gateway's guard actually
    // ARMS for a clip resumed later - this flow used to omit the field, it defaulted to zero, and the
    // guard was unreachable from the shipped Speak Send.
    await expect(opts.baselineBufferBytes).resolves.toBe(4321);

    // The screen is released immediately: the dialog is gone without waiting for any transcription.
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Dictate" })).toBeNull());

    // Nothing on this path blocks on the synchronous transcription or submits a text prompt itself -
    // the Gateway does both server-side. (Reverting the rewire flips exactly these.)
    expect(transcribeUtterance).not.toHaveBeenCalled();
    expect(sendPrompt).not.toHaveBeenCalled();

    // The typed text was cleared like any other Send (it rides in composeParts instead).
    expect((screen.getByPlaceholderText(/Type a message/i) as HTMLTextAreaElement).value).toBe("");
  });

  it("PAUSED-stage Send still submits the already-transcribed text synchronously via sendPrompt (unchanged path)", async () => {
    render(<Harness sessionId="sess-42" />);
    const dialog = await typeAndOpenRecordingDialog();

    // Pause: the checkpoint transcribes the segment synchronously and parks in PAUSED with the words
    // in the editable box. (The pause button renders a glyph, not text - reach it by its class.)
    const pauseBtn = dialog.querySelector(".dictate-pause") as HTMLButtonElement;
    fireEvent.click(pauseBtn);
    await within(dialog).findByText("PAUSED");
    expect(transcribeUtterance).toHaveBeenCalledTimes(1);

    // Send from PAUSED: the text is already in hand, so it submits via POST /prompt composed at the
    // snapshotted caret - no audio round trip, and the background pipeline stays untouched.
    fireEvent.click(within(dialog).getByText("Send"));
    await waitFor(() =>
      // The last argument is the SPOKEN claim (ruling R10, "Clean up Your Throttle"), and it is
      // undefined here on purpose: the box held typed text, so this turn is a mixture of speech and
      // typing and is not claimed as spoken. See the test below for the pure-dictation case.
      expect(sendPrompt).toHaveBeenCalledWith("sess-42", "A the dictated words B", true, undefined, undefined),
    );
    expect(backgroundTranscribeAndSend).not.toHaveBeenCalled();
  });

  // R10 ("Clean up Your Throttle", 2026-09-05). A dictated sentence submitted through the ordinary
  // prompt door was recorded as TYPED, because the Director reads modality off the send source and an
  // ordinary prompt is a typed one. The delivery id of the utterance the Gateway just transcribed is
  // what says otherwise, and it must reach the send.
  it("a PURE dictation carries the spoken delivery id, so the turn is recorded as speech and not as typing", async () => {
    render(<Harness sessionId="sess-42" />);
    // No typed text this time: open the dialog with an empty box, so what is sent is only the words.
    fireEvent.click(screen.getByRole("button", { name: "Speak" }));
    const dialog = await screen.findByRole("dialog", { name: "Dictate" });
    await within(dialog).findByText("RECORDING");

    const pauseBtn = dialog.querySelector(".dictate-pause") as HTMLButtonElement;
    fireEvent.click(pauseBtn);
    await within(dialog).findByText("PAUSED");

    fireEvent.click(within(dialog).getByText("Send"));
    await waitFor(() =>
      expect(sendPrompt).toHaveBeenCalledWith("sess-42", "the dictated words", true, undefined, "utt-77"),
    );
  });

  // The claim is dropped the moment the person edits the words: an edited transcript is composition,
  // not a transcription, and the page's own disclosure already tells the reader it counts as typed.
  it("editing the transcript before sending drops the spoken claim", async () => {
    render(<Harness sessionId="sess-42" />);
    fireEvent.click(screen.getByRole("button", { name: "Speak" }));
    const dialog = await screen.findByRole("dialog", { name: "Dictate" });
    await within(dialog).findByText("RECORDING");

    const pauseBtn = dialog.querySelector(".dictate-pause") as HTMLButtonElement;
    fireEvent.click(pauseBtn);
    await within(dialog).findByText("PAUSED");

    const box = dialog.querySelector("textarea") as HTMLTextAreaElement;
    fireEvent.change(box, { target: { value: "the dictated words, reworded" } });

    fireEvent.click(within(dialog).getByText("Send"));
    await waitFor(() =>
      expect(sendPrompt).toHaveBeenCalledWith("sess-42", "the dictated words, reworded", true, undefined, undefined),
    );
  });
});
