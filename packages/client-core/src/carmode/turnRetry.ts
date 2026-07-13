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
 *  - "auto": auto-retry it. Phase 4b made this safe regardless of whether the brain call was already sent:
 *    the Gateway now dedupes by the turn's Idempotency-Key, so a re-drive either is a fresh first call (the
 *    turn never reached the server) or returns the cached result (it did) - it ACTS at most once either way.
 *  - "ask-owner": the turn is too old to fire blind (past the staleness cap). Surface it to the owner for
 *    an explicit send/discard - the world has moved on, so a stale action should not fire unprompted. */
export type HeldDisposition = "auto" | "ask-owner";

/** Decide how a held turn is handled on reconnect. Since Phase 4b's server idempotency makes an
 *  already-sent turn safe to auto-retry (it acts at most once), the ONLY gate is the staleness cap: a turn
 *  older than it asks the owner; everything fresher auto-retries. (The `brainSent` flag is still recorded
 *  for diagnostics, but no longer gates the retry.) */
export function classifyHeldTurn(rec: Pick<PendingCarModeTurn, "createdAt">, now: number): HeldDisposition {
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

/** Spoken once when the hands-free end-phrase watch has failed to reach the Gateway several times in a
 *  row, so the owner is not left silently wondering why his "over and out" never lands (the silent-stall
 *  fix). It stays trying in the background. */
export const CONNECTION_DOWN_MESSAGE =
  "I'm not able to reach the fleet - the connection looks down. I'll keep trying, and your recording is saved.";
