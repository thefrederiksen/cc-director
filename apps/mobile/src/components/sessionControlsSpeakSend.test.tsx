// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, within, cleanup } from "@testing-library/react";

// Regression for issue #2478 on the MOBILE Speak flow: the Gateway's "session moved on" guard for a
// resumed dictation requires a baselineBufferBytes above zero, and this flow used to omit the field -
// it defaulted to zero, so the guard never armed and the recovery behavior the feature was built for
// was unreachable from the shipped app. The Speak press now snapshots the session's terminal-byte
// position from the roster (the same reading the Voice screen has always sent), and the recording-
// stage Send hands it to the durable background pipeline. This is the mobile workspace's first test
// file; it mirrors the cockpit's composerSpeakSend.test.tsx, which pins the same behavior for the
// Cockpit composer flow.

// vi.mock is hoisted above module scope, so the spies it references must be created in the same
// hoisted phase (vi.hoisted) rather than as ordinary consts.
const { sendPrompt, transcribeUtterance, backgroundTranscribeAndSend, listSessions } = vi.hoisted(() => ({
  // The synchronous POST /prompt path (typed Send, Insert-then-Enter).
  sendPrompt: vi.fn(async () => {}),
  // The synchronous /wingman/utterance/* transcription (the Pause checkpoint and Insert).
  transcribeUtterance: vi.fn(async () => "the dictated words"),
  // The durable background pipeline (POST /dictation/*) the recording-stage Send rides.
  backgroundTranscribeAndSend: vi.fn(async () => {}),
  // The roster read the Speak press snapshots its moved-on baseline from (issue #2478).
  listSessions: vi.fn(async () => [{ sessionId: "sess-42", totalBufferBytes: 4321 }]),
}));

// The Gateway client boundary.
vi.mock("@devthrottle/client-core/api/client", () => ({
  sendPrompt,
  transcribeUtterance,
  listSessions,
  sendEscape: vi.fn(async () => {}),
  sendInterrupt: vi.fn(async () => {}),
  uploadImage: vi.fn(async () => ""),
}));

// The durable background pipeline boundary.
vi.mock("@devthrottle/client-core/dictation/backgroundSend", () => ({
  backgroundTranscribeAndSend,
}));

// Mic + audio-decode boundaries jsdom cannot provide, byte-for-byte the fakes the cockpit test uses.
// The fake recorder fires onCaptureLive on start so the dialog flips to RECORDING without a real
// microphone, and returns a fixed clip on stop. A 1000 ms clip that decodes to 1.0 s means zero
// capture deficit, so the dialog commits instead of parking on a dropped-audio warning.
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
    // The liveness clocks the dialog's animation loop reads on every frame; zero = a healthy
    // microphone, so the live capture alarm stays quiet and this file keeps testing the Send path.
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

import { SessionControls } from "./SessionControls";

// Type "AB" into the input, drop the caret BETWEEN A and B, open Speak, and wait for RECORDING. The
// caret placement is what proves the compose lands the dictation at the caret, not at the end.
async function typeAndOpenRecordingDialog(): Promise<HTMLElement> {
  const input = screen.getByPlaceholderText(/type a message/i) as HTMLTextAreaElement;
  fireEvent.change(input, { target: { value: "AB" } });
  input.selectionStart = 1;
  input.selectionEnd = 1;
  fireEvent.click(screen.getByRole("button", { name: "Speak" }));
  const dialog = await screen.findByRole("dialog", { name: "Dictate" });
  await within(dialog).findByText("RECORDING");
  return dialog;
}

describe("Mobile Speak Send-direct (recording-stage)", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    // jsdom has no requestAnimationFrame; the dialog's display-only equalizer loop uses it. A no-op
    // that never calls back keeps the animation out of the test without affecting behaviour.
    globalThis.requestAnimationFrame = (() => 0) as typeof globalThis.requestAnimationFrame;
    globalThis.cancelAnimationFrame = (() => {}) as typeof globalThis.cancelAnimationFrame;
  });

  afterEach(() => cleanup());

  it("hands the captured audio to the background pipeline with the Speak-press baseline, so the moved-on guard arms", async () => {
    render(<SessionControls sessionId="sess-42" onFlash={() => {}} onError={() => {}} showKeyRows />);
    const dialog = await typeAndOpenRecordingDialog();

    // Press Send WHILE RECORDING - the fire-and-forget action under test.
    fireEvent.click(within(dialog).getByText("Send"));

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
    expect(listSessions).toHaveBeenCalledTimes(1);

    // The screen is released immediately and nothing on this path blocks on the synchronous
    // transcription or submits a text prompt itself - the Gateway does both server-side.
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Dictate" })).toBeNull());
    expect(transcribeUtterance).not.toHaveBeenCalled();
    expect(sendPrompt).not.toHaveBeenCalled();
  });

  it("a quick Send WAITS for the Speak-press roster read instead of racing it (issue #2478 review defect)", async () => {
    // The roster deliberately does not answer until AFTER Send is pressed - the exact quick-Send race.
    // The component must hand the pipeline the still-pending promise, which resolves to the real
    // record-time position once the roster answers; a peek at Send time would have read undefined.
    let releaseRoster: (roster: { sessionId: string; totalBufferBytes: number }[]) => void = () => {};
    listSessions.mockImplementationOnce(
      () => new Promise<{ sessionId: string; totalBufferBytes: number }[]>((resolve) => { releaseRoster = resolve; }),
    );
    render(<SessionControls sessionId="sess-42" onFlash={() => {}} onError={() => {}} showKeyRows />);
    const dialog = await typeAndOpenRecordingDialog();

    fireEvent.click(within(dialog).getByText("Send")); // the roster read is still in flight

    await waitFor(() => expect(backgroundTranscribeAndSend).toHaveBeenCalledTimes(1));
    const [, , opts] = backgroundTranscribeAndSend.mock.calls[0] as unknown as [
      string,
      unknown,
      { baselineBufferBytes?: Promise<number | undefined> },
    ];
    releaseRoster([{ sessionId: "sess-42", totalBufferBytes: 777 }]); // the roster answers afterwards
    await expect(opts.baselineBufferBytes).resolves.toBe(777);
  });

  it("delivers the clip unguarded (baseline unknown, never zero) when the press-time roster read fails - and that is FINAL", async () => {
    listSessions.mockRejectedValueOnce(new Error("gateway unreachable"));
    render(<SessionControls sessionId="sess-42" onFlash={() => {}} onError={() => {}} showKeyRows />);
    const dialog = await typeAndOpenRecordingDialog();

    fireEvent.click(within(dialog).getByText("Send"));

    // The words still go: a failed press-time read yields an UNKNOWN baseline (the pipeline's
    // documented omit-when-unknown contract, guard skipped for safety) - never a blocked or lost
    // dictation, never a fabricated zero, and never a later substitute reading: exactly one roster
    // call, because a reading taken after the press can include bytes produced during or after the
    // recording and would mask the very movement the guard detects.
    await waitFor(() => expect(backgroundTranscribeAndSend).toHaveBeenCalledTimes(1));
    const [, , opts] = backgroundTranscribeAndSend.mock.calls[0] as unknown as [
      string,
      unknown,
      { baselineBufferBytes?: Promise<number | undefined> },
    ];
    await expect(opts.baselineBufferBytes).resolves.toBeUndefined();
    expect(listSessions).toHaveBeenCalledTimes(1);
  });
});
