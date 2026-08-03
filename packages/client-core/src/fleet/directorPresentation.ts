import {
  REACHABILITY_OFFLINE,
  REACHABILITY_STOPPED,
  REACHABILITY_WOBBLY,
  type DirectorReachability,
} from "./fleetClient";

/**
 * How a Director is PRESENTED - read from the Gateway, never re-derived here.
 *
 * It lives in client-core, not in one app, because BOTH shells answer these questions about the same
 * Director: the Cockpit's map, session roster and Directors table, and the phone's roster note. When it
 * sat in the Cockpit only, the phone kept its own copy of the rule and collapsed every non-online state
 * to "offline" - so a Director that had been shut down on purpose was announced on the phone as
 * "Unreachable", which is the false outage this whole change exists to remove, one surface over.
 *
 * The Gateway folds all four of these judgements onto every reachability row (FleetReachabilityFold), and
 * these readers exist only so the views take them from ONE place. They used to be four separate `state ===`
 * comparisons written out in three different components: the lane header, the Director sub-group, and the
 * card. That shape is why a new state is a bug hunt rather than an edit, and why the map called a healthy
 * machine unreachable - a view that rules for itself renders something plausible the moment it meets a state
 * it did not expect.
 *
 * THE `??` BRANCHES ARE WIRE COMPATIBILITY, NOT A SECOND OPINION. They fire only when the field is absent -
 * an older Gateway that predates the fold - and they reproduce exactly what that Gateway's clients used to
 * render. They are never consulted when the Gateway has spoken, so the two can never disagree, and there is
 * one copy of them rather than one per component.
 */

/** The badge word; empty means no badge. */
export function directorStateLabel(r: DirectorReachability | undefined): string {
  if (r === undefined) return "";
  if (r.stateLabel !== undefined) return r.stateLabel;
  if (r.state === REACHABILITY_OFFLINE) return "Offline";
  if (r.state === REACHABILITY_WOBBLY) return "Wobbly";
  if (r.state === REACHABILITY_STOPPED) return "Not running";
  return "";
}

/** True when the rows are last-known rather than confirmed: dim the cards and show the last-seen age. */
export function isDataStale(r: DirectorReachability | undefined): boolean {
  if (r === undefined) return false;
  if (r.dataIsStale !== undefined) return r.dataIsStale;
  return r.state === REACHABILITY_OFFLINE || r.state === REACHABILITY_WOBBLY || r.state === REACHABILITY_STOPPED;
}

/** True when "+ New session" could actually be honoured - the Director's tunnel is up. */
export function canStartSessionOn(r: DirectorReachability | undefined): boolean {
  if (r === undefined) return true; // no entry at all: the historical render, which offered the action
  if (r.canStartSession !== undefined) return r.canStartSession;
  return r.state !== REACHABILITY_OFFLINE && r.state !== REACHABILITY_STOPPED;
}

/** The line to print where a Director has no sessions, saying why there are none. */
export function emptySlotTextOf(r: DirectorReachability | undefined): string {
  const free = "No sessions - free slot";
  if (r === undefined) return free;
  if (r.emptySlotText !== undefined && r.emptySlotText.length > 0) return r.emptySlotText;
  if (r.state === REACHABILITY_OFFLINE) return "No sessions - this director cannot be reached";
  if (r.state === REACHABILITY_STOPPED) return "No sessions - this director is not running";
  return free;
}
