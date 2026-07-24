// The Voice tab's fleet-wide voice sweep (owner, 2026-07-24).
//
// Opening the Voice tab IS the decision "read my whole fleet to me", so every session on the Gateway
// is put into voice mode on arrival - including sessions created after you got there. This module
// owns the one decision that sweep needs: given the roster and the ids already attempted on this
// visit, which sessions are still not voice sessions and have not been asked yet.
//
// It is a separate pure function, and not three lines inline in the roster, because getting it wrong
// is expensive in a way the screen would never show: the roster re-polls every 5 seconds, and a
// session whose computer is offline is SKIPPED by the Gateway and therefore stays "not a voice
// session" for as long as its machine is down. A naive "any session still off -> call the Gateway"
// would then fan out a fleet-wide write every 5 seconds, forever, for a session that can never be
// switched. Remembering what was already asked is the whole of the fix, and it is worth a test.

import type { SessionDto } from "../api/client";

/**
 * The sessions to ask the Gateway to switch into voice mode: those that are not voice sessions and
 * have not already been attempted on this visit. Returns the session ids, in roster order.
 *
 * `attempted` is the caller's memory of this visit (cleared when the Voice tab is left, so coming
 * back re-arms the sweep). Sessions with no id are ignored - there is nothing to ask about.
 */
export function sessionsNeedingVoice(
  sessions: readonly SessionDto[],
  attempted: ReadonlySet<string>,
): string[] {
  const ids: string[] = [];
  for (const s of sessions) {
    const sid = (s.sessionId ?? "").trim();
    if (sid.length === 0) continue;
    if (s.voiceMode) continue;
    if (attempted.has(sid)) continue;
    ids.push(sid);
  }
  return ids;
}
