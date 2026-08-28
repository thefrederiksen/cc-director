import { describe, expect, it } from "vitest";
import { describeWakeWordWeakness, matchWakeWord, normaliseForMatching } from "./wakeWordMatcher";

describe("normaliseForMatching", () => {
  it("lower cases, removes punctuation and collapses spaces", () => {
    expect(normaliseForMatching("  Wilson,   set a TIMER! ")).toBe("wilson set a timer");
  });

  it("returns an empty string for text with nothing to match on", () => {
    expect(normaliseForMatching("... !!! ")).toBe("");
  });
});

describe("matchWakeWord", () => {
  it("finds the wake word on its own and reports no command", () => {
    const match = matchWakeWord("Wilson", "wilson");
    expect(match).not.toBeNull();
    expect(match?.command).toBe("");
    expect(match?.wordIndex).toBe(0);
  });

  it("captures what was said after the wake word in the same breath", () => {
    const match = matchWakeWord("Wilson, set a timer for ten minutes", "Wilson");
    expect(match?.command).toBe("set a timer for ten minutes");
  });

  it("finds the wake word part way through a sentence", () => {
    const match = matchWakeWord("so anyway wilson play something quiet", "wilson");
    expect(match?.wordIndex).toBe(2);
    expect(match?.command).toBe("play something quiet");
  });

  it("uses the last occurrence, because that is the one the person meant", () => {
    const match = matchWakeWord("wilson no wilson play the news", "wilson");
    expect(match?.wordIndex).toBe(2);
    expect(match?.command).toBe("play the news");
  });

  it("matches whole words only, so a wake word inside a longer word does not fire", () => {
    expect(matchWakeWord("plug in the adapter", "ada")).toBeNull();
  });

  it("supports a wake word of more than one word, in order", () => {
    expect(matchWakeWord("hey wilson what is the time", "hey wilson")?.command).toBe("what is the time");
    expect(matchWakeWord("wilson hey what is the time", "hey wilson")).toBeNull();
  });

  it("returns null when the wake word is absent", () => {
    expect(matchWakeWord("set a timer for ten minutes", "wilson")).toBeNull();
  });

  it("treats an empty wake word as nothing configured rather than matching everything", () => {
    expect(matchWakeWord("anything at all", "")).toBeNull();
    expect(matchWakeWord("anything at all", "   ")).toBeNull();
  });

  it("returns null when the transcript is shorter than the wake word", () => {
    expect(matchWakeWord("hey", "hey wilson")).toBeNull();
  });
});

describe("describeWakeWordWeakness", () => {
  it("asks for a wake word when none is set", () => {
    expect(describeWakeWordWeakness("")).toMatch(/Choose a wake word/);
  });

  it("warns about very short wake words", () => {
    expect(describeWakeWordWeakness("ada")).toMatch(/Very short/);
  });

  it("warns about everyday words", () => {
    expect(describeWakeWordWeakness("Assistant")).toMatch(/everyday word/);
  });

  it("accepts a distinctive name", () => {
    expect(describeWakeWordWeakness("Peregrine")).toBeNull();
    expect(describeWakeWordWeakness("Wilson")).toBeNull();
  });
});
