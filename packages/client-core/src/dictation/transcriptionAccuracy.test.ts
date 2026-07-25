import { describe, expect, it } from "vitest";
import { alignTokens, judgeAccuracy, scoreTranscription, tokenize } from "./transcriptionAccuracy";
import { languageByCode, preferredLanguage, TEST_LANGUAGES } from "./languages";

// The transcription check reports a NUMBER to the user and, on a bad score, tells them their setup is
// at fault. So the number has to be right, and it has to be right in every language we offer - a
// scorer that silently mis-handles Chinese, or that marks a perfect transcript down for punctuation,
// would send people chasing microphone problems they do not have.

describe("tokenize", () => {
  it("folds case and drops punctuation, which are not transcription errors", () => {
    expect(tokenize("Yesterday, I finished six tasks.", "words")).toEqual([
      "yesterday",
      "i",
      "finished",
      "six",
      "tasks",
    ]);
  });

  it("drops the punctuation of other writing systems too, not just the Latin comma", () => {
    // Arabic comma, Devanagari danda, Chinese full stop: one rule has to cover all of them.
    expect(tokenize("bonjour، le monde", "words")).toEqual(["bonjour", "le", "monde"]);
    expect(tokenize("नमस्ते दुनिया।", "words")).toEqual(["नमस्ते", "दुनिया"]);
  });

  it("splits Chinese into characters, because it is not written with spaces between words", () => {
    // A whitespace split would make this ONE token and score the whole sentence right or wrong.
    const tokens = tokenize("天气很冷。", "characters");
    expect(tokens).toEqual(["天", "气", "很", "冷"]);
  });

  it("ignores spacing differences in a character language", () => {
    expect(tokenize("天气 很冷", "characters")).toEqual(tokenize("天气很冷", "characters"));
  });

  it("returns nothing for empty or punctuation-only text", () => {
    expect(tokenize("", "words")).toEqual([]);
    expect(tokenize("  ...  ", "words")).toEqual([]);
  });
});

describe("alignTokens", () => {
  it("marks a perfect match as all equal", () => {
    const diff = alignTokens(["a", "b", "c"], ["a", "b", "c"]);
    expect(diff.every((d) => d.op === "equal")).toBe(true);
  });

  it("identifies a substituted word and keeps both sides for display", () => {
    const diff = alignTokens(["the", "cat", "sat"], ["the", "hat", "sat"]);
    const sub = diff.find((d) => d.op === "substitute");
    expect(sub).toEqual({ op: "substitute", expected: "cat", actual: "hat" });
  });

  it("identifies a dropped word as a deletion", () => {
    const diff = alignTokens(["the", "cat", "sat"], ["the", "sat"]);
    expect(diff.find((d) => d.op === "delete")?.expected).toBe("cat");
  });

  it("identifies an invented word as an insertion", () => {
    const diff = alignTokens(["the", "sat"], ["the", "cat", "sat"]);
    expect(diff.find((d) => d.op === "insert")?.actual).toBe("cat");
  });

  it("handles either side being empty", () => {
    expect(alignTokens([], ["a", "b"]).every((d) => d.op === "insert")).toBe(true);
    expect(alignTokens(["a", "b"], []).every((d) => d.op === "delete")).toBe(true);
    expect(alignTokens([], [])).toEqual([]);
  });

  it("reads the alignment in order, so the screen can render the passage left to right", () => {
    const diff = alignTokens(["one", "two", "three"], ["one", "too", "three"]);
    expect(diff.map((d) => d.expected)).toEqual(["one", "two", "three"]);
  });
});

describe("scoreTranscription", () => {
  it("scores a perfect transcript as 100% however it was punctuated and capitalised", () => {
    const passage = "Yesterday I finished six small tasks before lunch.";
    const result = scoreTranscription(passage, "yesterday i finished six small tasks before lunch", "words");
    expect(result.accuracy).toBe(1);
    expect(result.errorRate).toBe(0);
    expect(result.substitutions + result.deletions + result.insertions).toBe(0);
  });

  it("computes the standard word error rate", () => {
    // Eight expected words, one misheard: one error in eight.
    const result = scoreTranscription("one two three four five six seven eight", "one two free four five six seven eight", "words");
    expect(result.substitutions).toBe(1);
    expect(result.errorRate).toBeCloseTo(1 / 8);
    expect(result.accuracy).toBeCloseTo(7 / 8);
  });

  it("counts dropped words and invented words separately", () => {
    const dropped = scoreTranscription("one two three four", "one three four", "words");
    expect(dropped.deletions).toBe(1);

    const invented = scoreTranscription("one two three", "one two extra three", "words");
    expect(invented.insertions).toBe(1);
  });

  it("never reports negative accuracy when the transcriber invents more than it heard", () => {
    const result = scoreTranscription("one two", "completely different words all over the place", "words");
    expect(result.errorRate).toBeGreaterThan(1);
    expect(result.accuracy).toBe(0);
  });

  it("scores an empty transcript as zero rather than throwing", () => {
    const result = scoreTranscription("one two three", "", "words");
    expect(result.accuracy).toBe(0);
    expect(result.deletions).toBe(3);
  });

  it("scores Chinese by character", () => {
    const perfect = scoreTranscription("天气很冷", "天气很冷", "characters");
    expect(perfect.accuracy).toBe(1);

    // One character in four misheard.
    const oneWrong = scoreTranscription("天气很冷", "天气很热", "characters");
    expect(oneWrong.accuracy).toBeCloseTo(0.75);
  });
});

describe("judgeAccuracy", () => {
  const score = (expected: string, actual: string) => scoreTranscription(expected, actual, "words");

  it("calls a near-perfect transcript excellent", () => {
    const verdict = judgeAccuracy(score("one two three four five six seven eight nine ten", "one two three four five six seven eight nine ten"), "English");
    expect(verdict.rating).toBe("excellent");
    expect(verdict.headline).toContain("100%");
  });

  it("calls a mostly-right transcript good and points at the microphone test", () => {
    // Eight of ten right.
    const verdict = judgeAccuracy(score("one two three four five six seven eight nine ten", "one two three four five six seven eight wrong words"), "English");
    expect(verdict.rating).toBe("good");
    expect(verdict.detail).toContain("microphone");
  });

  it("calls a poor transcript poor and names the usual cause", () => {
    const verdict = judgeAccuracy(score("one two three four five six seven eight nine ten", "one two wrong wrong wrong wrong wrong wrong wrong wrong"), "English");
    expect(verdict.rating).toBe("poor");
    expect(verdict.detail).toContain("Bluetooth");
  });

  it("handles a transcript with nothing in common without claiming a percentage is meaningful", () => {
    const verdict = judgeAccuracy(score("one two three", "completely unrelated text"), "Danish");
    expect(verdict.rating).toBe("poor");
    expect(verdict.headline).toContain("None of the passage");
    expect(verdict.detail).toContain("Danish");
  });

  it("names the language it is talking about, so a multi-language run is readable", () => {
    const verdict = judgeAccuracy(score("one two three", "one two three"), "Danish");
    expect(verdict.detail).toContain("Danish");
  });
});

describe("the language pack", () => {
  // The officially supported set, and nothing wider. This test is the guard on that: offering a test
  // in an unsupported language invites someone to measure what we never promised and read a poor
  // score as a defect. Widening this list is a product decision, so it has to be a deliberate edit
  // here and not a quiet append to the pack.
  const SUPPORTED = ["en", "da", "de", "fr", "es"];

  it("offers exactly the languages DevThrottle officially supports", () => {
    expect(TEST_LANGUAGES.map((l) => l.code)).toEqual(SUPPORTED);
  });

  it("offers no language outside the supported set", () => {
    for (const lang of TEST_LANGUAGES) {
      expect(SUPPORTED).toContain(lang.code);
    }
  });

  it("gives every language a real passage, a native name and a token mode", () => {
    for (const lang of TEST_LANGUAGES) {
      expect(lang.passage.trim().length).toBeGreaterThan(40);
      expect(lang.nativeName.trim()).not.toBe("");
      expect(lang.name.trim()).not.toBe("");
      expect(["words", "characters"]).toContain(lang.tokenMode);
    }
  });

  it("uses word scoring for every supported language, all of which are space-delimited", () => {
    // Character scoring exists and is tested directly above; it has no user in the supported set.
    // If a language written without spaces is ever added, this expectation is what should fail.
    for (const lang of TEST_LANGUAGES) {
      expect(`${lang.code}:${lang.tokenMode}`).toBe(`${lang.code}:words`);
    }
  });

  it("gives every passage enough tokens for a stable score", () => {
    // A passage too short makes one misheard word swing the percentage wildly.
    for (const lang of TEST_LANGUAGES) {
      expect(tokenize(lang.passage, lang.tokenMode).length).toBeGreaterThanOrEqual(25);
    }
  });

  it("scores each language's own passage as perfect against itself", () => {
    // Guards the tokenizer against a script it mishandles: if a language's punctuation or spacing
    // defeated the tokenizer, a perfect transcript would not score 100% and every user of that
    // language would be told their setup is broken.
    for (const lang of TEST_LANGUAGES) {
      const result = scoreTranscription(lang.passage, lang.passage, lang.tokenMode);
      expect(`${lang.code}:${result.accuracy}`).toBe(`${lang.code}:1`);
    }
  });

  it("falls back to English for an unknown code instead of returning nothing", () => {
    expect(languageByCode("xx").code).toBe("en");
  });

  it("matches a browser language on its primary subtag, so regional variants still match", () => {
    expect(preferredLanguage(["de-AT"]).code).toBe("de");
    expect(preferredLanguage(["fr-CH", "en"]).code).toBe("fr");
    expect(preferredLanguage(["es-MX"]).code).toBe("es");
    expect(preferredLanguage(["da-DK"]).code).toBe("da");
    expect(preferredLanguage([]).code).toBe("en");
  });

  it("offers English to a browser set to a language we do not support", () => {
    // Not a silent nearest-match: there is no passage in Dutch or Portuguese, and pretending
    // otherwise would score a Dutch speaker against English words.
    expect(preferredLanguage(["nl-NL"]).code).toBe("en");
    expect(preferredLanguage(["pt-BR"]).code).toBe("en");
    expect(preferredLanguage(["zh-CN"]).code).toBe("en");
  });
});
