// Car Mode control-phrase detection (Car Mode mission, decision 1). This is the walkie-talkie
// discipline in pure, testable code: the owner ends a turn ONLY by saying the complete phrase
// "over and out" as the last thing he says, and can cut the assistant off at any time with "stop"
// (also "wait", "shut up"). Nothing here touches audio - it is given the running speech-recognition
// transcript (interim or final) and decides two things: has the turn ended, and (during playback)
// did an interrupt word arrive.
//
// Why pure functions: the browser speech recognizer and the React hook are hard to unit-test, so the
// LANGUAGE decisions live here where they can be exercised exhaustively (issue: transcription
// integrity and no-fallback both demand the end-word logic be exactly right, never a guess).

/** The complete end-of-turn phrase. Required in full and only as the last thing said (decision 1):
 *  plain "over" or plain "out" alone never triggers. */
export const END_PHRASE = "over and out";

/** The interrupt words that cut the assistant off instantly during playback (decision 1). */
export const INTERRUPT_WORDS: readonly string[] = ["stop", "wait", "shut up"];

/**
 * Lowercase, strip punctuation to spaces, and collapse whitespace. The recognizer emits capitalization
 * and stray punctuation ("Over and out.") that must not defeat an exact-phrase match, so both the
 * transcript and the phrases are normalized through this one function before comparison.
 */
export function normalizeTranscript(raw: string): string {
  return raw
    .toLowerCase()
    .replace(/[^a-z0-9\s]/g, " ")
    .replace(/\s+/g, " ")
    .trim();
}

/** The result of testing a live transcript for the end-of-turn phrase. */
export interface EndPhraseResult {
  /** True when "over and out" was said as the LAST thing in the transcript. */
  ended: boolean;
  /** The command with the trailing end phrase removed, ready for the brain. Empty when the owner said
   *  nothing but the phrase, or when the turn has not ended. */
  command: string;
}

/**
 * Decide whether the owner has finished his turn. He finishes ONLY by saying the complete phrase
 * "over and out" as the very last words - so we match the phrase at the END of the normalized
 * transcript, never anywhere in the middle (saying "we talked about the stopover and outages" must
 * not end the turn), and never on a partial ("over" or "out" alone). When it has ended, the phrase is
 * stripped and the remaining words are the command the brain receives (decision 1: strip the phrase
 * before the command text reaches the brain).
 */
export function detectEndPhrase(rawTranscript: string): EndPhraseResult {
  const text = normalizeTranscript(rawTranscript);
  if (text.length === 0) return { ended: false, command: "" };

  // The phrase must be the trailing token sequence: either the whole transcript IS the phrase, or the
  // transcript ends with a word boundary followed by the phrase.
  const endsWithPhrase = text === END_PHRASE || text.endsWith(" " + END_PHRASE);
  if (!endsWithPhrase) return { ended: false, command: "" };

  const command = text.slice(0, text.length - END_PHRASE.length).trim();
  return { ended: true, command };
}

/**
 * Decide whether the transcript carries an interrupt word ("stop"/"wait"/"shut up"). Used ONLY while
 * the assistant is speaking, so the owner can cut it off instantly. Matches a WHOLE word/phrase (so
 * "stopwatch" or "waiting" does not fire) anywhere in the transcript, because an interrupt is urgent
 * and need not be the last thing said. "shut up" is matched as an adjacent word pair.
 */
export function detectInterrupt(rawTranscript: string): boolean {
  const text = normalizeTranscript(rawTranscript);
  if (text.length === 0) return false;
  const words = text.split(" ");
  for (const phrase of INTERRUPT_WORDS) {
    if (phrase.includes(" ")) {
      // Multi-word interrupt ("shut up"): look for the adjacent pair in the word list.
      const parts = phrase.split(" ");
      for (let i = 0; i + parts.length <= words.length; i++) {
        if (parts.every((p, j) => words[i + j] === p)) return true;
      }
    } else if (words.includes(phrase)) {
      return true;
    }
  }
  return false;
}
