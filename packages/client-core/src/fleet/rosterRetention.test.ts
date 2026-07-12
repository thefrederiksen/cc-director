import { describe, expect, it } from "vitest";
import type { SessionDto } from "../api/client";
import {
  REACHABILITY_OFFLINE,
  REACHABILITY_ONLINE,
  REACHABILITY_WOBBLY,
  type DirectorReachability,
  type SessionsEnvelope,
} from "./fleetClient";
import { emptyRetentionCache, mergeRosterRetention, type RetentionCache } from "./rosterRetention";

// Minimal SessionDto fixtures: the merge only routes sessions by id/director/machine, so the triage and
// color fields the ordering helpers need are irrelevant here.
function session(sessionId: string, directorId: string, machineName = "MACHINE_A"): SessionDto {
  return { sessionId, directorId, machineName } as unknown as SessionDto;
}

function director(directorId: string, state: DirectorReachability["state"], extra: Partial<DirectorReachability> = {}): DirectorReachability {
  return { directorId, state, machineName: "MACHINE_A", ...extra };
}

function envelope(sessions: SessionDto[], directors: DirectorReachability[] = []): SessionsEnvelope {
  return { sessions, machineErrors: [], directors };
}

describe("roster keep-and-mark retention merge", () => {
  it("renders live sessions of an online Director normally, with no mark", () => {
    const cache = emptyRetentionCache();
    const env = envelope([session("s1", "d1"), session("s2", "d1")], [director("d1", REACHABILITY_ONLINE)]);
    const { roster } = mergeRosterRetention(cache, env);
    expect(roster.sessions.map((s) => s.sessionId)).toEqual(["s1", "s2"]);
    expect(roster.marks.size).toBe(0);
  });

  it("treats a Director with live sessions and NO reachability entry as online (older Gateway)", () => {
    const { roster, cache } = mergeRosterRetention(emptyRetentionCache(), envelope([session("s1", "d1")], []));
    expect(roster.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
    expect(roster.marks.size).toBe(0);
    expect(cache.byDirector.get("d1")?.map((s) => s.sessionId)).toEqual(["s1"]);
  });

  it("keeps a wobbly Director's served sessions on the roster, marked wobbly", () => {
    const env = envelope([session("s1", "d1")], [director("d1", REACHABILITY_WOBBLY, { lastSeenAgeSeconds: 12 })]);
    const { roster } = mergeRosterRetention(emptyRetentionCache(), env);
    expect(roster.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
    const mark = roster.marks.get("s1");
    expect(mark?.reachability).toBe(REACHABILITY_WOBBLY);
    expect(mark?.machineName).toBe("MACHINE_A");
    expect(mark?.lastSeenLabel).toBe("last seen 12s ago");
  });

  it("keeps an offline Director's sessions from the cache after the Gateway drops them, marked offline", () => {
    // First poll: d1 online with s1 - cached.
    const first = mergeRosterRetention(emptyRetentionCache(), envelope([session("s1", "d1")], [director("d1", REACHABILITY_ONLINE)]));
    // Second poll: d1 offline, its session dropped from the envelope's sessions[] (only a directors entry).
    const second = mergeRosterRetention(first.cache, envelope([], [director("d1", REACHABILITY_OFFLINE, { lastSeenAgeSeconds: 130 })]));
    expect(second.roster.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
    const mark = second.roster.marks.get("s1");
    expect(mark?.reachability).toBe(REACHABILITY_OFFLINE);
    expect(mark?.lastSeenLabel).toBe("last seen 2m ago");
  });

  it("keeps sessions even after the Gateway forgets the Director entirely (no entry, no live sessions)", () => {
    const first = mergeRosterRetention(emptyRetentionCache(), envelope([session("s1", "d1")], [director("d1", REACHABILITY_ONLINE)]));
    // The Gateway has forgotten d1 completely: not in sessions[], not in directors[].
    const second = mergeRosterRetention(first.cache, envelope([], []));
    expect(second.roster.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
    expect(second.roster.marks.get("s1")?.reachability).toBe(REACHABILITY_OFFLINE);
    // The card is retained indefinitely - the cache still holds it.
    expect(second.cache.byDirector.get("d1")?.map((s) => s.sessionId)).toEqual(["s1"]);
  });

  it("removes a session only on an authoritative online answer, never while unreachable", () => {
    // Poll 1: d1 online with s1 and s2.
    let cache: RetentionCache = emptyRetentionCache();
    cache = mergeRosterRetention(cache, envelope([session("s1", "d1"), session("s2", "d1")], [director("d1", REACHABILITY_ONLINE)])).cache;
    // Poll 2: d1 goes offline - both sessions retained (no authority to remove).
    const offline = mergeRosterRetention(cache, envelope([], [director("d1", REACHABILITY_OFFLINE)]));
    expect(offline.roster.sessions.map((s) => s.sessionId).sort()).toEqual(["s1", "s2"]);
    // Poll 3: d1 answers online but now only reports s1 - s2 was genuinely killed, so it drops.
    const back = mergeRosterRetention(offline.cache, envelope([session("s1", "d1")], [director("d1", REACHABILITY_ONLINE)]));
    expect(back.roster.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
    expect(back.roster.marks.size).toBe(0);
    expect(back.cache.byDirector.get("d1")?.map((s) => s.sessionId)).toEqual(["s1"]);
  });

  it("replaces retained cards in place when the machine comes back online", () => {
    const first = mergeRosterRetention(emptyRetentionCache(), envelope([session("s1", "d1")], [director("d1", REACHABILITY_ONLINE)]));
    const offline = mergeRosterRetention(first.cache, envelope([], [director("d1", REACHABILITY_OFFLINE)]));
    expect(offline.roster.marks.get("s1")?.reachability).toBe(REACHABILITY_OFFLINE);
    const online = mergeRosterRetention(offline.cache, envelope([session("s1", "d1")], [director("d1", REACHABILITY_ONLINE)]));
    expect(online.roster.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
    expect(online.roster.marks.size).toBe(0);
  });

  it("never retains a session with no owning Director id (live-only)", () => {
    const first = mergeRosterRetention(emptyRetentionCache(), envelope([session("s1", "")], []));
    expect(first.roster.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
    expect(first.cache.byDirector.has("")).toBe(false);
    // Next poll with no sessions: the no-director session is simply gone (not retained).
    const second = mergeRosterRetention(first.cache, envelope([], []));
    expect(second.roster.sessions).toHaveLength(0);
  });

  it("keeps a reachable Director's sessions while a DIFFERENT machine is offline", () => {
    const first = mergeRosterRetention(
      emptyRetentionCache(),
      envelope(
        [session("s1", "d1", "MACHINE_A"), session("s2", "d2", "MACHINE_B")],
        [director("d1", REACHABILITY_ONLINE), director("d2", REACHABILITY_ONLINE)],
      ),
    );
    // d2 goes offline; d1 stays online with a fresh session s3.
    const second = mergeRosterRetention(
      first.cache,
      envelope(
        [session("s1", "d1", "MACHINE_A"), session("s3", "d1", "MACHINE_A")],
        [director("d1", REACHABILITY_ONLINE), director("d2", REACHABILITY_OFFLINE, { machineName: "MACHINE_B" })],
      ),
    );
    const ids = second.roster.sessions.map((s) => s.sessionId).sort();
    expect(ids).toEqual(["s1", "s2", "s3"]);
    expect(second.roster.marks.get("s2")?.reachability).toBe(REACHABILITY_OFFLINE);
    expect(second.roster.marks.get("s2")?.machineName).toBe("MACHINE_B");
    expect(second.roster.marks.has("s1")).toBe(false);
    expect(second.roster.marks.has("s3")).toBe(false);
  });

  it("is idempotent: applying the same envelope twice yields the same cache and roster", () => {
    const env = envelope([session("s1", "d1")], [director("d1", REACHABILITY_WOBBLY, { lastSeenAgeSeconds: 5 })]);
    const once = mergeRosterRetention(emptyRetentionCache(), env);
    const twice = mergeRosterRetention(once.cache, env);
    expect(twice.roster.sessions.map((s) => s.sessionId)).toEqual(once.roster.sessions.map((s) => s.sessionId));
    expect([...twice.cache.byDirector.keys()]).toEqual([...once.cache.byDirector.keys()]);
    expect(twice.cache.byDirector.get("d1")?.map((s) => s.sessionId)).toEqual(["s1"]);
  });

  it("does not mutate the previous cache (pure merge)", () => {
    const first = mergeRosterRetention(emptyRetentionCache(), envelope([session("s1", "d1")], [director("d1", REACHABILITY_ONLINE)]));
    const snapshot = [...(first.cache.byDirector.get("d1") ?? [])];
    // A later online answer that drops s1 must not retroactively change the earlier cache object.
    mergeRosterRetention(first.cache, envelope([], [director("d1", REACHABILITY_ONLINE)]));
    expect(first.cache.byDirector.get("d1")).toEqual(snapshot);
  });
});
