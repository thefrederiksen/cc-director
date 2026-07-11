import { describe, expect, it } from "vitest";
import {
  addMistranscriptionTerm,
  addMistranscriptionVariant,
  addVocabularyWord,
} from "./dictionaryEdits";
import type { Dictionary } from "./dictionaryClient";

// Regression tests for the Dictionary duplicate-swallowing bug (issue #1255): before the fix, adding
// an entry that already existed silently no-oped. These lock the explicit outcomes the page relies on
// to announce duplicates and to keep an unchanged Dictionary reference when nothing was added.

function base(): Dictionary {
  return {
    vocabulary: ["devthrottle", "gateway"],
    commonMistranscriptions: { throttle: ["throddle"], director: [] },
    profiles: {},
  };
}

describe("addVocabularyWord", () => {
  it("adds a new, trimmed word", () => {
    const dict = base();
    const r = addVocabularyWord(dict, "  cockpit  ");
    expect(r.status).toBe("added");
    if (r.status !== "added") throw new Error("expected added");
    expect(r.word).toBe("cockpit");
    expect(r.dict.vocabulary).toEqual(["devthrottle", "gateway", "cockpit"]);
    // The input Dictionary is not mutated.
    expect(dict.vocabulary).toEqual(["devthrottle", "gateway"]);
  });

  it("reports a duplicate instead of silently swallowing it", () => {
    const r = addVocabularyWord(base(), "gateway");
    expect(r).toEqual({ status: "duplicate", word: "gateway" });
  });

  it("treats whitespace-only input as empty", () => {
    expect(addVocabularyWord(base(), "   ").status).toBe("empty");
  });
});

describe("addMistranscriptionTerm", () => {
  it("adds a new term with an empty variant list", () => {
    const r = addMistranscriptionTerm(base(), "wingman");
    expect(r.status).toBe("added");
    if (r.status !== "added") throw new Error("expected added");
    expect(r.dict.commonMistranscriptions.wingman).toEqual([]);
  });

  it("reports a duplicate term (including one whose variant list is empty)", () => {
    // "director" already exists with an empty variant list - this is exactly the case that used to be
    // indistinguishable from a fresh add.
    expect(addMistranscriptionTerm(base(), "director")).toEqual({ status: "duplicate", word: "director" });
    expect(addMistranscriptionTerm(base(), "throttle")).toEqual({ status: "duplicate", word: "throttle" });
  });

  it("treats empty input as empty", () => {
    expect(addMistranscriptionTerm(base(), "").status).toBe("empty");
  });
});

describe("addMistranscriptionVariant", () => {
  it("adds a new, trimmed variant under an existing term", () => {
    const r = addMistranscriptionVariant(base(), "director", "  dyrector  ");
    expect(r.status).toBe("added");
    if (r.status !== "added") throw new Error("expected added");
    expect(r.dict.commonMistranscriptions.director).toEqual(["dyrector"]);
  });

  it("reports a duplicate variant instead of swallowing it", () => {
    expect(addMistranscriptionVariant(base(), "throttle", "throddle")).toEqual({
      status: "duplicate",
      term: "throttle",
      variant: "throddle",
    });
  });

  it("reports a missing term rather than crashing", () => {
    expect(addMistranscriptionVariant(base(), "nope", "x")).toEqual({ status: "missing-term", term: "nope" });
  });

  it("treats whitespace-only input as empty", () => {
    expect(addMistranscriptionVariant(base(), "throttle", "  ").status).toBe("empty");
  });
});
