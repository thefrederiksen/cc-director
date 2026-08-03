// The roster keep-and-mark merge (mobile-resilience mission, Phase 2). The rule the owner asked for:
// never let a session vanish from the roster just because its machine became unreachable. When a
// Director reads Wobbly or Offline, its last-known sessions STAY on the roster, marked unreachable; when
// the machine answers again its live sessions replace them in place. This generalizes the shipped voice
// rule (#1334): unreachable is a state you SHOW, not data you DELETE.
//
// A SESSION LEAVES FOR TWO REASONS, NOT ONE. This summary used to say "ONLY when its owning Director
// ANSWERED and no longer reports it", and that stopped being true when the client clock was added - it
// then contradicted the detailed contract further down this same file, which is worse than either version
// alone because a reader who stops at the summary is confidently wrong. The two reasons are:
//
//  1. an ONLINE Director answered and did not report the session - the authoritative removal; or
//  2. the envelope stopped naming that Director AT ALL and the client retention horizon then elapsed -
//     see RETENTION_HORIZON_MS and `missingSince` below for exactly what that clock measures, which is
//     less than its name suggests.
//
// The second is a display-cache expiry, not an authority: it drops a card the Gateway has stopped
// mentioning, and the card returns from Gateway data the moment that machine is named again.
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
  REACHABILITY_STOPPED,
  REACHABILITY_ONLINE,
  REACHABILITY_WOBBLY,
  type DirectorReachability,
  type ReachabilityState,
  type SessionsEnvelope,
} from "./fleetClient";
import { directorStateLabel } from "./directorPresentation";

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
  /**
   * The Gateway's own word for the owning Director's condition ("Wobbly", "Offline", "Not running"), so a
   * surface can name the state instead of inferring it from `reachability`. Empty when the Gateway did not
   * say (an older Gateway, or an online Director).
   */
  stateLabel: string;
}

// The retention cache carried across polls by the caller (held in a ref). The last-known session list
// per owning Director id. Opaque to callers except through the pure functions below.
export interface RetentionCache {
  byDirector: Map<string, SessionDto[]>;
  /**
   * When each Director was FIRST OBSERVED MISSING - the time of the first successful envelope that
   * failed to name it - on THIS DEVICE'S clock (milliseconds). Absent from the map means "not currently
   * observed missing", which is the state of every Director the last envelope named.
   *
   * Inspection 2, finding 3. The first attempt at bounding retention treated a Director's ABSENCE from the
   * envelope as proof that the Gateway had evicted it past the horizon. It is not proof of anything: the
   * pushed store is in memory, so a RESTARTED Gateway serves a byte-identical empty envelope before its
   * Directors have reconnected and reseeded - and the phone would have deleted every card on the first
   * successful poll after a restart. That is a worse defect than the unbounded retention it replaced,
   * because it is instant and silent.
   *
   * Inspection 3, finding 2. The SECOND attempt stamped when a Director was last NAMED, and that has the
   * same failure by a different road. Time in which the phone observes nothing at all - a suspended but
   * still-mounted page, a long network or Gateway outage - ages a last-named stamp just as fast as time
   * in which the Director is genuinely absent, because the stamp measures elapsed wall clock rather than
   * evidence. So the first successful empty envelope after a restart could arrive with the stamp already
   * over the horizon and delete every card at once: precisely the failure the client clock was introduced
   * to prevent.
   *
   * So the stamp STARTS on the first successful envelope that omits the Director, and is CLEARED the
   * moment one names it again. One response can therefore never delete anything, which was the whole of
   * inspection 3's finding.
   *
   * WHAT THIS MEASURES, SAID PLAINLY, BECAUSE AN EARLIER VERSION OF THIS COMMENT OVERCLAIMED IT
   * (inspection 4, finding 1). It measures ELAPSED WALL TIME SINCE THE FIRST OBSERVED ABSENCE. It does
   * NOT measure observed absence, and it does not count observations or require continuous polling. Once
   * the stamp is set, a suspended page or a dead network ages it exactly as fast as real absence does,
   * and the first successful envelope after resuming can delete on the strength of time in which the
   * phone learned nothing. The earlier wording claimed the phone had "watched that Director be absent
   * across the horizon", and that is simply not what the code does. The fix moved the suspension hole
   * from "after the last named response" to "after the first omitted response"; it did not remove it.
   *
   * THE REASON THAT IS ACCEPTABLE, and the reason no observation-counting scheme is built here: deleting
   * a retained card is NOT DESTRUCTIVE. This cache is a display convenience, not a store of record.
   * Nothing is lost with it - the sessions live on the Gateway, and the very next envelope that names
   * that machine restores every one of its rows from Gateway data, which is better data than the copy
   * that was dropped. The worst case is a phone briefly showing fewer sessions after a resume until the
   * Directors re-Hello, which is seconds. Weigh that against an observation counter, which is real state,
   * on the client, that can itself be wrong in ways nothing here would notice.
   *
   * A test beside this file pins that recovery property, so "not destructive" is checked rather than
   * asserted in a comment.
   */
  missingSince: Map<string, number>;
}

/**
 * How long the phone keeps a Director's cards after the envelope stops naming it AT ALL.
 *
 * TEN MINUTES, NOT A DAY, and the size is the whole point (inspection 4, finding 2). This used to mirror
 * the Gateway's twenty-four-hour eviction horizon, on the reasoning that the two clocks should "agree
 * about how long a machine may be gone". They do not stack that way. While a machine is merely offline
 * the Gateway KEEPS NAMING it in the envelope, which clears this stamp on every poll, so this clock does
 * not even start until the Gateway's own horizon has already expired and it has stopped naming the
 * machine. A day here therefore ran a day AFTER the Gateway's day: a machine that left at midday was
 * still on the phone nearly forty-eight hours later, while the record promised the configured
 * twenty-four. The bound was real but it was not the documented one.
 *
 * What this clock is actually for is much smaller: telling a Gateway RESTART apart from a real eviction.
 * Both produce an envelope that names nobody, and the wire carries no eviction tombstone to distinguish
 * them, so the phone waits a little before believing the silence. A restart's pre-Hello window is
 * SECONDS - the Directors reconnect and reseed almost immediately - so ten minutes is already generous by
 * two orders of magnitude, and every extra minute buys nothing while pushing the real bound further past
 * what the record says.
 *
 * The total the owner sees is therefore the configured Gateway horizon plus ten minutes: the documented
 * day plus a rounding error, instead of a second day.
 */
export const RETENTION_HORIZON_MS = 10 * 60 * 1000;

export function emptyRetentionCache(): RetentionCache {
  return { byDirector: new Map<string, SessionDto[]>(), missingSince: new Map<string, number>() };
}

// The display roster produced by the merge: the sessions to render (live + retained-and-marked), in a
// stable order, plus a per-sessionId mark for the non-online ones (an online session has no entry).
export interface RetainedRoster {
  sessions: SessionDto[];
  marks: Map<string, RosterSessionMark>;
}

// THIS LIST IS THE ONE SOURCE FOR EVERY "MAY NAG" SURFACE - the row, the voice queue, AND the app-icon
// badge (inspection 3, finding 3).
//
// The mobile Home page rendered the row and built the voice queue from `roster.sessions` and then counted
// the badge from the RAW envelope. In a wobbly fallback - the Gateway names a connected-but-quiet Director
// and serves no rows for it - this list holds the retained, re-stamped-reachable card while the envelope
// holds nothing, so the card took its attention treatment and could enter the voice queue while the badge
// was explicitly cleared. Three surfaces that exist to say the same thing said two different things: the
// same shape as inspection 1's finding 2, one layer along.
//
// The count itself was deliberately NOT folded into this module, though that would have removed the
// caller's choice of source, because `needsYouBadgeCount` calls `classify`, which THROWS on a session the
// Gateway did not stamp. Counting here would move that throw ahead of the roster render, so one unstamped
// row would blank the whole screen rather than miscount a badge - trading a visible wrong number for an
// invisible empty phone. It fails in the wrong direction, so the caller keeps the call and this note keeps
// the reason.
//
// NOT PROVEN, and not softened: `apps/mobile` has no test harness (issue #1171), so nothing pins that the
// Home page reads THIS list for its badge. What is pinned, in the test beside this file, is that the two
// sources genuinely differ in the wobbly fallback and which of them the other surfaces agree with.

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
    // Carried through so a surface can print the Gateway's word rather than inferring one from the state
    // it was handed. Without it the phone read "Unreachable" beside a Director that had been shut down on
    // purpose - the false outage this change removes on the desktop, still live on the phone.
    stateLabel: directorStateLabel(reach),
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
export function mergeRosterRetention(
  prev: RetentionCache,
  envelope: SessionsEnvelope,
  nowMs: number = Date.now(),
): { cache: RetentionCache; roster: RetainedRoster } {
  const liveByDir = groupByDirector(envelope.sessions);
  const reachByDir = new Map<string, DirectorReachability>();
  for (const d of envelope.directors) reachByDir.set(d.directorId, d);

  const nextCache: RetentionCache = {
    byDirector: new Map(prev.byDirector),
    missingSince: new Map(prev.missingSince ?? []),
  };

  // Every Director this envelope NAMES - by serving rows for it or by carrying a reachability entry -
  // is no longer observed missing, so its stamp is CLEARED. A machine that comes back and goes away
  // again starts a fresh horizon rather than resuming an old one, which is the honest reading: what was
  // observed is a new absence, not a continuation of the previous one.
  for (const id of new Set<string>([...liveByDir.keys(), ...envelope.directors.map((d) => d.directorId)])) {
    if (id) nextCache.missingSince.delete(id);
  }
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

    // Not online. The mark carries the machine's OWN state through, unflattened. It used to collapse
    // everything that was not wobbly into "offline", which was harmless while offline was the only other
    // state and became a lie the moment "stopped" existed: a Director that announced its own shutdown was
    // marked unreachable, and the phone duly announced an outage about a machine that was fine.
    const markState: SessionReachability =
      state === REACHABILITY_WOBBLY
        ? REACHABILITY_WOBBLY
        : state === REACHABILITY_STOPPED
        ? REACHABILITY_STOPPED
        : REACHABILITY_OFFLINE;

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

    // Nothing served this poll. Whether that means "keep showing it" or "it is gone" is decided by the
    // Gateway's own semantics, not by a second clock on the client that could drift away from the one
    // that actually evicts (inspection 1, finding 3):
    //
    //  - The envelope NAMES this Director but served no rows: the Gateway still knows the machine, it
    //    just has nothing to say about it. Keep retaining - that is the whole point of the retention.
    //  - The envelope does not name it AT ALL: the Gateway has swept it past the eviction horizon and
    //    forgotten it. Drop it from the cache. Without this the phone re-injects its own copy forever,
    //    the machine never returns to supply an authoritative empty set, and the Gateway's BOUNDED
    //    retention becomes an UNBOUNDED per-page client retention - which made "sessions leave after the
    //    eviction horizon" false on the one surface the owner actually looks at.
    //
    // CORRECTED after inspection 2, finding 3. The rule above was wrong and the reasoning that produced it
    // is worth keeping visible: it read the Gateway's own semantics rather than inventing a client clock,
    // which sounded like the disciplined choice, and it made ABSENCE mean EVICTED. Absence means no such
    // thing. The pushed store is in memory and the wire carries no eviction tombstone, so a RESTARTED
    // Gateway produces exactly the same empty envelope before its Directors reconnect - and the phone would
    // have deleted every card on the first successful poll after a restart, instantly and silently. That is
    // worse than the unbounded retention it was fixing.
    //
    // CORRECTED AGAIN after inspection 3, finding 2. The stamp is taken on the first OMISSION rather than
    // on the last naming, so no single response can delete: the first omission only starts the clock, and
    // a deletion needs a second successful envelope, still not naming the Director, a horizon later.
    //
    // WHAT IT MEASURES, WITHOUT THE FLATTERY (inspection 4, finding 1). Elapsed wall time since that first
    // observed absence - NOT observed absence. Once the stamp is set, suspension and network loss age it
    // just as fast as real absence, so a resume can delete on time nobody watched. That hole is not
    // closed, it is moved: from "after the last named response" to "after the first omitted one".
    //
    // It is left open ON PURPOSE rather than fixed with an observation counter, because the deletion is
    // not a loss. This cache is a display convenience; the sessions live on the Gateway, and the next
    // envelope naming that machine restores every row from Gateway data. The cost of being wrong here is
    // a phone showing fewer cards for the seconds it takes Directors to re-Hello. An observation counter
    // would be new client state that can be wrong in ways nothing here would catch - a worse trade for a
    // recoverable symptom.
    //
    // A Director the envelope DOES name but serves no rows for is not missing at all: the Gateway still
    // knows the machine and is simply saying nothing about it. It is never stamped, so it retains until
    // the Gateway itself forgets it, which is the Gateway's horizon doing the work rather than a second
    // client clock racing it.
    const named = reach !== undefined || live !== undefined;
    if (!named) {
      const missingAt = nextCache.missingSince.get(id);
      if (missingAt === undefined) {
        nextCache.missingSince.set(id, nowMs);
      } else if (nowMs - missingAt > RETENTION_HORIZON_MS) {
        nextCache.byDirector.delete(id);
        nextCache.missingSince.delete(id);
        continue;
      }
    }

    const retained = prev.byDirector.get(id);
    if (retained && retained.length > 0) {
      for (const s of retained) {
        // RE-STAMPED with the machine's CURRENT reachability, not the value this row was cached with
        // (inspection 2, finding 2). A row cached while the machine was ONLINE carries
        // machineReachable=true, and this mission deliberately moved the attention treatment, the waiting
        // clock, the voice indicator and the voice queue onto that stamp - so re-injecting the row
        // untouched produced a card that LOOKED unreachable, dimmed and dated, while still nagging and
        // still promising it could speak. The mark said one thing and the row said another.
        //
        // Wobbly stays reachable, because a wobbly machine's tunnel is up and a command sent to it lands;
        // offline becomes false. That is the same two-flag rule the Gateway applies to rows it serves
        // itself, applied here to the rows it did not.
        sessions.push({ ...s, machineReachable: markState === REACHABILITY_WOBBLY });
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
