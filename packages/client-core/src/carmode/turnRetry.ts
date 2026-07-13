// The pure retry policy for held Car Mode turns (Car Mode mission, offline-resilience Phase 4a, issue
// #1427). No React, no network, no storage - just the decisions the useCarMode driver applies, extracted
// so they can be unit-tested exactly. The cadence mirrors the proven dictation background driver
// (dictation/backgroundSend.ts): try hard for the first hour, then throttle to a slow background attempt
// forever - never stop, never discard the audio.

import type { PendingCarModeTurn } from "./pendingTurnStore";

// ---- retry cadence ---------------------------------------------------------------------------------
// Try hard for the first hour after the command was captured, then throttle to a slow attempt so a long
// outage does not hammer a dead connection - but never stop and never discard the audio.
export const HARD_WINDOW_MS = 60 * 60 * 1000; // "hard" retries for the first hour since capture
const HARD_MIN_DELAY_MS = 2_000; // first hard retry after two seconds
const HARD_MAX_DELAY_MS = 15_000; // hard exponential backoff caps at fifteen seconds
const THROTTLED_DELAY_MS = 5 * 60 * 1000; // after the hard hour: one slow attempt every five minutes

// A held turn older than this is NOT auto-fired even if it never reached the brain (Architect decision,
// Q2): the world has moved on, so firing a stale action blind is wrong. It is surfaced to the owner for a
// spoken/tapped yes instead. Reads are equally gated - a half-hour-old answer is not worth speaking blind.
export const STALE_TURN_MS = 30 * 60 * 1000;

/** How a held turn should be handled when connectivity returns.
 *  - "auto": the brain call never started AND the turn is fresh, so re-driving it is a safe first brain
 *    call - auto-retry it.
 *  - "ask-owner": either the brain call was already sent (its result is unknown, so a blind retry could
 *    double-act - held until Phase 4b's idempotency key makes it safe) OR the turn is too old to fire
 *    blind. Surface it to the owner for an explicit send/discard. */
export type HeldDisposition = "auto" | "ask-owner";

/** Decide how a held turn is handled on reconnect (Architect decisions Q1/Q2). The `brainSent` flag is
 *  the safety boundary; the staleness cap is the second gate. */
export function classifyHeldTurn(rec: Pick<PendingCarModeTurn, "brainSent" | "createdAt">, now: number): HeldDisposition {
  if (rec.brainSent) return "ask-owner"; // already sent to the brain; result unknown; do not auto-fire
  if (now - rec.createdAt >= STALE_TURN_MS) return "ask-owner"; // too old to fire blind
  return "auto";
}

/** The delay before the next automatic retry attempt: hard exponential (from two seconds, capped at
 *  fifteen) for the first hour since capture, then throttled to five minutes - forever. `attempt` is the
 *  zero-based count of hard attempts already made this run. */
export function nextTurnRetryDelayMs(createdAt: number, attempt: number, now: number): number {
  const age = now - createdAt;
  if (age >= HARD_WINDOW_MS) return THROTTLED_DELAY_MS;
  return Math.min(HARD_MIN_DELAY_MS * 2 ** attempt, HARD_MAX_DELAY_MS);
}

// ---- the audible, honest, plain-English lines (mission decision 8: never a silent stall) -------------

/** Spoken + shown the moment a turn is held because the fleet is unreachable. It says the request is
 *  SAVED, never lost, and will send automatically - the walkie-talkie equivalent of the dictation
 *  "waiting for a connection" line. */
export const HOLDING_MESSAGE =
  "I can't reach the fleet right now. I've saved your request and I'll send it the moment we're back online.";

/** Spoken prefix on the reply of a held turn that finally lands after reconnect, so the owner knows this
 *  answer is the delayed one he asked for earlier, not a reply to something he just said (Architect Q3:
 *  always audibly acknowledge a landed held turn, briefly). */
export const RECOVERY_PREFIX = "Back online. ";

/** Spoken + shown when a turn was already sent to the brain but its result is unknown, so it is held for
 *  the owner rather than auto-fired. Honest about the uncertainty. */
export const AMBIGUOUS_HELD_MESSAGE =
  "I sent your last request but couldn't confirm it went through, so I've left it alone to avoid doing it twice. Say or tap discard to clear it.";

/** Spoken once when the hands-free end-phrase watch has failed to reach the Gateway several times in a
 *  row, so the owner is not left silently wondering why his "over and out" never lands (the silent-stall
 *  fix). It stays trying in the background. */
export const CONNECTION_DOWN_MESSAGE =
  "I'm not able to reach the fleet - the connection looks down. I'll keep trying, and your recording is saved.";
