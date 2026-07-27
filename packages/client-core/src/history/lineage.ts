// Turning the flat work-history list into the shape it actually had (internal#989, on the record
// internal#982 laid down).
//
// A day where you started three things and they spawned nineteen helpers is a list of twenty-two
// rows and a tree of three roots. Both contain identical data and they tell completely different
// stories; only one of them answers "what did I set in motion".
//
// THE HARD PART IS NOT THE TREE, IT IS THE PARENTS THAT ARE NOT THERE. The History page groups by
// repository and then by day, and lineage cuts straight across both: `session spawn <other-repo>`
// is the fleet's most ordinary move, so a child very often sits in a different group from its
// parent - and a parent can also have been pruned by the 90-day retention, or simply started before
// the window the page is showing. Three genuinely different situations, and collapsing them would
// each be its own small lie:
//
//   - parent in this group      -> nest it. The tree does its job.
//   - parent elsewhere in the report -> keep it at top level and SAY who started it, with the id so
//                                  the page can link. Moving the row into the parent's group would
//                                  file work under a repository it never touched.
//   - parent nowhere in the report -> keep it at top level and say the parent is outside this view.
//                                  Silently treating it as a root would invent a root, and roots are
//                                  the thing being counted.
//
// Pure functions over plain data, no React, so the rules above are testable on their own.
import type { WorkHistoryReport, WorkHistorySession } from "./historyClient";

/** One session in the tree, with its children and an honest note about a parent that is not here. */
export interface LineageNode {
  session: WorkHistorySession;
  /** Nested children, oldest first. Empty for a leaf. */
  children: LineageNode[];
  /**
   * Set when this session names a parent that is NOT in the same group. `label` is the parent's
   * display name when the report contains it, or null when the parent is outside the report
   * entirely (pruned, or started before the window). A null label is not a missing value to hide:
   * "started by a session we no longer keep" is a true and useful thing to show.
   */
  parentElsewhere?: { sessionId: string; label: string | null };
}

/** How many sessions sit under a node, itself included. */
export function nodeCount(node: LineageNode): number {
  let total = 1;
  for (const child of node.children) total += nodeCount(child);
  return total;
}

/** A short human label for a session row: its name, else its number, else a short id. */
export function sessionLabel(session: WorkHistorySession): string {
  if (session.sessionName != null && session.sessionName.length > 0) return session.sessionName;
  if (session.sessionNumber != null) return `#${session.sessionNumber}`;
  return session.sessionId.slice(0, 8);
}

/** Every session in the report, keyed by id - the index a cross-group parent is resolved against. */
export function indexReport(report: WorkHistoryReport): Map<string, WorkHistorySession> {
  const index = new Map<string, WorkHistorySession>();
  for (const repo of report.repos)
    for (const day of repo.days)
      for (const session of day.sessions)
        if (!index.has(session.sessionId)) index.set(session.sessionId, session);
  return index;
}

/**
 * Build the forest for ONE group's sessions (one repository, one day), resolving cross-group parents
 * against `reportIndex` - the whole report - so a child can name its parent even when that parent is
 * filed under another repository or another day.
 *
 * Order is preserved: roots come out in the order they arrived (the page sorts them already), and
 * children are ordered oldest-first, because a parent's children read as the sequence of things it
 * set off.
 */
export function buildLineage(
  sessions: readonly WorkHistorySession[],
  reportIndex?: ReadonlyMap<string, WorkHistorySession>,
): LineageNode[] {
  const inGroup = new Map<string, WorkHistorySession>();
  for (const s of sessions) if (!inGroup.has(s.sessionId)) inGroup.set(s.sessionId, s);

  const nodes = new Map<string, LineageNode>();
  for (const s of inGroup.values()) nodes.set(s.sessionId, { session: s, children: [] });

  const roots: LineageNode[] = [];
  for (const s of inGroup.values()) {
    const node = nodes.get(s.sessionId)!;
    const parentId = s.parentSessionId ?? null;

    if (parentId === null || parentId === s.sessionId) {
      // No parent, or a session claiming itself. The self-parent case is not paranoia for its own
      // sake: the id is minted on another machine and arrives over the wire, and a node that is its
      // own child would hang the render rather than show a wrong number.
      roots.push(node);
      continue;
    }

    const parentHere = nodes.get(parentId);
    if (parentHere !== undefined && !wouldCycle(parentId, s.sessionId, inGroup)) {
      parentHere.children.push(node);
      continue;
    }

    // Either the parent is not in this group, or attaching would close a cycle. Both end up at top
    // level carrying the note; a cycle is corrupt data and the honest render is the flat one.
    //
    // Mutated in place rather than copied. A child may be attached to this same node either before
    // or after we get here - the iteration order is the map's, not the tree's - and a copy would
    // leave two objects that have to be kept in step for the rest of the function.
    const known = reportIndex?.get(parentId);
    node.parentElsewhere = { sessionId: parentId, label: known ? sessionLabel(known) : null };
    roots.push(node);
  }

  const byStart = (a: LineageNode, b: LineageNode) =>
    a.session.startedAtUtc < b.session.startedAtUtc ? -1 : a.session.startedAtUtc > b.session.startedAtUtc ? 1 : 0;
  const sortChildren = (node: LineageNode, depth: number) => {
    if (depth > 64) return; // corrupt data cannot make this loop forever
    node.children.sort(byStart);
    for (const child of node.children) sortChildren(child, depth + 1);
  };
  for (const root of roots) sortChildren(root, 0);

  return roots;
}

/**
 * Would attaching `childId` under `parentId` close a loop? Walks the PARENT-POINTER chain upward
 * from the prospective parent, not the half-built tree.
 *
 * That distinction is the whole of it, and the first version got it wrong. Walking the tree asks
 * "is the child already underneath the parent right now", which depends on how far construction has
 * got: for a two-session cycle (a names b, b names a) neither node has children at the moment it is
 * examined, so both attach, the forest comes out with NO roots at all, and the first thing to walk
 * the result loops forever. The parent chain is in the data from the start, so the answer does not
 * depend on iteration order.
 *
 * A chain that leaves the group is not a cycle - it just ends. A chain that revisits any id, or runs
 * implausibly long, is refused.
 */
function wouldCycle(
  parentId: string,
  childId: string,
  inGroup: ReadonlyMap<string, WorkHistorySession>,
): boolean {
  const seen = new Set<string>();
  let current: string | undefined = parentId;
  for (let hops = 0; current !== undefined && hops < 256; hops++) {
    if (current === childId) return true;
    if (seen.has(current)) return true;
    seen.add(current);
    const parent = inGroup.get(current);
    if (parent === undefined) return false; // the chain leaves this group; nothing to close
    current = parent.parentSessionId ?? undefined;
  }
  return current !== undefined; // ran out of hops with more chain to go: refuse
}

/** What the origin markers add up to over a set of sessions. Counts only - see originTotals. */
export interface OriginTally {
  human: number;
  agent: number;
  schedule: number;
  /** The create path was asked and had nothing to say. */
  unknown: number;
  /** The row predates the field entirely. A different thing from `unknown`. */
  notRecorded: number;
  total: number;
}

/**
 * Tally how a set of sessions came to exist.
 *
 * `unknown` and `notRecorded` are kept apart, and neither is folded into `human`. That fold is the
 * one mistake this whole feature exists to prevent: these fields only started being written on
 * 2026-07-27, so for any window reaching back before that the unrecorded rows are the MAJORITY, and
 * quietly counting them as human would report the exact opposite of the truth.
 */
export function tallyOrigins(sessions: Iterable<WorkHistorySession>): OriginTally {
  const tally: OriginTally = { human: 0, agent: 0, schedule: 0, unknown: 0, notRecorded: 0, total: 0 };
  for (const s of sessions) {
    tally.total++;
    switch (s.originKind ?? null) {
      case "human": tally.human++; break;
      case "agent": tally.agent++; break;
      case "schedule": tally.schedule++; break;
      case "unknown": tally.unknown++; break;
      default: tally.notRecorded++; break;
    }
  }
  return tally;
}

/** How many sessions in the tally we can actually account for - the only honest denominator. */
export function accountedFor(tally: OriginTally): number {
  return tally.human + tally.agent + tally.schedule;
}

/**
 * The plain-language marker for a row: who started this session.
 *
 * Deliberately not "you". The Gateway is multi-tenant and "a person" is all the record says - which
 * human is not stored, so claiming it would be inventing a fact to make a nicer sentence. Returns
 * null for unknown and for unrecorded, because a row that cannot say who started it should show
 * nothing rather than a hedge.
 */
export function originLabel(session: WorkHistorySession): string | null {
  switch (session.originKind ?? null) {
    case "human": return "started by hand";
    case "agent": return "started by an agent";
    case "schedule": return "started by a schedule";
    default: return null;
  }
}
