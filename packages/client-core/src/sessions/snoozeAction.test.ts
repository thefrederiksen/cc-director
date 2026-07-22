import { describe, expect, it } from "vitest";
import {
  holdButtonLabel,
  holdPillLabel,
  holdStateFromResponse,
  isSnoozing,
  optimisticHoldToggle,
  type HoldUiState,
} from "./snoozeAction";

const none: HoldUiState = { held: false, deferred: false };

describe("snooze action - instant, honest feedback for every case", () => {
  it("snoozing a WORKING session shows the DEFERRED affordance immediately - not 'no change'", () => {
    // This is the exact owner symptom: on a working/narrating session the old client dropped Pending and
    // showed nothing. The optimistic state must flip the moment the tap lands.
    const optimistic = optimisticHoldToggle(none, /* working */ true);
    expect(optimistic).toEqual({ held: false, deferred: true });
    expect(holdButtonLabel(optimistic)).toBe("Unsnooze");
    expect(holdPillLabel(optimistic, /* snoozedByFold */ false, null)).toBe("Snoozing when it finishes");

    // ...and the server's deferred answer reconciles to the same state (self-heals to Held when work ends).
    expect(holdStateFromResponse({ onHold: false, pending: true })).toEqual({ held: false, deferred: true });
  });

  it("snoozing an IDLE session shows HELD immediately", () => {
    const optimistic = optimisticHoldToggle(none, /* working */ false);
    expect(optimistic).toEqual({ held: true, deferred: false });
    expect(holdButtonLabel(optimistic)).toBe("Unsnooze");

    // The server's armed answer agrees; the pill reads the running countdown from the fold.
    const reconciled = holdStateFromResponse({ onHold: true, pending: false });
    expect(reconciled).toEqual({ held: true, deferred: false });
    expect(holdPillLabel(reconciled, /* snoozedByFold */ true, "3h 48m")).toBe("Snoozed - 3h 48m");
  });

  it("un-snoozing clears both senses and the label returns to Snooze", () => {
    expect(optimisticHoldToggle({ held: true, deferred: false }, false)).toEqual(none);
    expect(optimisticHoldToggle({ held: false, deferred: true }, true)).toEqual(none);
    expect(holdButtonLabel(none)).toBe("Snooze");
    expect(isSnoozing(none)).toBe(false);
  });

  it("a deferred hold's pill wins over the fold verdict, and a plain armed hold with no clock reads 'Snoozed'", () => {
    expect(holdPillLabel({ held: false, deferred: true }, false, null)).toBe("Snoozing when it finishes");
    expect(holdPillLabel({ held: true, deferred: false }, true, null)).toBe("Snoozed");
    expect(holdPillLabel(none, false, null)).toBeNull();
  });
});
