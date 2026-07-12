import { describe, expect, it } from "vitest";
import { splitFirstSentence } from "./useCarMode";

// The first-sentence split lets Car Mode start speaking the first sentence while the rest is still being
// synthesized (performance round). It must return exactly two parts (at most one extra synthesis), keep a
// single-sentence reply whole, and never split off a tiny lead fragment that would only add a round trip.

describe("splitFirstSentence", () => {
  it("splits a two-sentence reply after the first sentence", () => {
    const [first, rest] = splitFirstSentence("Two sessions need you. The newest is Local Files Manager.");
    expect(first).toBe("Two sessions need you.");
    expect(rest).toBe("The newest is Local Files Manager.");
  });

  it("keeps a single-sentence reply as one chunk with an empty remainder", () => {
    const [first, rest] = splitFirstSentence("Nothing needs you right now.");
    expect(first).toBe("Nothing needs you right now.");
    expect(rest).toBe("");
  });

  it("does not split off a tiny lead fragment", () => {
    const [first, rest] = splitFirstSentence("Okay. I left Old Worker alone.");
    expect(first).toBe("Okay. I left Old Worker alone.");
    expect(rest).toBe("");
  });

  it("puts everything after the first sentence into the remainder, not a third part", () => {
    const [first, rest] = splitFirstSentence("I started a session. It is warming up now. I will let you know.");
    expect(first).toBe("I started a session.");
    expect(rest).toBe("It is warming up now. I will let you know.");
  });

  it("trims surrounding whitespace", () => {
    const [first, rest] = splitFirstSentence("   Done deleting Old Worker.   Anything else?   ");
    expect(first).toBe("Done deleting Old Worker.");
    expect(rest).toBe("Anything else?");
  });
});
