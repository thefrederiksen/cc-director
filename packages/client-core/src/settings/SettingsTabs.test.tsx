// The Settings page body, rendered. These are the checks that the UNIFICATION actually holds - the
// reason the tabs and the cards were moved into client-core in the first place.
//
// Why render rather than assert the tab list alone: the tab list already has its own test (tabs.test.ts),
// and a tab list can be perfectly correct while the panel behind a tab renders nothing. That is exactly
// the failure this work exists to prevent - a "Transcription" tab that offers no way to test your
// microphone is worse than no tab, because it tells the user they have already looked.
//
// The panels are the REAL MicTestPanel / TranscriptionTestPanel / MicrophoneQualityPanel. They are not
// stubbed on purpose: stubbing them would prove the tab renders a stub. They need no microphone to draw
// their resting state, and the microphone-quality panel reports its own load failure when the Gateway
// cannot be reached, which is a rendered state, not a blank.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { SettingsTabPanel, SettingsTabStrip } from "./SettingsTabs";
import { setWingmanFastModel, setWingmanModel } from "../api/ai";

// The account snapshot every AI-ish tab reads. Values are arbitrary but must be present: a tab that
// renders "Loading..." forever proves nothing about its content.
const SNAPSHOT = {
  provider: "devthrottle",
  wingmanModel: "test-thinking-model",
  wingmanFastModel: "test-fast-model",
  carModeModel: "test-carmode-model",
  carModeEndPhrase: "over and out",
  ttsModel: "test-speech-model",
  ttsVoice: "test-voice",
  voices: ["test-voice"],
  transcriptionModel: "test-transcription-model",
  catalogAvailable: false,
};

vi.mock("../api/ai", () => ({
  getAiProvider: vi.fn(async () => SNAPSHOT),
  getAiModels: vi.fn(async () => []),
  setWingmanModel: vi.fn(),
  setWingmanFastModel: vi.fn(),
  setCarModeModel: vi.fn(),
  setCarModeEndPhrase: vi.fn(),
  setTtsModel: vi.fn(),
  setTtsVoice: vi.fn(),
  testChat: vi.fn(),
  ttsSample: vi.fn(),
}));

function mount(ui: React.ReactNode) {
  return render(<MemoryRouter>{ui}</MemoryRouter>);
}

beforeEach(() => {
  vi.clearAllMocks();
  // No Gateway in a unit test. Every card must therefore render its own explicit state - a heading and
  // either its content or its own error line - and never a blank panel.
  vi.stubGlobal("fetch", vi.fn(async () => {
    throw new Error("no Gateway in this test");
  }));
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("the Settings tab strip", () => {
  it("offers the three shared tabs on the phone", () => {
    mount(<SettingsTabStrip active="notifications" onSelect={() => {}} surface="mobile" />);
    expect(screen.getAllByRole("tab").map((t) => t.textContent)).toEqual([
      "Notifications",
      "Transcription",
      "Car Mode",
    ]);
  });

  // The Cockpit-only tab, rendered (issue #550). The tab set decides this, not the shell, so the strip is
  // where it can be seen: the same three in the same order, and one more that the phone above does not get.
  it("offers those same three plus Injected text on the Cockpit", () => {
    mount(<SettingsTabStrip active="notifications" onSelect={() => {}} surface="cockpit" />);
    expect(screen.getAllByRole("tab").map((t) => t.textContent)).toEqual([
      "Notifications",
      "Transcription",
      "Car Mode",
      "Injected text",
    ]);
  });

  // Hidden means hidden on both surfaces. Asserted through the RENDERED strip and not only through the
  // tab list, because the strip is what a person actually sees; a list can be right while a shell puts a
  // button back.
  it("offers no AI button on either surface", () => {
    for (const surface of ["cockpit", "mobile"] as const) {
      const { unmount } = mount(
        <SettingsTabStrip active="notifications" onSelect={() => {}} surface={surface} />,
      );
      expect(screen.queryByRole("tab", { name: "AI" })).toBeNull();
      unmount();
    }
  });

  it("marks exactly the active tab selected", () => {
    mount(<SettingsTabStrip active="transcription" onSelect={() => {}} surface="cockpit" />);
    const selected = screen.getAllByRole("tab").filter((t) => t.getAttribute("aria-selected") === "true");
    expect(selected.map((t) => t.textContent)).toEqual(["Transcription"]);
  });
});

describe("the Transcription tab", () => {
  // THE point of this work: the dictation checks used to be a separate page on the desktop and two
  // separate screens on the phone, so neither Settings page could answer "why is my dictation wrong?".
  it("carries the transcription model and BOTH on-demand checks", async () => {
    mount(<SettingsTabPanel tab="transcription" />);

    expect(await screen.findByText("test-transcription-model")).toBeTruthy();
    expect(screen.getByRole("heading", { name: "Test your microphone" })).toBeTruthy();
    expect(screen.getByRole("heading", { name: "Test transcription" })).toBeTruthy();
    expect(screen.getByRole("heading", { name: "How your microphones are doing" })).toBeTruthy();
  });

  // The phone has no Transcription Health page and no account page. A shared component that hard-coded
  // those links would put dead links on the phone, so the links are opt-in per surface.
  it("renders no health link unless the mounting surface says it has one", async () => {
    const { unmount } = mount(<SettingsTabPanel tab="transcription" />);
    await screen.findByText("test-transcription-model");
    expect(screen.queryByRole("link", { name: "Transcription Health" })).toBeNull();
    unmount();

    mount(<SettingsTabPanel tab="transcription" transcriptionHealthHref="/transcription" />);
    await screen.findByText("test-transcription-model");
    expect(screen.getByRole("link", { name: "Transcription Health" })).toBeTruthy();
  });
});

// The AI tab is no longer offered anywhere - not in the strip, and not by ?tab= either. The COMPONENT is
// still here and still built, because hiding it is meant to be reversible by one word rather than by
// restoring a deleted screen (see tabs.ts). These are the checks that the thing being kept is still whole:
// mounted directly, it draws in full and it writes nothing.
describe("the AI tab, hidden but kept", () => {
  it("draws the whole tab, with the account's saved models selected", async () => {
    mount(<SettingsTabPanel tab="ai" />);

    const thinking = (await screen.findByLabelText("Thinking model")) as HTMLSelectElement;
    const fast = screen.getByLabelText("Fast model") as HTMLSelectElement;
    expect(thinking.value).toBe("test-thinking-model");
    expect(fast.value).toBe("test-fast-model");
    expect(screen.getByLabelText("Speech model")).toBeTruthy();
    expect(screen.getByLabelText("Voice")).toBeTruthy();
  });

  // The classic version of this bug: a control that is no longer in front of anybody quietly writes over
  // what it holds - typically a null - the first time it is drawn. The stored models live on the Gateway
  // per account, they are still what the wingman runs on, and merely rendering this tab must write
  // NOTHING. Only a person choosing does that.
  it("writes no model setting merely by being rendered", async () => {
    mount(<SettingsTabPanel tab="ai" />);
    await screen.findByLabelText("Thinking model");
    expect(setWingmanModel).not.toHaveBeenCalled();
    expect(setWingmanFastModel).not.toHaveBeenCalled();
  });
});

describe("what moved off the AI tab", () => {
  // The phone used to carry the Car Mode model on its AI screen while the desktop carried it on a Car
  // Mode tab. It is on the Car Mode tab now, on both, and it must not be in two places at once.
  it("puts the Car Mode model on the Car Mode tab and nowhere else", async () => {
    const { unmount } = mount(<SettingsTabPanel tab="ai" />);
    expect(await screen.findByLabelText("Thinking model")).toBeTruthy();
    expect(screen.queryByLabelText("Model")).toBeNull();
    unmount();

    mount(<SettingsTabPanel tab="carmode" />);
    expect(await screen.findByLabelText("Model")).toBeTruthy();
    expect(screen.getByLabelText("End phrase (say this to finish your turn)")).toBeTruthy();
  });

  it("keeps the account link off surfaces that have no account page", async () => {
    const { unmount } = mount(<SettingsTabPanel tab="ai" />);
    await screen.findByLabelText("Thinking model");
    expect(screen.queryByRole("link", { name: "Manage account" })).toBeNull();
    unmount();

    mount(<SettingsTabPanel tab="ai" accountHref="/account" />);
    await screen.findByLabelText("Thinking model");
    expect(screen.getByRole("link", { name: "Manage account" })).toBeTruthy();
  });
});
