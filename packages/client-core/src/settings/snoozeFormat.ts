// Turning a snooze length in minutes into the words a menu row shows. Shared, because the Cockpit
// settings list and the phone's snooze menu must name the same length the same way - "4 hours" on one
// surface and "240 minutes" on another would read as two different settings.

const MINUTES_PER_HOUR = 60;
const MINUTES_PER_DAY = 24 * 60;

// The shortest and longest snooze the Gateway will store. Mirrors SnoozeDefaultConfig.MinMinutes and
// MaxMinutes: zero would defeat the always-comes-back guarantee, and seven days is the ceiling that keeps
// an accidental huge value out. Checked here too so the editor can refuse a bad length before the round
// trip - the Gateway remains the one that enforces it.
export const SNOOZE_MIN_MINUTES = 1;
export const SNOOZE_MAX_MINUTES = 7 * 24 * 60;

// The units a snooze length can be entered in. A number plus one of these beats free text: there is
// nothing to parse and nothing to get wrong.
export type SnoozeUnit = "minutes" | "hours" | "days";

const UNIT_MINUTES: Record<SnoozeUnit, number> = {
  minutes: 1,
  hours: MINUTES_PER_HOUR,
  days: MINUTES_PER_DAY,
};

/**
 * The snooze length in minutes for a typed count and unit, or null when the pair is not a length the
 * Gateway would accept - the editor uses null to keep its Save button disabled rather than let the user
 * fire a request that can only come back rejected.
 */
export function snoozeMinutesFrom(count: string, unit: SnoozeUnit): number | null {
  const trimmed = count.trim();
  if (trimmed === "") return null;

  const parsed = Number(trimmed);
  if (!Number.isInteger(parsed)) return null;

  const minutes = parsed * UNIT_MINUTES[unit];
  if (minutes < SNOOZE_MIN_MINUTES || minutes > SNOOZE_MAX_MINUTES) return null;
  return minutes;
}

/**
 * The count and unit to open the editor with for an existing length: the largest unit that divides it
 * evenly, so "4 hours" opens as 4 hours rather than 240 minutes.
 */
export function snoozeDraftFrom(minutes: number): { count: string; unit: SnoozeUnit } {
  if (minutes % MINUTES_PER_DAY === 0) return { count: String(minutes / MINUTES_PER_DAY), unit: "days" };
  if (minutes % MINUTES_PER_HOUR === 0) return { count: String(minutes / MINUTES_PER_HOUR), unit: "hours" };
  return { count: String(minutes), unit: "minutes" };
}

function plural(count: number, noun: string): string {
  return `${count} ${noun}${count === 1 ? "" : "s"}`;
}

/**
 * The words for a snooze length: "15 minutes", "1 hour", "4 hours", "1 hour 30 minutes", "2 days".
 * Whole units read as one word; a length that does not divide evenly reads as the larger unit plus the
 * remainder, so nothing is ever rounded away and the row always names the exact length it sets.
 */
export function formatSnoozeLength(minutes: number): string {
  if (!Number.isInteger(minutes) || minutes < 1) return `${minutes} minutes`;

  if (minutes < MINUTES_PER_HOUR) return plural(minutes, "minute");

  if (minutes < MINUTES_PER_DAY) {
    const hours = Math.floor(minutes / MINUTES_PER_HOUR);
    const rest = minutes % MINUTES_PER_HOUR;
    return rest === 0 ? plural(hours, "hour") : `${plural(hours, "hour")} ${plural(rest, "minute")}`;
  }

  const days = Math.floor(minutes / MINUTES_PER_DAY);
  const restMinutes = minutes % MINUTES_PER_DAY;
  if (restMinutes === 0) return plural(days, "day");

  const hours = Math.round(restMinutes / MINUTES_PER_HOUR);
  // A remainder under half an hour would render as "1 day 0 hours", which reads as a bug. Name the
  // exact minutes instead - these lengths are rare and being exact matters more than being short.
  if (hours === 0) return `${plural(days, "day")} ${plural(restMinutes, "minute")}`;
  return `${plural(days, "day")} ${plural(hours, "hour")}`;
}
