// The parts of timers that have a right answer, kept away from React and the microphone so they can
// be tested.
//
// Three jobs live here: recognising a shout to silence a ringing alarm, matching a spoken name
// against the timers that exist, and writing the sentence Wilson says afterwards.
//
// THE SENTENCE IS WRITTEN HERE, NOT BY THE MODEL. The model's job is to understand what was asked;
// what actually happened is known only by the device. Letting the model narrate its own tool calls is
// how you end up being told a timer was set when none was, which happened once today already.

import { normaliseForMatching } from "../wakeWord/wakeWordMatcher";
import { similarity } from "../assistant/echoGuard";
import { spokenDuration } from "./timerParse";

export interface StoredTimer {
  readonly id: number;
  /** What it is called, or null for a plain unnamed timer. */
  readonly name: string | null;
  readonly totalSeconds: number;
  readonly endsAt: number;
  readonly ringing: boolean;
}

// Said at a beeping alarm. These work WITHOUT the wake word, because shouting "Wilson" over an alarm
// is absurd and because there is nothing else these words could mean while one is going off.
const SILENCE_PHRASES = [
  "stop", "stop it", "stop the timer", "stop the timers", "stop the alarm", "stop the ringing",
  "shut up", "be quiet", "quiet", "enough", "that is enough", "thats enough", "ok", "okay",
  "alright", "all right", "turn it off", "off", "cancel",
];

/**
 * Is this someone telling a ringing alarm to be quiet?
 *
 * Only ever consulted while something is actually ringing, so it can afford to be generous. The same
 * words at any other time mean something else entirely and never reach this.
 */
export function isSilenceCommand(text: string): boolean {
  const normalised = normaliseForMatching(text);
  if (normalised.length === 0) {
    return false;
  }
  if (SILENCE_PHRASES.includes(normalised)) {
    return true;
  }
  // A recogniser hears "shut up" as "shutup" and "okay" as "ok" often enough to matter.
  return SILENCE_PHRASES.some((phrase) => similarity(normalised, phrase) >= 0.9);
}

export interface NameMatch {
  readonly matched: StoredTimer[];
  /** Set when nothing matched, or when the request was too vague to act on safely. */
  readonly problem: "none" | "ambiguous" | null;
}

/**
 * Find the timers a spoken name refers to.
 *
 * "the pasta one" has to find the timer called pasta, so this looks for the name anywhere in what was
 * said as well as comparing the two directly. When a bare "the timer" is said and several are running
 * it deliberately matches nothing: guessing which of three timers to cancel is worse than asking.
 */
export function matchTimersByName(timers: readonly StoredTimer[], spokenName: string | null): NameMatch {
  const live = timers.filter((t) => !t.ringing);
  const pool = live.length > 0 ? live : timers;

  if (pool.length === 0) {
    return { matched: [], problem: "none" };
  }

  const wanted = spokenName === null ? "" : normaliseForMatching(spokenName);
  const generic = wanted === "" || ["timer", "the timer", "it", "that", "this"].includes(wanted);

  if (generic) {
    // One running timer and "the timer" is unambiguous. More than one and it is not.
    return pool.length === 1 ? { matched: [pool[0]], problem: null } : { matched: [], problem: "ambiguous" };
  }

  const named = pool.filter((t) => t.name !== null);
  const hits = named.filter((t) => {
    const name = normaliseForMatching(t.name!);
    return name === wanted || wanted.includes(name) || name.includes(wanted) || similarity(name, wanted) >= 0.75;
  });

  if (hits.length > 0) {
    return { matched: hits, problem: null };
  }
  // Named nothing that exists. If exactly one timer is running that is almost certainly the one meant.
  return pool.length === 1 ? { matched: [pool[0]], problem: null } : { matched: [], problem: "none" };
}

/** How a timer is referred to out loud. */
export function callIt(timer: StoredTimer): string {
  return timer.name === null ? `${spokenDuration(timer.totalSeconds)} timer` : `${timer.name} timer`;
}

/** Join a list the way a person says it. */
export function joinSpoken(items: string[]): string {
  if (items.length === 0) {
    return "";
  }
  if (items.length === 1) {
    return items[0];
  }
  return `${items.slice(0, -1).join(", ")} and ${items[items.length - 1]}`;
}

export function remainingSeconds(timer: StoredTimer, now: number): number {
  return Math.max(0, Math.round((timer.endsAt - now) / 1000));
}

// ---------------------------------------------------------------------------
// The sentences. One function per outcome, each built from what actually happened.
// ---------------------------------------------------------------------------

export function sayStarted(timer: StoredTimer): string {
  return timer.name === null
    ? `${spokenDuration(timer.totalSeconds)}, starting now.`
    : `${spokenDuration(timer.totalSeconds)} for the ${timer.name}, starting now.`;
}

export function sayStopped(stopped: readonly StoredTimer[]): string {
  if (stopped.length === 0) {
    return "There was no timer to stop.";
  }
  // "the" on each one, or two timers read as "the pasta timer and eggs timer".
  return `Stopped ${joinSpoken(stopped.map((t) => `the ${callIt(t)}`))}.`;
}

export function sayStoppedAll(count: number): string {
  if (count === 0) {
    return "There are no timers running.";
  }
  return count === 1 ? "Stopped it." : `Stopped all ${count} timers.`;
}

export function sayAmbiguous(timers: readonly StoredTimer[]): string {
  return `There are ${timers.length} timers running: ${joinSpoken(timers.map((t) => `the ${callIt(t)}`))}. Which one?`;
}

export function sayNotFound(spokenName: string | null): string {
  return spokenName === null || spokenName.trim().length === 0
    ? "There is no timer running."
    : `There is no ${spokenName} timer running.`;
}

export function sayList(timers: readonly StoredTimer[], now: number): string {
  const ringing = timers.filter((t) => t.ringing);
  const live = timers.filter((t) => !t.ringing);

  if (live.length === 0 && ringing.length === 0) {
    return "There are no timers running.";
  }
  if (live.length === 0) {
    return ringing.length === 1 ? "One timer is going off now." : `${ringing.length} timers are going off now.`;
  }

  const parts = live.map((t) => {
    const left = spokenDuration(remainingSeconds(t, now));
    return t.name === null ? `${left} left` : `${left} left on the ${t.name}`;
  });
  const sentence = joinSpoken(parts);
  return ringing.length > 0 ? `${sentence}. And one is going off now.` : `${sentence}.`;
}
