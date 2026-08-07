// Pure edit operations for the dictation Dictionary editor (issue #1255). The Cockpit Dictionary page
// used to fold "the word is already there" into the same silent no-op as "the box is empty" - it
// cleared the input and did nothing, so a person who re-typed an existing word got no sign their edit
// was rejected. These functions make the outcome explicit: an add either produces a new Dictionary, or
// reports exactly why it did not (empty input, or a duplicate the page must announce). The page reads
// the status and shows the duplicate case instead of swallowing it.
//
// Every function is a pure transform over the immutable Dictionary shape (it returns a fresh object and
// never mutates its argument), so the page can drive React state from the result and these rules can be
// unit-tested without a browser.
import type { Dictionary } from "./dictionaryClient";

/** Outcome of trying to add a single string entry (a vocabulary word, or a term to correct). */
export type AddEntryResult =
  // The trimmed input was empty - there is nothing to add, and nothing to announce.
  | { status: "empty" }
  // The entry already exists - the page must say so rather than silently clearing the input.
  | { status: "duplicate"; word: string }
  // The entry was added - `dict` is the new Dictionary the page should adopt.
  | { status: "added"; word: string; dict: Dictionary };

/** Add a word to the canonical vocabulary the cleanup pass corrects towards on the finished
 *  transcript - it is never sent to the transcriber (issue 2481). Trims first; rejects empty
 *  and duplicates. */
export function addVocabularyWord(dict: Dictionary, raw: string): AddEntryResult {
  const word = raw.trim();
  if (word.length === 0) return { status: "empty" };
  if (dict.vocabulary.includes(word)) return { status: "duplicate", word };
  return { status: "added", word, dict: { ...dict, vocabulary: [...dict.vocabulary, word] } };
}

/** Add a correct-term key to the common-mistranscriptions map (with an empty variant list). Trims
 *  first; rejects empty and a term already present in the map. */
export function addMistranscriptionTerm(dict: Dictionary, raw: string): AddEntryResult {
  const word = raw.trim();
  if (word.length === 0) return { status: "empty" };
  if (Object.prototype.hasOwnProperty.call(dict.commonMistranscriptions, word)) {
    return { status: "duplicate", word };
  }
  return {
    status: "added",
    word,
    dict: { ...dict, commonMistranscriptions: { ...dict.commonMistranscriptions, [word]: [] } },
  };
}

/** Outcome of trying to add a wrong-spelling variant under an existing correct term. */
export type AddVariantResult =
  // The trimmed input was empty - nothing to add.
  | { status: "empty" }
  // The term is not in the map (it was removed under the page's feet) - the page cannot add to it.
  | { status: "missing-term"; term: string }
  // The variant is already listed for this term - the page must say so, not silently clear the input.
  | { status: "duplicate"; term: string; variant: string }
  // The variant was added - `dict` is the new Dictionary the page should adopt.
  | { status: "added"; term: string; variant: string; dict: Dictionary };

/** Add a wrong-spelling variant under a correct term. Trims first; rejects empty, a missing term, and
 *  a duplicate variant already listed for that term. */
export function addMistranscriptionVariant(dict: Dictionary, term: string, raw: string): AddVariantResult {
  const variant = raw.trim();
  if (variant.length === 0) return { status: "empty" };
  const variants = dict.commonMistranscriptions[term];
  if (variants === undefined) return { status: "missing-term", term };
  if (variants.includes(variant)) return { status: "duplicate", term, variant };
  return {
    status: "added",
    term,
    variant,
    dict: {
      ...dict,
      commonMistranscriptions: { ...dict.commonMistranscriptions, [term]: [...variants, variant] },
    },
  };
}
