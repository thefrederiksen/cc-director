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
 * The same end-of-turn rule as detectEndPhrase, but for a CONFIGURABLE phrase (the Car Mode end-word
 * test page and, later, the Car Mode settings tab where the owner chooses his own sign-off phrase). The
 * phrase must be the trailing token sequence of the normalized transcript - the whole transcript IS the
 * phrase, or it ends with a word boundary followed by the phrase - so it never fires mid-sentence, and
 * it is stripped to yield the command. A blank phrase never matches (so an empty setting cannot end
 * every turn). Both the transcript and the phrase pass through the one normalizer.
 */
export function detectPhraseAtEnd(rawTranscript: string, phrase: string): EndPhraseResult {
  const norm = normalizeTranscript(phrase);
  if (norm.length === 0) return { ended: false, command: "" };
  const text = normalizeTranscript(rawTranscript);
  if (text.length === 0) return { ended: false, command: "" };

  const endsWithPhrase = text === norm || text.endsWith(" " + norm);
  if (!endsWithPhrase) return { ended: false, command: "" };

  const command = text.slice(0, text.length - norm.length).trim();
  return { ended: true, command };
}

/** What the turn-taking machine should do with a live control transcript, given the current phase. */
export type ControlAction = "end" | "interrupt" | "none";

/** The Car Mode phases the control-word handler branches on. */
export type ControlPhase = "listening" | "thinking" | "speaking";

/**
 * The single turn-taking decision, pure and testable: given the current phase and the live control-word
 * transcript, decide whether the owner ended his turn, interrupted the assistant, or did neither. Only
 * Listening can END (on "over and out"), only Speaking can INTERRUPT (on "stop"/"wait"/"shut up"), and
 * Thinking ignores control words (nothing is playing to interrupt, and the turn is already committed).
 * useCarMode() calls this from its recognizer callback so the state machine's language rules live in one
 * unit-tested place instead of inline in the imperative hook.
 */
export function decideControlAction(phase: ControlPhase, rawTranscript: string): ControlAction {
  if (phase === "listening") return detectEndPhrase(rawTranscript).ended ? "end" : "none";
  if (phase === "speaking") return detectInterrupt(rawTranscript) ? "interrupt" : "none";
  return "none";
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
