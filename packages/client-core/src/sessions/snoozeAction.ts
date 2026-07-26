// The client-side snooze/hold DISPLAY state and the pure rules that drive every snooze surface - the
// phone Voice-mode bottom bar and the Chat/Terminal overflow menu (both through useSessionManage). Kept
// here, unit-tested, so the mobile hook is a thin consumer and the "instant feedback" behaviour cannot
// silently regress.
//
// WHY A TRI-STATE, NOT A BOOLEAN. A hold has two ON states the UI must tell apart, because the Gateway
// does (HoldResponse.Pending):
//   - HELD: an armed snooze whose clock is running NOW - the session was idle/waiting when it was
//     snoozed, so the hold landed immediately.
//   - DEFERRED: the snooze was asked for while the agent was WORKING, so its clock starts when the work
//     ENDS (owner ruling, 14 July 2026). It is a REAL, ACCEPTED snooze that has not armed yet.
//
// The bug this closes: the mobile client used to read only the boolean `onHold`, which is false for a
// DeferredHold. So snoozing a WORKING (e.g. narrating) session recorded the snooze on the Gateway but
// showed NO change on screen - the button stayed "Snooze", no pill appeared - and the owner reported the
// Snooze button "does nothing" in voice mode. The wire already carries `Pending` for exactly this; the
// client simply dropped it.

export interface HoldUiState {
  /** An armed snooze whose clock is running now. */
  held: boolean;
  /** A deferred snooze - accepted, will arm the moment the current work ends. */
  deferred: boolean;
}

/** Snoozed in EITHER sense - what the toggle turns off and what the button label reflects. */
export function isSnoozing(s: HoldUiState): boolean {
  return s.held || s.deferred;
}

/**
 * The state to show THE INSTANT the user taps, before the server answers, so a snooze is never silent.
 * Snoozing a WORKING session shows DEFERRED ("it'll snooze when it finishes"); snoozing a settled one
 * shows HELD immediately. Tapping again while snoozing (either sense) clears it. This is optimistic and
 * is reconciled by {@link holdStateFromResponse} the moment the hold endpoint answers.
 */
export function optimisticHoldToggle(current: HoldUiState, working: boolean): HoldUiState {
  if (isSnoozing(current)) return { held: false, deferred: false }; // toggling OFF
  return working ? { held: false, deferred: true } : { held: true, deferred: false };
}

/**
 * The state to show THE INSTANT the user picks an explicit snooze LENGTH ("Snooze for 4 hours"),
 * before the server answers. Distinct from {@link optimisticHoldToggle} in one way that matters:
 * picking a length is ALWAYS a hold, never an unsnooze - picking one while already snoozed re-arms the
 * clock to the new length. So it never flips to off, and it shows DEFERRED for a working session for
 * the same reason the toggle does (the clock starts when the work ends).
 */
export function optimisticHoldFor(working: boolean): HoldUiState {
  return working ? { held: false, deferred: true } : { held: true, deferred: false };
}

/** The reconciled state from the hold endpoint's authoritative tri-state answer. */
export function holdStateFromResponse(res: { onHold: boolean; pending: boolean }): HoldUiState {
  return { held: res.onHold, deferred: res.pending };
}

/** How a hold toggle RESOLVED: the server's authoritative answer, or a failed request. */
export type HoldToggleOutcome =
  | { ok: true; response: { onHold: boolean; pending: boolean } }
  | { ok: false };

/**
 * The UI state to SETTLE on once a hold toggle resolves. On success the server's authoritative tri-state
 * wins. On FAILURE the optimistic flip is rolled all the way back to <paramref name="preTap"/> - the state
 * BEFORE the tap - because the snooze did NOT happen: the button must never falsely read "Snoozed" or
 * "Snoozing when it finishes" for a hold the Gateway rejected. Keeping this decision here (not in the
 * async hook) is what lets the rollback be unit-tested and stops it silently regressing.
 */
export function reconcileHoldToggle(preTap: HoldUiState, outcome: HoldToggleOutcome): HoldUiState {
  return outcome.ok ? holdStateFromResponse(outcome.response) : preTap;
}

/** The snooze button's label for the current state. */
export function holdButtonLabel(s: HoldUiState): "Snooze" | "Unsnooze" {
  return isSnoozing(s) ? "Unsnooze" : "Snooze";
}

/**
 * The pill shown beside the session title, or null for none. A DEFERRED snooze reads "Snoozing when it
 * finishes"; an armed one reads the running countdown (falling back to a plain "Snoozed"); nothing
 * otherwise. <paramref name="snoozedByFold"/> is the Gateway fold's display verdict (where working wins
 * over a landed hold), used for the armed pill so it matches the roster and the desktop rail exactly.
 */
export function holdPillLabel(s: HoldUiState, snoozedByFold: boolean, countdown: string | null): string | null {
  if (s.deferred) return "Snoozing when it finishes";
  if (snoozedByFold) return countdown ? `Snoozed - ${countdown}` : "Snoozed";
  return null;
}
