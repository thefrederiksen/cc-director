import { describe, expect, it } from "vitest";
import {
  holdButtonLabel,
  holdPillLabel,
  holdStateFromResponse,
  isSnoozing,
  optimisticHoldToggle,
  reconcileHoldToggle,
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

  it("a FAILED /hold rolls the optimistic snooze back to the pre-tap state - never a false 'Snoozed'", () => {
    // The tap optimistically flipped an idle session to HELD. When the /hold POST rejects, the snooze did
    // NOT happen: the UI must settle back on the PRE-TAP state, so the button reads "Snooze" again (not
    // "Unsnooze") and no snoozed pill is shown. The hook pairs this rollback with an error message.
    const preTap = none;
    const optimistic = optimisticHoldToggle(preTap, /* working */ false);
    expect(holdButtonLabel(optimistic)).toBe("Unsnooze"); // it briefly showed snoozed...

    const settled = reconcileHoldToggle(preTap, { ok: false });
    expect(settled).toEqual(preTap); // ...and rolls all the way back on failure
    expect(holdButtonLabel(settled)).toBe("Snooze");
    expect(holdPillLabel(settled, /* snoozedByFold */ false, null)).toBeNull();

    // A failed UN-snooze rolls back the other way - a still-armed hold stays "Unsnooze", not a false clear.
    const armed: HoldUiState = { held: true, deferred: false };
    expect(reconcileHoldToggle(armed, { ok: false })).toEqual(armed);
    expect(holdButtonLabel(reconcileHoldToggle(armed, { ok: false }))).toBe("Unsnooze");
  });

  it("a SUCCESSFUL /hold still settles on the server's authoritative tri-state (success path unchanged)", () => {
    expect(reconcileHoldToggle(none, { ok: true, response: { onHold: true, pending: false } })).toEqual({
      held: true,
      deferred: false,
    });
    expect(reconcileHoldToggle(none, { ok: true, response: { onHold: false, pending: true } })).toEqual({
      held: false,
      deferred: true,
    });
  });
});
