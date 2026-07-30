// SOMETHING THE BROWSER IS ABOUT TO SAY OUT LOUD, WITH ITS LANGUAGE ALREADY DECIDED (issue #1031).
//
// This is the browser expression of the SAME contract the Gateway and the desktop use - one contract, two
// runtimes, not two designs. TypeScript cannot share a C# type, so the property is rebuilt here rather than
// imported: an utterance cannot be made without a language, one factory makes them, and sinks take only that.
//
// WHY IT EXISTS. One notice in this product is spoken by the BROWSER rather than by the Gateway: the refusal
// when a menu owns a session's screen. That speech has to be local by design - it is the product saying it will
// not act, so it must cost nothing, need no network, and never be the thing that fails. It was handed a bare
// string, so it set no language, so a correctly-translated French refusal was pronounced by the device's default
// English voice. The audio played. Nothing errored. That is the failure shape this whole mission is about.
//
// IT CARRIES A LANGUAGE AND NEVER A MODEL OR AN ENGINE. A language selects the voice; a language selecting an
// engine is what got the feature reverted (devthrottle_internal#547). There is nothing on this type for a
// future change to branch an engine on - and the local engine is the device's own, which is not ours to choose.

/**
 * An utterance: words, and the language they are in. Both always present.
 *
 * `decided` is a marker, and it is load-bearing: a plain `{ text, language }` object literal does NOT satisfy
 * this type, so a sink cannot be handed something merely utterance-SHAPED that skipped the factory. It is
 * weaker than the C# side, where the constructor is private and there is no way around it - a determined caller
 * could write the marker by hand - but it turns the accident into something you have to do on purpose, and the
 * accident is what happened.
 */
export interface SpokenUtterance {
  /** The words to say. Spoken CONTENT, so it carries whatever accents its language needs - and it is therefore
   *  never logged; log its length instead (docs/MISSION-multilingual-RULINGS.md, guard 1). */
  readonly text: string;
  /** The language code the Gateway decided for this account: "en", "fr", "es". Never guessed here - the browser
   *  cannot know the account's language, and a guess here plus a guess in another shell is two different
   *  guesses. */
  readonly language: string;
  /** Set only by `utteranceFor`. See the note on this interface. */
  readonly decided: true;
}

/**
 * Build an utterance. The only way one is meant to exist.
 *
 * Both arguments are required and neither is optional, which is the mechanism: a caller who does not know the
 * language cannot get past this line. The Gateway sends the language beside the words on every response that
 * carries something to say, so a caller always has one to give.
 *
 * THROWS on a missing language rather than defaulting to English. A quiet English default is the reported bug
 * itself - the setting appears to do nothing - and every caller of this is on a path that already shows the
 * same notice on screen, so a throw is visible in the error channel instead of silently mispronouncing.
 */
export function utteranceFor(language: string, text: string): SpokenUtterance {
  const spokenLanguage = (language ?? "").trim();
  if (spokenLanguage.length === 0) {
    throw new Error(
      "A spoken utterance needs a language. The Gateway sends one beside the words; an empty language here "
        + "means it was dropped between the response and this call, and speaking anyway would pronounce the "
        + "text with the device's default voice.",
    );
  }
  if (typeof text !== "string" || text.trim().length === 0) {
    throw new Error("A spoken utterance needs words.");
  }
  return { text, language: spokenLanguage, decided: true };
}
