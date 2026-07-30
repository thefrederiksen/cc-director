// The one place any web client turns "a prompt to this session did not go" into something to render
// (issue internal#811).
//
// WHY THIS FILE EXISTS. On 2026-07-15 two spoken prompts failed to submit. The Director wrote
// "Command FAILED: the composer never echoed the typed text" into a log file and nothing else happened -
// no badge, no banner, nothing on any screen. The words were gone and the bug went on losing more of them
// for two days, because the only witness was a file nobody reads. A delivery failure is the user's
// sentence being deleted; it belongs on the screen.
//
// THE DIVISION OF LABOUR. The Director counts the failures (only the machine running the terminal can see
// one happen). The Gateway folds the SENTENCE - SessionOrdering.PromptDeliveryNotice - so every client
// renders one set of words it did not compose (CLAUDE.md rule 7). This file does no ruling: it reads the
// stamped fields and decides only the SHAPE of the badge and its tooltip, once, so the Cockpit roster and
// the mobile roster cannot say the same fact two different ways.
import type { SessionDto } from "../api/client";

// The generated schema.ts does not carry these fields yet, so they are augmented here - the same way
// uncommittedCount is augmented in changes.ts and the Gateway-stamped fields are in ordering.ts.
type SessionWithDelivery = SessionDto & {
  promptDeliveryNotice?: string | null;
  promptDeliveryUnresolved?: boolean | null;
  failedPromptDeliveries?: number | string | null;
  composerEchoMisses?: number | string | null;
};

/** Coerce the numeric-string form the serializer can emit; anything unparseable counts as zero. */
function count(raw: number | string | null | undefined): number {
  if (raw === null || raw === undefined) return 0;
  const n = Number(raw);
  if (!Number.isFinite(n) || n < 0) return 0;
  return Math.floor(n);
}

/**
 * The Gateway's plain-English notice that this session's last prompt was lost, or null when there is
 * nothing to say. Rendered VERBATIM - a client never composes these words and never decides when they
 * apply. Null on an old Gateway that does not stamp it, which is the honest answer: no verdict arrived,
 * so none is shown.
 */
export function promptDeliveryNotice(session: SessionDto): string | null {
  const notice = (session as SessionWithDelivery).promptDeliveryNotice;
  if (typeof notice !== "string") return null;
  const trimmed = notice.trim();
  return trimmed.length > 0 ? trimmed : null;
}

/**
 * Should this session's row wear the loud "not delivered" badge? True only while the failure is live -
 * the last send failed and nothing has landed since. Driven by the Gateway's notice, so the badge and the
 * banner cannot disagree about whether there is a problem.
 */
export function hasUndeliveredPrompt(session: SessionDto): boolean {
  return promptDeliveryNotice(session) !== null;
}

/** The badge text. Short, because it sits in a row of chips; the tooltip carries the detail. */
export const DELIVERY_BADGE_TEXT = "not delivered";

/**
 * The badge's tooltip: the Gateway's sentence, plus how often this has happened on this session when it
 * has happened more than once. The counts are shown ONLY here and never as their own chip - a number on
 * the row would compete with the thing the reader must act on, which is that their words are gone.
 */
export function promptDeliveryTitle(session: SessionDto): string | null {
  const notice = promptDeliveryNotice(session);
  if (notice === null) return null;
  const history = promptDeliveryHistory(session);
  return history === null ? notice : `${notice} (${history})`;
}

/**
 * How often delivery has gone wrong on this session, in plain words, or null when there is nothing worth
 * saying - a single failure and no composer retries is already fully described by the notice.
 *
 * Counts SURVIVE a recovery on purpose: "this has failed four times today" stays true after the fifth
 * attempt gets through, and it is the number that says whether the session is sick or was unlucky once.
 */
export function promptDeliveryHistory(session: SessionDto): string | null {
  const s = session as SessionWithDelivery;
  const failures = count(s.failedPromptDeliveries);
  const misses = count(s.composerEchoMisses);
  const parts: string[] = [];
  if (failures > 1) parts.push(`${failures} failed deliveries on this session`);
  if (misses > 0) parts.push(misses === 1 ? "1 composer retry" : `${misses} composer retries`);
  return parts.length > 0 ? parts.join(", ") : null;
}
