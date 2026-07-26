// The one place any web client turns a session's uncommitted file count into something to render.
//
// The count itself is a FACT and only the machine holding the checkout can measure it: the owning
// Director's SessionGitStatusMonitor probes git and reports it on SessionDto.uncommittedCount, and the
// Gateway passes it through untouched. What is shared here is the WORDING, so the Cockpit roster and the
// mobile roster cannot drift into showing the same number two different ways.
//
// THE RULE THIS FILE EXISTS TO HOLD: null and 0 both render NOTHING, and they are not the same thing.
// Null means the git probe has not succeeded - no git on the path, a permissions problem, a repository
// mid-rebase, or a Director too old to know the field - and "we could not tell" must never be shown as a
// clean tree. Zero means a probe DID run and found nothing changed. Both are silent on the row, because a
// badge reading "0 chg" is noise; the difference matters upstream, where a false zero would overwrite a
// real measurement (see SessionDto.UncommittedCount, issue 516).
import type { SessionDto } from "../api/client";

// The generated schema.ts does not carry this field yet, so it is augmented here - the same way
// snoozeUntil, holdState and the other Gateway-stamped fields are augmented in ordering.ts.
type SessionWithChanges = SessionDto & {
  uncommittedCount?: number | string | null;
};

/**
 * How many files are changed in this session's working tree, or null when that is unknown.
 * Coerces the numeric-string form the serializer can emit (the same coercion inDesktopOrder does for
 * sortOrder), and treats anything unparseable as unknown rather than as zero.
 */
export function uncommittedCount(session: SessionDto): number | null {
  const raw = (session as SessionWithChanges).uncommittedCount;
  if (raw === null || raw === undefined) return null;
  const n = Number(raw);
  if (!Number.isFinite(n) || n < 0) return null;
  return Math.floor(n);
}

/**
 * The roster badge text for a session's uncommitted work ("12 chg"), or null when there is nothing to
 * show - which is BOTH a clean tree and an unknown one. Matches the desktop rail's amber "N chg" pill.
 */
export function changesBadge(session: SessionDto): string | null {
  const n = uncommittedCount(session);
  if (n === null || n === 0) return null;
  return `${n} chg`;
}

/**
 * The badge's tooltip - the long form of what the number means, since "chg" is a squeeze that only makes
 * sense once. Null whenever there is no badge.
 */
export function changesTitle(session: SessionDto): string | null {
  const n = uncommittedCount(session);
  if (n === null || n === 0) return null;
  return n === 1 ? "1 uncommitted file in this session's working tree"
                 : `${n} uncommitted files in this session's working tree`;
}
