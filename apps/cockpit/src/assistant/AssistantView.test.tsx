// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, within, cleanup } from "@testing-library/react";

// Regression tests for the three Assistant screen complaints (owner report, this branch):
//
//   1. The recording button did not use the shared dictation control. Speaking a question was a bare
//      round microphone button that turned red: no level bars, no elapsed timer, no Pause to check the
//      words, and no way out of a recording except sending it. It now opens the SHARED
//      DictationDialog - the same control the session composer's Speak button opens - and hands that
//      dialog's Send text to the turn machine.
//   2. The Chat / Voice toggle sat in the top-right corner, where (on the phone) the fixed network
//      status pill landed on top of it. The toggle is no longer cornered: the blank screen shows it
//      big in the MIDDLE, and once a conversation exists it is the header's compact control.
//   3. On the blank screen the toggle was a 13px pill in the chrome. Blank is exactly when choosing
//      how to ask is the only decision on the screen, so it renders large and centred there.
//
// Revert-proof: put the mic button back on an inline recorder (the old startTalk path) and test 2
// finds no "Dictate" dialog; drop the large empty-state chooser and test 1 finds no large tablist.

const { assistantTurn, postBrainWarmup, transcribeUtterance } = vi.hoisted(() => ({
  assistantTurn: vi.fn(async () => ({ spoken: "Four sessions are open.", actions: [], pendingConfirmation: false })),
  postBrainWarmup: vi.fn(async () => {}),
  transcribeUtterance: vi.fn(async () => ({ text: "how many sessions are open", deliveryId: "utt-11" })),
}));

// The brain (POST /assistant/turn) and the keep-warm ping.
vi.mock("@devthrottle/client-core/assistant/assistantApi", () => ({ assistantTurn }));
vi.mock("@devthrottle/client-core/fleetbrain/brainApi", () => ({
  postBrainWarmup,
  speakText: vi.fn(async () => new Blob(["audio"])),
}));

// The Gateway client boundary. transcribeUtterance is the dictation dialog's tenant-aware
// transcription; authHeaders is what the client error channel reads.
vi.mock("@devthrottle/client-core/api/client", () => ({
  transcribeUtterance,
  authHeaders: () => ({}),
  gatewayErrorMessage: (e: unknown) => (e instanceof Error ? e.message : String(e)),
}));

// Mic + audio-decode boundaries jsdom cannot provide (same fakes as the composer Speak test). The
// recorder fires onCaptureLive on start so the dialog reaches RECORDING without a real microphone, and
// a 1000 ms clip that decodes to 1.0 s means zero capture deficit, so Send commits instead of parking
// on a dropped-audio warning.
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
    // The liveness clocks the dialog's animation loop reads every frame. Zero = a healthy microphone,
    // so the live capture alarm stays quiet and this file keeps testing what it is about.
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
  blobToWav16kMono: async () => ({ wav: new Blob(["wav"]), decodedSeconds: 1, sourceBytes: 1000 }),
}));
vi.mock("@devthrottle/client-core/dictation/readyCue", () => ({
  playReadyCue: () => {},
  primeCueAudio: () => {},
  releaseCueAudio: () => {},
  startThinkingCue: () => () => {},
  playYourTurnCue: () => {},
}));

import { AssistantView } from "./AssistantView";

describe("Assistant screen", () => {
  // This project does not run vitest with globals, so testing-library's auto-cleanup is off: without
  // this every render stacks in the same document and the queries find two of everything.
  afterEach(() => cleanup());

  beforeEach(() => {
    vi.clearAllMocks();
    // jsdom has no requestAnimationFrame; the dictation dialog's display-only equalizer loop uses it.
    globalThis.requestAnimationFrame = (() => 0) as typeof globalThis.requestAnimationFrame;
    globalThis.cancelAnimationFrame = (() => {}) as typeof globalThis.cancelAnimationFrame;
    // The turn id. jsdom's crypto has getRandomValues but not always randomUUID.
    if (typeof globalThis.crypto?.randomUUID !== "function") {
      Object.defineProperty(globalThis.crypto, "randomUUID", { value: () => "11111111-1111-4111-8111-111111111111", configurable: true });
    }
  });

  it("offers ONE Chat / Voice control on the blank screen, big and in the middle - not a small pill in a corner", () => {
    render(<AssistantView />);

    const toggle = screen.getByRole("tablist", { name: "Assistant mode" });
    // The large variant: a wide, centred target in the empty state (52px-tall halves), not the 13px
    // header pill the owner could barely hit.
    expect(toggle.classList.contains("asst-mode-large")).toBe(true);
    // Exactly one such control - the header's compact copy stands down while the screen is blank, so
    // there are never two live Chat / Voice controls to choose between.
    expect(screen.getAllByRole("tab")).toHaveLength(2);
    expect(screen.getByRole("tab", { name: "Chat" })).toBeTruthy();
    expect(screen.getByRole("tab", { name: "Voice" })).toBeTruthy();
  });

  it("switching to Voice keeps the choice reachable and changes what the screen offers", () => {
    render(<AssistantView />);

    fireEvent.click(screen.getByRole("tab", { name: "Voice" }));

    expect(screen.getByRole("tab", { name: "Voice" }).getAttribute("aria-selected")).toBe("true");
    // Voice mode replaces the typing composer with the press-to-speak dock.
    expect(screen.queryByPlaceholderText(/Ask about your fleet/i)).toBeNull();
    expect(screen.getByText(/Press to speak/i)).toBeTruthy();
  });

  it("the microphone opens the SHARED dictation control - level bars, timer, Cancel and Send - and its Send asks the fleet", async () => {
    render(<AssistantView />);

    fireEvent.click(screen.getByTitle("Dictate your question"));

    // The shared control, not a button that merely turns red: the dictation dialog, in RECORDING, with
    // its equalizer bars, its elapsed timer, and a way OUT of the recording (Cancel).
    const dialog = await screen.findByRole("dialog", { name: "Dictate" });
    await within(dialog).findByText("RECORDING");
    expect(dialog.querySelectorAll(".dictate-eq-bar")).toHaveLength(9);
    expect(dialog.querySelector(".dictate-timer")?.textContent).toBe("0:00");
    expect(within(dialog).getByText("Cancel")).toBeTruthy();
    expect(within(dialog).getByText("Insert")).toBeTruthy();

    fireEvent.click(within(dialog).getByText("Send"));

    // The dictated words are transcribed through the one Gateway transcription path and asked as a turn.
    await waitFor(() => expect(assistantTurn).toHaveBeenCalledWith("how many sessions are open", expect.any(String)));
    expect(transcribeUtterance).toHaveBeenCalledTimes(1);

    // The dialog closes, the answer lands, and the mode control is still there - now the header's
    // compact copy, since the screen is no longer blank.
    await waitFor(() => expect(screen.queryByRole("dialog", { name: "Dictate" })).toBeNull());
    await screen.findByText("Four sessions are open.");
    const toggle = screen.getByRole("tablist", { name: "Assistant mode" });
    expect(toggle.classList.contains("asst-mode-large")).toBe(false);
  });

  it("voice mode dictation offers no Insert - there is no box to drop the words into, Send asks straight away", async () => {
    render(<AssistantView />);
    fireEvent.click(screen.getByRole("tab", { name: "Voice" }));

    fireEvent.click(screen.getByRole("button", { name: "Dictate your question" }));

    const dialog = await screen.findByRole("dialog", { name: "Dictate" });
    await within(dialog).findByText("RECORDING");
    expect(within(dialog).queryByText("Insert")).toBeNull();
    expect(within(dialog).getByText("Send")).toBeTruthy();
  });
});
