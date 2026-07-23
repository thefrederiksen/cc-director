import type { SessionDto } from "@devthrottle/client-core/api/client";
import { REACHABILITY_OFFLINE, type DirectorReachability } from "@devthrottle/client-core/fleet/fleetClient";

/**
 * Pure helpers for the Fleet Map's card rendering, kept out of FleetMapView.tsx so they can be unit
 * tested. The Cockpit's vitest run has no DOM environment, so a helper that lives inside the component
 * file is a helper that cannot be tested; anything with a rule worth stating belongs here.
 */

/** One card in a lane, flattened out of the controller tree with the indent level it renders at. */
export interface TreeNode {
  session: SessionDto;
  /** 0 for a root card; 1 for a child of a root, and so on. Not capped - nesting is real. */
  depth: number;
}

/**
 * Issue #1626: order a lane's sessions as the spawn tree the Gateway already resolves, so a Manager's
 * Workers sit under it instead of scattered through the lane.
 *
 * The edge is `controllerSessionId` (issue #815), stamped at birth by whoever spawned the session -
 * `cc-devthrottle session spawn` sets it to the spawning session by default. The Gateway is the only
 * thing that can resolve the ROLE from it (see FleetRoleResolver: "is my controller alive?" is
 * unanswerable from one Director, because the controller may be on another machine), and we do not
 * re-derive that here - `sessionRole` is read, never recomputed. This function decides ORDER and INDENT
 * only.
 *
 * Four rules, each of which is a case that actually occurs:
 *
 *  - A controller that is not in this lane is not a parent here. The pivots slice the fleet, so a
 *    Worker's Manager can be filtered out (a different repository, a different machine). Such a child
 *    renders at the lane's top level rather than under a parent the lane cannot show.
 *  - An EXITED controller is not a parent. FleetRoleResolver already demotes a session whose controller
 *    has exited back to Standalone; indenting it under the corpse would say the opposite of what the
 *    roster says.
 *  - A cycle cannot hang the view. A session that cannot reach a root by walking controllers is treated
 *    as a root itself.
 *  - Every session renders exactly once. Cards are never dropped by this pass - a lost card is a worse
 *    bug than a badly indented one.
 */
export function buildControllerTree(
  sessions: SessionDto[],
  sort: (a: SessionDto, b: SessionDto) => number,
): TreeNode[] {
  const byId = new Map<string, SessionDto>();
  for (const s of sessions) {
    const id = (s.sessionId ?? "").trim();
    if (id.length > 0) byId.set(id, s);
  }

  const isAlive = (s: SessionDto): boolean =>
    (s.activityState ?? "").toLowerCase() !== "exited";

  // The controller this session actually hangs under IN THIS LANE, or null when it is a root here.
  const parentOf = (s: SessionDto): string | null => {
    if (s.isControlled !== true) return null;
    const cid = (s.controllerSessionId ?? "").trim();
    if (cid.length === 0) return null;
    if (cid === (s.sessionId ?? "").trim()) return null; // self-reference: its own root
    const parent = byId.get(cid);
    if (parent === undefined) return null; // controller not in this lane
    if (!isAlive(parent)) return null; // never indent under a corpse
    return cid;
  };

  // Walk up to a root to prove this session is reachable. A session in a cycle never reaches one, so
  // it is promoted to a root rather than being lost or looping forever.
  const reachesRoot = (s: SessionDto): boolean => {
    const seen = new Set<string>();
    let cur: SessionDto | undefined = s;
    while (cur !== undefined) {
      const id = (cur.sessionId ?? "").trim();
      if (seen.has(id)) return false;
      seen.add(id);
      const pid = parentOf(cur);
      if (pid === null) return true;
      cur = byId.get(pid);
    }
    return true;
  };

  const roots: SessionDto[] = [];
  const childrenOf = new Map<string, SessionDto[]>();
  for (const s of sessions) {
    const pid = parentOf(s);
    if (pid === null || !reachesRoot(s)) {
      roots.push(s);
      continue;
    }
    const arr = childrenOf.get(pid);
    if (arr === undefined) childrenOf.set(pid, [s]);
    else arr.push(s);
  }

  roots.sort(sort);
  const out: TreeNode[] = [];
  const emitted = new Set<string>();
  const walk = (s: SessionDto, depth: number): void => {
    const id = (s.sessionId ?? "").trim();
    if (id.length > 0) {
      if (emitted.has(id)) return;
      emitted.add(id);
    }
    out.push({ session: s, depth });
    const kids = childrenOf.get(id);
    if (kids === undefined) return;
    for (const k of [...kids].sort(sort)) walk(k, depth + 1);
  };
  for (const r of roots) walk(r, 0);

  return out;
}

/**
 * The machine identity used by the "By machine" pivot: the trimmed machine name, or "(unknown machine)"
 * when a session/Director carries no name. Both sides of the machine join - the lanes built from sessions
 * AND the reachable-Director list folded in on top of them - MUST key through this one function, or an
 * idle Director could land in a machine lane that its sessions never key to (a session on "SOREN" and a
 * Director advertising "soren " would split into two lanes). One rule, one key.
 */
export function machineKeyOf(machineName: string | null | undefined): { key: string; title: string } {
  const name = (machineName ?? "").trim();
  const title = name.length === 0 ? "(unknown machine)" : name;
  return { key: title.toLowerCase(), title };
}

/** A Director id shortened to its last segment (Directors are "<machine>-<n>" or a guid); keeps a
 * sub-header compact without losing which Director it is. */
export function shortDir(directorId: string): string {
  const dash = directorId.lastIndexOf("-");
  const tail = dash >= 0 ? directorId.slice(dash + 1) : directorId;
  return tail.length > 8 ? tail.slice(0, 8) : tail;
}

/** One machine's reachable Directors, keyed exactly as the machine pivot's lanes are (machineKeyOf). */
export interface MachineDirectors {
  key: string;
  title: string;
  directors: DirectorReachability[];
}

/**
 * The reachable Directors (Online or Wobbly) grouped by machine, keyed EXACTLY as the machine pivot's
 * lanes are (machineKeyOf), so a machine that has a Director but no sessions still resolves to a lane and
 * shows as a free slot the owner can start a session on. An OFFLINE Director is excluded on purpose: it
 * cannot host a session, so it is not a free slot - the roster already surfaces it in the
 * unreachable-machines banner (machineErrors), and showing it here as available would be a lie.
 */
export function reachableDirectorsByMachine(directors: DirectorReachability[]): MachineDirectors[] {
  const byKey = new Map<string, MachineDirectors>();
  for (const d of directors) {
    if (d.state === REACHABILITY_OFFLINE) continue;
    const { key, title } = machineKeyOf(d.machineName);
    let g = byKey.get(key);
    if (g === undefined) {
      g = { key, title, directors: [] };
      byKey.set(key, g);
    }
    g.directors.push(d);
  }
  return [...byKey.values()];
}

/** One Director sub-group inside a machine lane; `sessions` is empty for an idle Director (a free slot). */
export interface DirectorGroup {
  key: string;
  label: string;
  sessions: SessionDto[];
}

/**
 * Group a machine lane's sessions by their owning Director, then FOLD IN every reachable Director on the
 * machine that currently has no sessions, so an idle Director renders as an empty group - a free slot.
 * This is what makes "By machine" show capacity (machine -> Director -> session) rather than only the
 * Directors that happen to be busy. `directors` is the machine's reachable-Director list; a Director
 * already present via a session is never duplicated, and an unidentified Director (no id) is skipped
 * because it is not an addressable slot. Groups are ordered by Director id so a lane never reflows.
 */
export function groupByDirector(
  sessions: SessionDto[],
  sort: (a: SessionDto, b: SessionDto) => number,
  directors: DirectorReachability[] = [],
): DirectorGroup[] {
  const byDir = new Map<string, SessionDto[]>();
  for (const s of sessions) {
    const key = (s.directorId ?? "").trim();
    const arr = byDir.get(key);
    if (arr === undefined) byDir.set(key, [s]);
    else arr.push(s);
  }
  for (const d of directors) {
    const key = (d.directorId ?? "").trim();
    if (key.length === 0) continue; // an unidentified Director is not an addressable slot
    if (!byDir.has(key)) byDir.set(key, []); // idle Director -> empty group -> free slot
  }
  return [...byDir.entries()]
    .map(([key, arr]) => ({
      key: key.length === 0 ? "(unknown)" : key,
      label: `Director ${key.length === 0 ? "(unknown)" : shortDir(key)}`,
      sessions: [...arr].sort(sort),
    }))
    .sort((a, b) => a.key.localeCompare(b.key));
}

/** The agent label to show on a card's meta row, or null when the card must not show one. */
export function agentBadgeText(s: SessionDto, pivot: string): string | null {
  // The agent pivot's lane header already states the agent for every card in the lane; repeating it per
  // card is noise, and it was the reason the badge existed on the title row at all.
  if (pivot === "agent") return null;
  const agent = (s.agent ?? "").trim();
  // An unknown agent still renders "?" rather than vanishing: a card with no agent is a fact worth
  // seeing, and a silently absent badge reads as "this card is fine" (issue #1625).
  return agent.length === 0 ? "?" : agent;
}
