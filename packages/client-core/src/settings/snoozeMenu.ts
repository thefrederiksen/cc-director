import { formatSnoozeLength } from "./snoozeFormat";
import type { SnoozeOptions } from "./snoozeOptions";

// What the Snooze part of a session menu should say, decided from the two facts that drive it: whether
// the session is already snoozed, and whether this client has learned the user's lengths from the Gateway.
//
// This is the TypeScript twin of src/CcDirector.Avalonia/SnoozeMenuModel.cs. The desktop is C# and the
// Cockpit and phone are TypeScript with no shared runtime, so the decision is written twice - but it must
// stay identical, because these are the same Gateway-owned lengths and a menu that reads differently on
// two devices reads as two different features. snoozeMenu.test.ts pins the same strings the C#
// SnoozeMenuModelTests pin.

export interface SnoozeChoice {
  /** The words the row shows, e.g. "4 hours" or "1 hour  (default)". */
  header: string;
  /** The length this row snoozes for. */
  minutes: number;
}

export interface SnoozeMenu {
  /** The plain top item: "Snooze  (1 hour)", "Snooze", or "Unsnooze". */
  toggleHeader: string;
  /** The "Snooze for" rows. EMPTY means offer no submenu at all. */
  choices: SnoozeChoice[];
}

/**
 * Decide the menu.
 *
 * `options` is null when this client has never successfully read the lengths from the Gateway. That is not
 * a failure to paper over: the plain Snooze still works (a hold with no length makes the Gateway apply the
 * default), so the item does not claim a length it does not know and no submenu appears. Inventing a
 * plausible list here would be the one genuinely bad outcome - it would show lengths that are not the
 * user's.
 *
 * The submenu is offered while snoozed too: re-snoozing to a different length in one step is the point.
 */
export function buildSnoozeMenu(isOnHold: boolean, options: SnoozeOptions | null): SnoozeMenu {
  if (options === null || options.presets.length === 0) {
    return { toggleHeader: isOnHold ? "Unsnooze" : "Snooze", choices: [] };
  }

  const toggleHeader = isOnHold ? "Unsnooze" : `Snooze  (${formatSnoozeLength(options.defaultMinutes)})`;

  const choices = options.presets.map((minutes) => ({
    header:
      minutes === options.defaultMinutes
        ? `${formatSnoozeLength(minutes)}  (default)`
        : formatSnoozeLength(minutes),
    minutes,
  }));

  return { toggleHeader, choices };
}
