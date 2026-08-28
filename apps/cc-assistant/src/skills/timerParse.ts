// Understanding "set a timer for ten minutes" without asking a model.
//
// This is the fast path. Timers are the thing these devices are actually used for, and they need no
// intelligence at all: a number, a unit, and a clock. Parsed here it answers in a few milliseconds,
// works with no network, and cannot be talked out of it by a model having a bad day. Sending it to a
// language model would cost half a second and add the possibility of a wrong answer to a question
// that has exactly one right one.

import { normaliseForMatching } from "../wakeWord/wakeWordMatcher";

export type TimerIntent =
  | { kind: "start"; seconds: number; spoken: string }
  | { kind: "cancel" }
  | { kind: "query" };

const NUMBER_WORDS = new Map<string, number>([
  ["a", 1], ["an", 1], ["one", 1], ["two", 2], ["three", 3], ["four", 4], ["five", 5],
  ["six", 6], ["seven", 7], ["eight", 8], ["nine", 9], ["ten", 10], ["eleven", 11],
  ["twelve", 12], ["thirteen", 13], ["fourteen", 14], ["fifteen", 15], ["sixteen", 16],
  ["seventeen", 17], ["eighteen", 18], ["nineteen", 19], ["twenty", 20], ["thirty", 30],
  ["forty", 40], ["fifty", 50], ["sixty", 60], ["ninety", 90],
]);

const UNITS = new Map<string, number>([
  ["second", 1], ["seconds", 1], ["sec", 1], ["secs", 1],
  ["minute", 60], ["minutes", 60], ["min", 60], ["mins", 60],
  ["hour", 3600], ["hours", 3600], ["hr", 3600], ["hrs", 3600],
]);

/** Say a duration the way a person would say it out loud. */
export function spokenDuration(totalSeconds: number): string {
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  const parts: string[] = [];
  if (hours > 0) {
    parts.push(`${hours} ${hours === 1 ? "hour" : "hours"}`);
  }
  if (minutes > 0) {
    parts.push(`${minutes} ${minutes === 1 ? "minute" : "minutes"}`);
  }
  if (seconds > 0 || parts.length === 0) {
    parts.push(`${seconds} ${seconds === 1 ? "second" : "seconds"}`);
  }
  if (parts.length === 1) {
    return parts[0];
  }
  return `${parts.slice(0, -1).join(", ")} and ${parts[parts.length - 1]}`;
}

/** A clock face for the screen. */
export function clockFace(totalSeconds: number): string {
  const safe = Math.max(0, totalSeconds);
  const hours = Math.floor(safe / 3600);
  const minutes = Math.floor((safe % 3600) / 60);
  const seconds = safe % 60;
  const pad = (n: number) => String(n).padStart(2, "0");
  return hours > 0 ? `${hours}:${pad(minutes)}:${pad(seconds)}` : `${minutes}:${pad(seconds)}`;
}

/**
 * Read a spoken command as a timer instruction, or return null when it is not about timers.
 *
 * Returning null is the important half: everything this does not recognise goes on to the model, so
 * being greedy here would swallow ordinary questions. It only claims a sentence that mentions a timer
 * or an alarm.
 */
export function parseTimer(command: string): TimerIntent | null {
  const text = normaliseForMatching(command);
  if (text.length === 0) {
    return null;
  }

  const mentionsTimer = /\b(timer|alarm)\b/.test(text);

  if (mentionsTimer && /\b(cancel|stop|clear|delete|remove|forget|off)\b/.test(text)) {
    return { kind: "cancel" };
  }

  // "how long left", "how much time is left on the timer", "how long on the timer"
  if (mentionsTimer && /\b(how long|how much|left|remaining|check)\b/.test(text)) {
    return { kind: "query" };
  }
  if (/\bhow (long|much)\b/.test(text) && /\b(left|remaining)\b/.test(text)) {
    return { kind: "query" };
  }

  if (!mentionsTimer) {
    return null;
  }

  const seconds = readDuration(text);
  if (seconds === null || seconds <= 0) {
    return null;
  }
  // A day is far past anything a kitchen needs and well into "it misheard something".
  if (seconds > 24 * 3600) {
    return null;
  }
  return { kind: "start", seconds, spoken: spokenDuration(seconds) };
}

/**
 * Total the durations in a sentence.
 *
 * Adds every number-and-unit pair it finds, so "one hour and thirty minutes" works and so does
 * "ninety seconds". A number with no unit after it is ignored rather than guessed at.
 */
function readDuration(text: string): number | null {
  const words = text.split(" ");
  let total = 0;
  let found = false;
  let pending: number | null = null;

  for (const word of words) {
    const asDigits = /^\d+$/.test(word) ? Number(word) : null;
    const asWord = NUMBER_WORDS.get(word);
    const number = asDigits ?? asWord ?? null;

    if (number !== null) {
      // "twenty five minutes" arrives as two numbers and means twenty-five. But "a 3 minute timer"
      // also arrives as two numbers, and adding those gives four. Only a genuine tens-and-units pair
      // combines; anything else means the later number is the one meant.
      const carried: number | null = pending;
      const isTensAndUnits: boolean =
        carried !== null && carried >= 20 && carried <= 90 && carried % 10 === 0 && number >= 1 && number <= 9;
      pending = isTensAndUnits && carried !== null ? carried + number : number;
      continue;
    }

    const unit = UNITS.get(word);
    if (unit !== undefined) {
      total += (pending ?? 1) * unit;
      pending = null;
      found = true;
    } else if (word !== "and") {
      pending = null;
    }
  }

  return found ? total : null;
}
