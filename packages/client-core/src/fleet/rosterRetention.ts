// The roster keep-and-mark merge (mobile-resilience mission, Phase 2). The rule the owner asked for:
// never let a session vanish from the roster just because its machine became unreachable - a session
// leaves the list ONLY when its owning Director ANSWERED (is Online) and no longer reports it. When a
// Director reads Wobbly or Offline, its last-known sessions STAY on the roster, marked unreachable, for
// as long as the machine stays unreachable; when the machine answers again its live sessions replace
// them in place. This generalizes the shipped voice rule (#1334): unreachable is a state you SHOW, not
// data you DELETE.
//
// THE GATEWAY IS NOW THE PRIMARY SOURCE FOR AN UNREACHABLE MACHINE; THIS CACHE IS THE FALLBACK.
//
// This module was written against a Gateway that DELETED: its FleetRosterCache (#1215) served a failing
// Director's last-known-good sessions for a few poll cycles (state "wobbly"), then declared it "offline"
// and dropped its sessions from the envelope's sessions[], keeping only a per-Director "offline" entry
// in directors[]. Past the grace window there was nothing to render, so this module kept its own copy
// and re-injected it.
//
// Epic #1159 step A changed that: GET /sessions now serves every session of a machine it cannot reach,
// unconditionally, marked with how old the information is. So for an offline Director the envelope
// usually carries REAL, current-as-of-the-last-push rows - and they must win. The offline branch used to
// ignore the live rows entirely and re-inject the cache, which after the Gateway change would have
// thrown away Gateway-served data in favour of a client copy, and on a COLD START (an empty cache) would
// have shown nothing at all for a machine that was already offline when the app opened.
//
// The cache survives because the Gateway can still legitimately carry no rows for a Director - it was
// swept past the eviction horizon, the tenant read returned it with an empty set, or an older Gateway is
// on the other end - and a card must not vanish for that. Live rows are the answer when they exist; the
// cache answers only when they do not. Either way the rows are MARKED, so they render dimmed and dated.
//
// It invents no shrink heuristic (decision 4): removal is driven only by an Online Director's
// authoritative answer.
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
//  - WOBBLY or OFFLINE **with rows in the envelope**: the Gateway is serving this machine's last-known
//    sessions (it does so unconditionally now, for both states). Those rows are the answer - they are
//    Gateway data and they are newer than anything held here - so refresh the cache with them and mark
//    them with the machine's state. Never pruned: the Director did not answer, so the absence of a
//    session from the set is not authority to remove it.
//  - WOBBLY or OFFLINE **with no rows**, or a Director the Gateway forgot entirely: nothing was served,
//    so re-inject the retained cache sessions, marked, and keep the cache untouched - the cards stay
//    until the machine answers again (decision 5).
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

    // Not online. The mark is the machine's own state - wobbly reads as "reconnecting", anything else as
    // "unreachable" - and it is applied whether the rows came from the Gateway or from this cache.
    const markState: SessionReachability = state === REACHABILITY_WOBBLY ? REACHABILITY_WOBBLY : REACHABILITY_OFFLINE;

    if (live && live.length > 0) {
      // THE GATEWAY SERVED ROWS FOR AN UNREACHABLE MACHINE, so those rows are the answer - for offline
      // exactly as for wobbly. Refresh the cache with them and mark them; never prune, because the
      // Director did not answer and its silence is not authority to remove anything. Preferring the cache
      // here would discard current Gateway data for a stale client copy, and would show NOTHING at all on
      // a cold start (empty cache) for a machine that was already unreachable when the app opened.
      nextCache.byDirector.set(id, live);
      for (const s of live) {
        sessions.push(s);
        marks.set(s.sessionId ?? "", markFor(markState, machineLabel(s, reach), reach));
      }
      continue;
    }

    // Nothing served this poll (an older Gateway that still drops an offline machine's sessions, a
    // Director swept past the eviction horizon, or one the Gateway forgot): re-inject the retained cache,
    // marked, and leave the cache untouched so the cards persist.
    const retained = prev.byDirector.get(id);
    if (retained && retained.length > 0) {
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
