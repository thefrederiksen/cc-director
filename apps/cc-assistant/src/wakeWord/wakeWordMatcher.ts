// Matching a spoken wake word inside a running transcript.
//
// CC Assistant lets every person choose their own wake word, so there is no fixed model trained on
// one phrase. Instead speech recognition runs continuously and this module decides whether the
// chosen word appeared in what was heard, and what the person said after it.
//
// The whole file is pure: no browser objects, no timers, no state. That is deliberate, because this
// is the one piece whose behaviour has to be provable without a microphone in the room.

/** The outcome of looking for a wake word in one piece of transcript. */
export interface WakeWordMatch {
  /** The transcript, normalised, exactly as it was compared. Shown in the log so a miss is explainable. */
  readonly normalisedTranscript: string;
  /** Position of the first word of the wake word, counted in words, starting at zero. */
  readonly wordIndex: number;
  /** Everything the person said after the wake word in the same breath. Empty when they said only the word. */
  readonly command: string;
}

/**
 * Lower case, strip anything that is not a letter, a digit or a space, and collapse runs of spaces.
 *
 * Speech recognition returns capitalisation and punctuation that varies between one interim result
 * and the next, so comparing raw text produces matches that come and go while a person is still
 * speaking. Normalising both sides removes that entirely.
 */
export function normaliseForMatching(text: string): string {
  return text
    .toLowerCase()
    .replace(/[^\p{Letter}\p{Number}]+/gu, " ")
    .trim()
    .replace(/\s+/g, " ");
}

/** Split normalised text into words. Empty text yields an empty list rather than one empty word. */
function toWords(normalisedText: string): string[] {
  return normalisedText.length === 0 ? [] : normalisedText.split(" ");
}

/**
 * Look for the wake word in a transcript.
 *
 * Matching is on whole words, so a wake word of "ada" does not fire on the word "adapter", and a
 * wake word of more than one word ("hey wilson") has to appear as those words in that order.
 *
 * Returns the LAST occurrence rather than the first. Speech recognition delivers a growing transcript
 * for one long utterance, so when somebody says the wake word twice the second one is the one they
 * meant, and the command they want is the text after it.
 *
 * Returns null when the wake word is not present, and also when the wake word itself is empty, which
 * is treated as "no wake word configured" rather than "matches everything".
 */
export function matchWakeWord(transcript: string, wakeWord: string): WakeWordMatch | null {
  const normalisedTranscript = normaliseForMatching(transcript);
  const normalisedWakeWord = normaliseForMatching(wakeWord);

  const wakeWordWords = toWords(normalisedWakeWord);
  if (wakeWordWords.length === 0) {
    return null;
  }

  const transcriptWords = toWords(normalisedTranscript);
  if (transcriptWords.length < wakeWordWords.length) {
    return null;
  }

  const lastPossibleStart = transcriptWords.length - wakeWordWords.length;
  for (let start = lastPossibleStart; start >= 0; start -= 1) {
    let allWordsMatch = true;
    for (let offset = 0; offset < wakeWordWords.length; offset += 1) {
      if (transcriptWords[start + offset] !== wakeWordWords[offset]) {
        allWordsMatch = false;
        break;
      }
    }
    if (allWordsMatch) {
      return {
        normalisedTranscript,
        wordIndex: start,
        command: transcriptWords.slice(start + wakeWordWords.length).join(" "),
      };
    }
  }

  return null;
}

/**
 * Why a chosen wake word is a poor one, or null when it is fine.
 *
 * This is advice shown next to the input, never a refusal. A person is allowed to pick a bad wake
 * word; they just deserve to be told before they spend an evening wondering why it keeps firing.
 */
export function describeWakeWordWeakness(wakeWord: string): string | null {
  const normalised = normaliseForMatching(wakeWord);
  if (normalised.length === 0) {
    return "Choose a wake word before you start listening.";
  }
  if (normalised.replace(/\s/g, "").length < 4) {
    return "Very short wake words are triggered by ordinary speech. Four letters or more works better.";
  }
  if (COMMON_ENGLISH_WORDS.has(normalised)) {
    return "This is an everyday word, so it will fire during normal conversation. An unusual name works better.";
  }
  return null;
}

// Words common enough in kitchen and living room conversation that using one as a wake word means
// constant false starts. Not a spell checker, just the traps people reach for first.
const COMMON_ENGLISH_WORDS = new Set([
  "assistant",
  "computer",
  "companion",
  "hello",
  "help",
  "hey",
  "house",
  "listen",
  "music",
  "okay",
  "play",
  "stop",
  "timer",
  "yes",
]);
