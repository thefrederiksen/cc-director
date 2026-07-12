import { describe, expect, it } from "vitest";
import { decideControlAction, detectEndPhrase, detectInterrupt, detectPhraseAtEnd, normalizeTranscript } from "./controlPhrases";

// The walkie-talkie discipline is load-bearing: the assistant must never speak until "over and out",
// and must never mistake a mid-sentence "over" or "stopover" for the end word. These tests pin the
// exact-phrase, only-at-the-end, whole-word rules the mission settled (decision 1).

describe("normalizeTranscript", () => {
  it("lowercases, strips punctuation, and collapses whitespace", () => {
    expect(normalizeTranscript("  Over, and   OUT! ")).toBe("over and out");
  });
  it("returns empty for punctuation-only input", () => {
    expect(normalizeTranscript("...")).toBe("");
  });
});

describe("detectEndPhrase", () => {
  it("ends the turn when the phrase is the whole transcript", () => {
    const r = detectEndPhrase("over and out");
    expect(r.ended).toBe(true);
    expect(r.command).toBe("");
  });

  it("ends the turn and strips the phrase, keeping the command", () => {
    const r = detectEndPhrase("How many sessions need me right now over and out");
    expect(r.ended).toBe(true);
    expect(r.command).toBe("how many sessions need me right now");
  });

  it("tolerates capitalization and trailing punctuation", () => {
    const r = detectEndPhrase("Start a session in the devthrottle repo. Over and out.");
    expect(r.ended).toBe(true);
    expect(r.command).toBe("start a session in the devthrottle repo");
  });

  it("does NOT end on plain 'over' alone", () => {
    expect(detectEndPhrase("come over").ended).toBe(false);
  });

  it("does NOT end on plain 'out' alone", () => {
    expect(detectEndPhrase("check it out").ended).toBe(false);
  });

  it("does NOT end when the phrase is in the MIDDLE, not the last thing said", () => {
    expect(detectEndPhrase("over and out was the old radio sign off").ended).toBe(false);
  });

  it("does NOT end on a near-miss embedded in other words", () => {
    expect(detectEndPhrase("we talked about the stopover and outages").ended).toBe(false);
  });

  it("does not end an empty transcript", () => {
    expect(detectEndPhrase("").ended).toBe(false);
  });
});

describe("detectInterrupt", () => {
  it("fires on 'stop'", () => {
    expect(detectInterrupt("stop")).toBe(true);
  });
  it("fires on 'wait'", () => {
    expect(detectInterrupt("wait")).toBe(true);
  });
  it("fires on the two-word 'shut up'", () => {
    expect(detectInterrupt("no shut up please")).toBe(true);
  });
  it("fires when the interrupt word is not the last word", () => {
    expect(detectInterrupt("stop reading that one")).toBe(true);
  });
  it("does NOT fire on a whole word that merely contains an interrupt word", () => {
    expect(detectInterrupt("start the stopwatch")).toBe(false);
    expect(detectInterrupt("we are waiting on the build")).toBe(false);
  });
  it("does not fire on empty input", () => {
    expect(detectInterrupt("")).toBe(false);
  });
});

describe("decideControlAction (the turn-taking decision)", () => {
  it("ends the turn only from Listening on the end phrase", () => {
    expect(decideControlAction("listening", "how many need me over and out")).toBe("end");
    expect(decideControlAction("listening", "how many need me")).toBe("none");
  });

  it("interrupts only from Speaking on an interrupt word", () => {
    expect(decideControlAction("speaking", "stop")).toBe("interrupt");
    expect(decideControlAction("speaking", "keep going")).toBe("none");
  });

  it("does NOT end from Speaking and does NOT interrupt from Listening (phase-gated)", () => {
    expect(decideControlAction("speaking", "over and out")).toBe("none");
    expect(decideControlAction("listening", "stop")).toBe("none");
  });

  it("ignores control words entirely while Thinking (nothing to interrupt, turn committed)", () => {
    expect(decideControlAction("thinking", "over and out")).toBe("none");
    expect(decideControlAction("thinking", "stop")).toBe("none");
  });
});

// detectPhraseAtEnd is the configurable end-phrase matcher behind the hands-free end-of-turn watch and the
// Car Mode settings (the owner chooses his own sign-off phrase). Same trailing-only, whole-phrase rule as
// detectEndPhrase, but for any phrase.
describe("detectPhraseAtEnd (configurable end phrase)", () => {
  it("ends and strips the phrase when the transcript ends with it", () => {
    expect(detectPhraseAtEnd("start the tests over and out", "over and out")).toEqual({
      ended: true,
      command: "start the tests",
    });
    expect(detectPhraseAtEnd("read me that one, I'm done.", "i am done")).toEqual({ ended: false, command: "" });
    expect(detectPhraseAtEnd("read me that one im done", "im done")).toEqual({ ended: true, command: "read me that one" });
  });

  it("matches a custom phrase the same way (case/punctuation-insensitive, trailing only)", () => {
    expect(detectPhraseAtEnd("How many need me, GO AHEAD.", "go ahead")).toEqual({ ended: true, command: "how many need me" });
    expect(detectPhraseAtEnd("go ahead and start it", "go ahead")).toEqual({ ended: false, command: "" }); // mid-sentence
    expect(detectPhraseAtEnd("go ahead", "go ahead")).toEqual({ ended: true, command: "" }); // whole transcript is the phrase
  });

  it("never ends on an empty phrase (an unset setting must not end every turn)", () => {
    expect(detectPhraseAtEnd("anything at all", "")).toEqual({ ended: false, command: "" });
    expect(detectPhraseAtEnd("anything at all", "   ")).toEqual({ ended: false, command: "" });
  });

  it("does not fire when the transcript is empty", () => {
    expect(detectPhraseAtEnd("", "over and out")).toEqual({ ended: false, command: "" });
  });
});
