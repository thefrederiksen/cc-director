// The roster keep-and-mark merge (mobile-resilience mission, Phase 2). The rule the owner asked for:
// never let a session vanish from the roster just because its machine became unreachable - a session
// leaves the list ONLY when its owning Director ANSWERED (is Online) and no longer reports it. When a
// Director reads Wobbly or Offline, its last-known sessions STAY on the roster, marked unreachable, for
// as long as the machine stays unreachable; when the machine answers again its live sessions replace
// them in place. This generalizes the shipped voice rule (#1334): unreachable is a state you SHOW, not
// data you DELETE.
//
// Why a client-side retention cache is needed on top of the Gateway envelope: the Gateway's
// FleetRosterCache (#1215) serves a failing Director's last-known-good sessions for only a few poll
// cycles (state "wobbly"), then declares it "offline" and DROPS its sessions from the envelope's
// sessions[] - keeping only a per-Director "offline" entry in directors[]. So to keep showing those
// cards past the grace window, this module retains the last-known sessions per Director and re-injects
// them, marked, using the envelope's per-Director state as the honest signal. It invents no shrink
// heuristic (decision 4): removal is driven only by an Online Director's authoritative answer.
//
// The merge is a PURE function - it takes the previous cache and the new envelope and returns a fresh
// cache plus the display roster, mutating nothing - so the mobile page can hold the cache in a ref and
// the whole rule is unit-testable (a StrictMode double-invoke is safe: applying the same envelope twice
// yields the same cache).
import type { SessionDto } from "../api/client";
import {
  reachabilityLastSeen,
  REACHABILITY_OFFLINE,
  REACHABILITY_ONLINE,
  REACHABILITY_WOBBLY,
  type DirectorReachability,
  type ReachabilityState,
  type SessionsEnvelope,
} from "./fleetClient";

// How one session should be rendered on the roster. "online" sessions render normally (no mark); a
// "wobbly" or "offline" session is grayed and carries a short plain note naming the machine and, when
// known, how long ago it was last seen.
export type SessionReachability = ReachabilityState;

export interface RosterSessionMark {
  reachability: SessionReachability;
  /** Best-effort machine name for the note (the Director's, else the session's own stamp). */
  machineName: string;
  /** "last seen Ns ago" style age, or "" when unknown. */
  lastSeenLabel: string;
}

// The retention cache carried across polls by the caller (held in a ref). The last-known session list
// per owning Director id. Opaque to callers except through the pure functions below.
export interface RetentionCache {
  byDirector: Map<string, SessionDto[]>;
}

export function emptyRetentionCache(): RetentionCache {
  return { byDirector: new Map<string, SessionDto[]>() };
}

// The display roster produced by the merge: the sessions to render (live + retained-and-marked), in a
// stable order, plus a per-sessionId mark for the non-online ones (an online session has no entry).
export interface RetainedRoster {
  sessions: SessionDto[];
  marks: Map<string, RosterSessionMark>;
}

// The owning Director id for a session, or "" when the Gateway did not stamp one (an older session, or
// a purely local Director). "" sessions are never retained per-Director - they render live-only.
function directorIdOf(s: SessionDto): string {
  return (s.directorId ?? "").trim();
}

// Group the envelope's live sessions by owning Director id, preserving the Gateway's order within each
// group so the roster does not reshuffle between polls.
function groupByDirector(sessions: SessionDto[]): Map<string, SessionDto[]> {
  const byDir = new Map<string, SessionDto[]>();
  for (const s of sessions) {
    const id = directorIdOf(s);
    const list = byDir.get(id);
    if (list) list.push(s);
    else byDir.set(id, [s]);
  }
  return byDir;
}

function markFor(state: SessionReachability, machineName: string, reach: DirectorReachability | undefined): RosterSessionMark {
  return {
    reachability: state,
    machineName,
    lastSeenLabel: reachabilityLastSeen(reach?.lastSeenAgeSeconds),
  };
}

// Merge a fresh roster envelope with the retained last-known cache and produce the display roster.
//
// The authority rule, per owning Director:
//  - ONLINE (its state is "online", OR it has live sessions this poll and no reachability entry at all
//    - a fully-online Director the Gateway omits from directors[]): the Director answered. Its live
//    session set is authoritative - it REPLACES the cache for that Director, so a session genuinely
//    killed while the machine was reachable disappears. These render normally (no mark).
//  - WOBBLY: the Gateway is still serving this Director's last-known sessions (within its grace window).
//    They are present in the envelope; refresh the cache with them and mark them wobbly. Never pruned -
//    the Director did not answer, so its absence of a session is not authority to remove it.
//  - OFFLINE / vanished: the Gateway dropped this Director's sessions from the envelope (grace exhausted)
//    or forgot the Director entirely. Re-inject the retained cache sessions, marked offline, and keep the
//    cache untouched - the cards stay until the machine answers again (decision 5).
export function mergeRosterRetention(prev: RetentionCache, envelope: SessionsEnvelope): { cache: RetentionCache; roster: RetainedRoster } {
  const liveByDir = groupByDirector(envelope.sessions);
  const reachByDir = new Map<string, DirectorReachability>();
  for (const d of envelope.directors) reachByDir.set(d.directorId, d);

  const nextCache: RetentionCache = { byDirector: new Map(prev.byDirector) };
  const sessions: SessionDto[] = [];
  const marks = new Map<string, RosterSessionMark>();

  // The full set of Director ids to consider: everyone with live sessions this poll, everyone the
  // envelope reports a reachability for, and everyone we still hold retained sessions for.
  const directorIds = new Set<string>([
    ...liveByDir.keys(),
    ...envelope.directors.map((d) => d.directorId),
    ...prev.byDirector.keys(),
  ]);

  for (const id of directorIds) {
    const reach = id ? reachByDir.get(id) : undefined;
    const live = liveByDir.get(id);
    // A session with no owning Director id ("") is only ever live - render it as-is, never retained.
    if (id === "") {
      if (live) sessions.push(...live);
      continue;
    }
    const state: ReachabilityState = reach?.state ?? (live ? REACHABILITY_ONLINE : REACHABILITY_OFFLINE);

    if (state === REACHABILITY_ONLINE) {
      // Authoritative answer: the live set replaces the cache (killed sessions drop), rendered normally.
      const list = live ?? [];
      if (list.length > 0) nextCache.byDirector.set(id, list);
      else nextCache.byDirector.delete(id);
      sessions.push(...list);
      continue;
    }

    if (state === REACHABILITY_WOBBLY && live && live.length > 0) {
      // Still served stale within the Gateway's grace window: refresh the cache, mark wobbly, never prune.
      nextCache.byDirector.set(id, live);
      for (const s of live) {
        sessions.push(s);
        marks.set(s.sessionId ?? "", markFor(REACHABILITY_WOBBLY, machineLabel(s, reach), reach));
      }
      continue;
    }

    // Offline (or wobbly with nothing served this poll, or a Director the Gateway forgot): re-inject the
    // retained cache, marked unreachable, and leave the cache untouched so the cards persist.
    const retained = prev.byDirector.get(id);
    if (retained && retained.length > 0) {
      const markState: SessionReachability = state === REACHABILITY_WOBBLY ? REACHABILITY_WOBBLY : REACHABILITY_OFFLINE;
      for (const s of retained) {
        sessions.push(s);
        marks.set(s.sessionId ?? "", markFor(markState, machineLabel(s, reach), reach));
      }
    }
  }

  return { cache: nextCache, roster: { sessions, marks } };
}

// The machine label for a mark: prefer the Director's reported machine name, fall back to the session's
// own stamp, so the note always names a machine even when one source is blank.
function machineLabel(s: SessionDto, reach: DirectorReachability | undefined): string {
  const fromDirector = (reach?.machineName ?? "").trim();
  if (fromDirector.length > 0) return fromDirector;
  return (s.machineName ?? "").trim();
}
