import { describe, expect, it } from "vitest";
import { detectEndPhrase, detectInterrupt, normalizeTranscript } from "./controlPhrases";

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
