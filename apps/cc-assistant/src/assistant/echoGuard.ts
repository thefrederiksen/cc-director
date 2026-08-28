// Stopping it from answering itself.
//
// On 28 August Soren asked one question, "can you make me a cup of coffee", and got four turns:
//
//   you: can you make me a cup of coffee   it: I'm sorry, I can't make coffee.
//   you: I'm sorry I can't make coffee     it: No problem.
//   you: no problem                        it: Alright.
//   you: all right                         it: Got it.
//
// Its own voice went out of the speaker, came back through the microphone, was transcribed like
// anything else, and inside the follow-up window it was treated as a new command. It would have gone
// on for as long as the battery lasted.
//
// Two guards, because either alone leaks.
//
// TIME is the primary one: nothing said between the moment it starts speaking and a short drain after
// it stops is a command. Transcription lags the audio, so the drain matters as much as the speaking.
//
// TEXT is the backstop, for anything that arrives late: a transcript that closely resembles something
// just said out loud is an echo, not a question. It has to be fuzzy, because the recogniser hears its
// own "Alright." as "all right" - the same sound, different letters, and an exact comparison misses it.

import { normaliseForMatching } from "../wakeWord/wakeWordMatcher";

/** How long after it stops talking before the room counts as quiet again. */
export const DRAIN_MS = 1500;

/** Above this, a transcript is treated as the assistant's own voice coming back. */
export const ECHO_SIMILARITY = 0.72;

export interface EchoDecision {
  readonly isEcho: boolean;
  /** Why, in plain words, so a suppressed turn is visible rather than silently vanishing. */
  readonly reason: string | null;
}

/**
 * Letters only, no spaces.
 *
 * "Alright." and "all right" are the same sound and differ by a space, so anything that compares
 * words misses them. Collapsing to letters makes them one edit apart instead of nothing in common.
 */
function collapse(text: string): string {
  return normaliseForMatching(text).replace(/\s+/g, "");
}

/** How alike two pieces of text are, from 0 to 1, by edit distance over the longer one. */
export function similarity(a: string, b: string): number {
  const left = collapse(a);
  const right = collapse(b);
  if (left.length === 0 && right.length === 0) {
    return 1;
  }
  if (left.length === 0 || right.length === 0) {
    return 0;
  }
  const distance = editDistance(left, right);
  return 1 - distance / Math.max(left.length, right.length);
}

function editDistance(a: string, b: string): number {
  let previous = Array.from({ length: b.length + 1 }, (_, i) => i);
  for (let i = 1; i <= a.length; i += 1) {
    const current = [i];
    for (let j = 1; j <= b.length; j += 1) {
      current[j] = a[i - 1] === b[j - 1]
        ? previous[j - 1]
        : 1 + Math.min(previous[j - 1], previous[j], current[j - 1]);
    }
    previous = current;
  }
  return previous[b.length];
}

export interface EchoContext {
  /** True while the assistant's own voice is coming out of the speaker. */
  readonly speaking: boolean;
  /** When it last stopped speaking, or null if it has not spoken yet. */
  readonly lastSpokeEndedAt: number | null;
  /** The last few things it said out loud. */
  readonly recentlySpoken: readonly string[];
  readonly now: number;
}

/**
 * Should this transcript be ignored because the assistant said it?
 *
 * Deliberately errs towards suppressing. Losing a question you have to repeat is a small annoyance;
 * an assistant talking to itself in an endless loop is unusable, and it is what actually happened.
 */
export function judgeEcho(heard: string, context: EchoContext): EchoDecision {
  if (normaliseForMatching(heard).length === 0) {
    return { isEcho: true, reason: "Nothing was said." };
  }

  if (context.speaking) {
    return { isEcho: true, reason: "Heard while it was talking." };
  }

  if (context.lastSpokeEndedAt !== null && context.now - context.lastSpokeEndedAt < DRAIN_MS) {
    const ago = context.now - context.lastSpokeEndedAt;
    return { isEcho: true, reason: `Heard ${ago} ms after it stopped talking.` };
  }

  for (const spoken of context.recentlySpoken) {
    const score = similarity(heard, spoken);
    if (score >= ECHO_SIMILARITY) {
      return {
        isEcho: true,
        reason: `That is what it just said (${Math.round(score * 100)}% alike).`,
      };
    }
  }

  return { isEcho: false, reason: null };
}
