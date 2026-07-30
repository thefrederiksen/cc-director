import { afterEach, describe, expect, it, vi } from "vitest";
import { pickVoiceFor, speakLocally } from "./localSpeech";
import { utteranceFor } from "./spokenUtterance";

// The BROWSER expression of the one speech contract (issue #1031). Same property as the C# side: nothing speaks
// without a language, one factory builds the utterance, and the sink only plays what it is handed.
//
// The failure being pinned is silent by nature. The words arrive from the Gateway already translated; what went
// missing was the LANGUAGE on the utterance, so a correct French refusal was read out in the device's default
// English voice. It plays, nothing errors, and it is wrong - the same shape as a stripped accent.

/** Stub the PLATFORM engine - the sink reaches for it itself, which is what makes it the only place that can -
 *  and hand back what it was asked to say. */
function fakeEngine(voices: { lang: string; name: string }[] = [], extra?: Record<string, unknown>) {
  const spoken: SpeechSynthesisUtterance[] = [];
  vi.stubGlobal("SpeechSynthesisUtterance", class {
    lang = "";
    voice: unknown = null;
    constructor(public text: string) {}
  });
  const engine = {
    cancel: vi.fn(),
    getVoices: () => voices as unknown as SpeechSynthesisVoice[],
    speak: (u: SpeechSynthesisUtterance) => void spoken.push(u),
    ...extra,
  };
  vi.stubGlobal("speechSynthesis", engine);
  return { spoken, engine };
}

afterEach(() => vi.unstubAllGlobals());

describe("utteranceFor", () => {
  it("carries the words and the language it was given", () => {
    const utterance = utteranceFor("fr", "Cette session attend une réponse.");
    expect(utterance.language).toBe("fr");
    expect(utterance.text).toBe("Cette session attend une réponse.");
  });

  // THROWS rather than defaulting to English. A quiet English default is the reported bug itself - the setting
  // appears to do nothing - and every caller is on a path that has already shown the same notice on screen, so a
  // throw is visible instead of silently mispronouncing.
  it("refuses to exist without a language", () => {
    expect(() => utteranceFor("", "some words")).toThrow(/needs a language/);
    expect(() => utteranceFor("   ", "some words")).toThrow(/needs a language/);
  });

  it("refuses to exist without words", () => {
    expect(() => utteranceFor("fr", "")).toThrow(/needs words/);
  });

  // A language code arrives trimmed, because it is about to be compared against device voice tags.
  it("trims the language it was given", () => {
    expect(utteranceFor("  es  ", "unas palabras").language).toBe("es");
  });
});

describe("speakLocally", () => {
  it("tells the engine the language and picks a voice that speaks it", () => {
    const { spoken } = fakeEngine([
      { lang: "en-US", name: "Samantha" },
      { lang: "fr-FR", name: "Amelie" },
    ]);

    expect(speakLocally(utteranceFor("fr", "Cette session attend une réponse."))).toBe(true);

    expect(spoken).toHaveLength(1);
    expect(spoken[0].lang).toBe("fr");
    expect((spoken[0].voice as unknown as { name: string }).name).toBe("Amelie");
  });

  // The voice is a nicety; the notice is not. Every engine offers no voice list until its list has loaded, so
  // trading a wrong accent for total silence would be the worse of the two failures.
  it("still speaks when the device has no voice for that language", () => {
    const { spoken } = fakeEngine([{ lang: "en-US", name: "Samantha" }]);

    expect(speakLocally(utteranceFor("es", "Esta sesión está esperando."))).toBe(true);

    expect(spoken[0].lang).toBe("es");
    expect(spoken[0].voice).toBeNull();
  });

  // An engine with no getVoices at all - some do not have one - must cost the VOICE and never the UTTERANCE.
  it("still speaks on an engine that offers no voice list", () => {
    const { spoken } = fakeEngine([], { getVoices: undefined });

    expect(speakLocally(utteranceFor("fr", "Bonjour."))).toBe(true);
    expect(spoken[0].lang).toBe("fr");
  });

  it("cancels whatever was speaking first, so two notices never overlap", () => {
    const { engine } = fakeEngine();
    speakLocally(utteranceFor("en", "A notice."));
    expect(engine.cancel).toHaveBeenCalled();
  });

  // No engine at all (a browser without speech synthesis) is a normal state, not an error: the on-screen notice
  // is the primary channel and it has already been shown.
  it("reports that it did not speak when the platform has no engine", () => {
    vi.stubGlobal("speechSynthesis", undefined);
    expect(speakLocally(utteranceFor("en", "A notice."))).toBe(false);
  });

  it("never throws when the engine does", () => {
    fakeEngine([], { cancel: () => { throw new Error("engine is broken"); } });
    expect(speakLocally(utteranceFor("en", "A notice."))).toBe(false);
  });
});

describe("pickVoiceFor", () => {
  const voices = [
    { lang: "en-US", name: "Samantha" },
    { lang: "fr-FR", name: "Amelie" },
    { lang: "es_ES", name: "Monica" },
  ];

  // Matched on the PRIMARY SUBTAG, which is the whole point: the Gateway decides a LANGUAGE ("fr") and devices
  // carry region-tagged voices ("fr-FR"). Comparing whole tags would find nothing on a real device.
  it("finds a voice for the language the Gateway named", () => {
    expect(pickVoiceFor(voices, "fr")?.name).toBe("Amelie");
  });

  // Some Android engines report "es_ES"; a hyphen-only rule would silently miss those, and silently missing
  // means Spanish read aloud in an English voice.
  it("understands an underscore-tagged voice", () => {
    expect(pickVoiceFor(voices, "es")?.name).toBe("Monica");
  });

  it("ignores case on both sides", () => {
    expect(pickVoiceFor([{ lang: "FR-fr", name: "Amelie" }], "Fr")?.name).toBe("Amelie");
  });

  // Never a voice of the WRONG language: "close enough" is the failure being fixed, so an absence stays absent.
  it("returns nothing rather than a voice of another language", () => {
    expect(pickVoiceFor(voices, "de")).toBeNull();
    expect(pickVoiceFor([], "fr")).toBeNull();
  });
});
