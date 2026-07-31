import { describe, expect, it } from "vitest";
import type { SessionDto } from "../api/client";
import {
  REACHABILITY_OFFLINE,
  REACHABILITY_ONLINE,
  REACHABILITY_WOBBLY,
  type DirectorReachability,
  type SessionsEnvelope,
} from "./fleetClient";
import { emptyRetentionCache, mergeRosterRetention, RETENTION_HORIZON_MS, type RetentionCache } from "./rosterRetention";

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

  // Epic #1159 step A: the Gateway now SERVES an unreachable machine's sessions instead of dropping them.
  // Those rows are real Gateway data and must win over anything held here - the offline branch used to
  // ignore them and re-inject the cache, which would have thrown away current data for a stale copy.
  it("prefers the Gateway's live rows over the cache for an OFFLINE Director, and still marks them", () => {
    // Poll 1: d1 online with s1 - cached.
    const first = mergeRosterRetention(emptyRetentionCache(), envelope([session("s1", "d1")], [director("d1", REACHABILITY_ONLINE)]));
    // Poll 2: d1 offline, but the Gateway serves its sessions anyway - and it now reports s2 as well,
    // which the client cache has never seen. The served set is what renders.
    const second = mergeRosterRetention(
      first.cache,
      envelope([session("s1", "d1"), session("s2", "d1")], [director("d1", REACHABILITY_OFFLINE, { lastSeenAgeSeconds: 90 })]),
    );
    expect(second.roster.sessions.map((s) => s.sessionId)).toEqual(["s1", "s2"]);
    // Served, not live: both rows are marked, so they render dimmed and dated rather than as normal rows.
    expect(second.roster.marks.get("s1")?.reachability).toBe(REACHABILITY_OFFLINE);
    expect(second.roster.marks.get("s2")?.reachability).toBe(REACHABILITY_OFFLINE);
    expect(second.roster.marks.get("s2")?.lastSeenLabel).toBe("last seen 1m ago");
    // The cache takes the served set, so a later poll that serves nothing still shows both.
    expect(second.cache.byDirector.get("d1")?.map((s) => s.sessionId)).toEqual(["s1", "s2"]);
  });

  // THE COLD START - the case that showed NOTHING before. The phone's retention cache lives in memory for
  // as long as the roster page is mounted: a relaunch, a reload, or simply opening a session and coming
  // back starts it empty. With a machine already unreachable at that moment there was nothing to retain
  // and no rows in the envelope either, so the roster came up blank. Now the Gateway serves the rows and
  // an empty cache costs nothing.
  it("shows an offline Director's Gateway-served rows on a COLD START, with an empty cache", () => {
    const { roster, cache } = mergeRosterRetention(
      emptyRetentionCache(),
      envelope([session("s1", "d1"), session("s2", "d1")], [director("d1", REACHABILITY_OFFLINE, { lastSeenAgeSeconds: 3600 })]),
    );
    expect(roster.sessions.map((s) => s.sessionId)).toEqual(["s1", "s2"]);
    expect(roster.marks.get("s1")?.reachability).toBe(REACHABILITY_OFFLINE);
    expect(roster.marks.get("s1")?.lastSeenLabel).toBe("last seen 1h ago");
    expect(cache.byDirector.get("d1")?.map((s) => s.sessionId)).toEqual(["s1", "s2"]);
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

  // Inspection 2, finding 3 - and this test has now been wrong TWICE, which is worth recording.
  //
  // First it demanded the card survive "indefinitely", which encoded the unbounded-retention defect.
  // Then it demanded the card be dropped the moment the envelope stopped NAMING the Director, which
  // read absence as proof of eviction - and a RESTARTED Gateway serves exactly that envelope before its
  // Directors reconnect, so the phone would have emptied itself on the first poll after any restart.
  // The wire carries no eviction tombstone, so absence proves nothing at all.
  //
  // What is true: the retention is bounded, and the bound is the PHONE'S OWN clock.
  it("keeps the cards through a Gateway restart, when the envelope suddenly names nobody", () => {
    const t0 = 1_000_000;
    const first = mergeRosterRetention(emptyRetentionCache(), envelope([session("s1", "d1")], [director("d1", REACHABILITY_ONLINE)]), t0);
    // The Gateway restarted: it answers successfully, but knows nothing yet.
    const second = mergeRosterRetention(first.cache, envelope([], []), t0 + 5_000);
    expect(second.roster.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
    expect(second.cache.byDirector.get("d1")?.map((s) => s.sessionId)).toEqual(["s1"]);
  });

  it("drops the cards once the Director has gone unnamed for longer than the retention horizon", () => {
    const t0 = 1_000_000;
    const first = mergeRosterRetention(emptyRetentionCache(), envelope([session("s1", "d1")], [director("d1", REACHABILITY_ONLINE)]), t0);
    const later = mergeRosterRetention(first.cache, envelope([], []), t0 + RETENTION_HORIZON_MS + 1);
    expect(later.roster.sessions).toEqual([]);
    expect(later.cache.byDirector.has("d1")).toBe(false);
  });

  // The other side of the boundary: while the Gateway still names the Director, the cards stay however
  // long it serves no rows. Being named refreshes the clock, so this never expires.
  it("keeps retaining while the Gateway still names the Director, however long it serves no rows", () => {
    let cache = mergeRosterRetention(emptyRetentionCache(), envelope([session("s1", "d1")], [director("d1", REACHABILITY_ONLINE)]), 0).cache;
    for (let poll = 0; poll < 50; poll++) {
      const at = poll * RETENTION_HORIZON_MS;   // far beyond the horizon, but it is NAMED every time
      const next = mergeRosterRetention(cache, envelope([], [director("d1", REACHABILITY_OFFLINE, { lastSeenAgeSeconds: 3600 })]), at);
      expect(next.roster.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
      cache = next.cache;
    }
  });

  // Inspection 2, finding 2. A row cached while the machine was ONLINE carries machineReachable=true.
  // Re-injecting it untouched produced a card that LOOKED unreachable - dimmed, dated - while still
  // nagging, still showing a waiting clock, and still promising it could speak, because the branch
  // deliberately moved all of those onto the Gateway's stamp.
  it("re-stamps a retained row as unreachable when its machine is offline", () => {
    const online = session("s1", "d1");
    (online as { machineReachable?: boolean }).machineReachable = true;
    const first = mergeRosterRetention(emptyRetentionCache(), envelope([online], [director("d1", REACHABILITY_ONLINE)]), 0);
    const offline = mergeRosterRetention(first.cache, envelope([], [director("d1", REACHABILITY_OFFLINE, { lastSeenAgeSeconds: 300 })]), 1000);
    const row = offline.roster.sessions.find((s) => s.sessionId === "s1");
    expect(row).toBeDefined();
    expect((row as { machineReachable?: boolean }).machineReachable).toBe(false);
  });

  // ...and a WOBBLY machine keeps its true stamp, because its tunnel is up and a command sent to it
  // lands. Without this the re-stamp would silence exactly the machine the mission set out to keep nagging.
  it("leaves a retained row reachable when its machine is only wobbly", () => {
    const online = session("s1", "d1");
    (online as { machineReachable?: boolean }).machineReachable = true;
    const first = mergeRosterRetention(emptyRetentionCache(), envelope([online], [director("d1", REACHABILITY_ONLINE)]), 0);
    const wobbly = mergeRosterRetention(first.cache, envelope([], [director("d1", REACHABILITY_WOBBLY, { lastSeenAgeSeconds: 45 })]), 1000);
    const row = wobbly.roster.sessions.find((s) => s.sessionId === "s1");
    expect((row as { machineReachable?: boolean }).machineReachable).toBe(true);
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
