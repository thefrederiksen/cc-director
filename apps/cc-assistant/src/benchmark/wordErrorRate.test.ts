import { describe, expect, it } from "vitest";
import { wordErrorRate } from "./wordErrorRate";

describe("wordErrorRate", () => {
  it("scores a perfect transcript as zero", () => {
    expect(wordErrorRate("set a timer for ten minutes", "set a timer for ten minutes").rate).toBe(0);
  });

  it("ignores capitalisation and punctuation, which are not mishearings", () => {
    expect(wordErrorRate("set a timer for ten minutes", "Set a timer for ten minutes.").rate).toBe(0);
  });

  it("counts one wrong word out of six as one sixth", () => {
    const result = wordErrorRate("set a timer for ten minutes", "set a timer for two minutes");
    expect(result.substitutions).toBe(1);
    expect(result.referenceWords).toBe(6);
    expect(result.rate).toBeCloseTo(1 / 6, 4);
  });

  it("counts a missing word as a deletion", () => {
    const result = wordErrorRate("set a timer for ten minutes", "set a timer ten minutes");
    expect(result.deletions).toBe(1);
    expect(result.insertions).toBe(0);
    expect(result.rate).toBeCloseTo(1 / 6, 4);
  });

  it("counts an invented word as an insertion", () => {
    const result = wordErrorRate("set a timer", "set a big timer");
    expect(result.insertions).toBe(1);
    expect(result.rate).toBeCloseTo(1 / 3, 4);
  });

  it("can exceed one when a model invents a great deal", () => {
    expect(wordErrorRate("hello", "hello there and welcome to the show").rate).toBeGreaterThan(1);
  });

  it("scores an empty transcript against real speech as total failure", () => {
    expect(wordErrorRate("set a timer for ten minutes", "").rate).toBe(1);
  });

  it("keeps deletions and insertions apart, because they are different problems", () => {
    const dropped = wordErrorRate("one two three four", "one four");
    expect(dropped.deletions).toBe(2);
    expect(dropped.insertions).toBe(0);

    const invented = wordErrorRate("one four", "one two three four");
    expect(invented.insertions).toBe(2);
    expect(invented.deletions).toBe(0);
  });
});

describe("numbers written as digits versus spelled out", () => {
  it("does not count a digit against its word as a mistake", () => {
    expect(wordErrorRate("set a timer for ten minutes", "Set a timer for 10 minutes.").rate).toBe(0);
  });

  it("works in the other direction too", () => {
    expect(wordErrorRate("set a timer for 10 minutes", "set a timer for ten minutes").rate).toBe(0);
  });

  it("still catches a genuinely wrong number", () => {
    expect(wordErrorRate("set a timer for ten minutes", "set a timer for 2 minutes").rate).toBeGreaterThan(0);
  });
});
