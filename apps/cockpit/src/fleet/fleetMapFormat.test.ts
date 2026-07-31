import { describe, expect, it } from "vitest";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import {
  REACHABILITY_OFFLINE,
  REACHABILITY_ONLINE,
  REACHABILITY_WOBBLY,
  type DirectorReachability,
} from "@devthrottle/client-core/fleet/fleetClient";
import {
  agentBadgeText,
  buildControllerTree,
  directorLabelOf,
  directorsByMachine,
  groupByDirector,
  machineKeyOf,
} from "./fleetMapFormat";

function session(overrides: Partial<SessionDto> = {}): SessionDto {
  return { sessionId: "s1", agent: "ClaudeCode", activityState: "Working", ...overrides } as SessionDto;
}

// A child of `controller`. The two fields travel together on the wire, so the fixture keeps them together.
function child(sessionId: string, controllerSessionId: string, overrides: Partial<SessionDto> = {}): SessionDto {
  return session({ sessionId, isControlled: true, controllerSessionId, ...overrides });
}

// Order roots/siblings by session id, so the assertions below are about the TREE and not about sorting.
const byId = (a: SessionDto, b: SessionDto): number => (a.sessionId ?? "").localeCompare(b.sessionId ?? "");

// The flattened tree as "id@depth", which is exactly what the view renders.
function shape(sessions: SessionDto[]): string[] {
  return buildControllerTree(sessions, byId).map((n) => `${n.session.sessionId}@${n.depth}`);
}

describe("buildControllerTree", () => {
  it("nests a controlled session under its controller", () => {
    expect(shape([child("b", "a"), session({ sessionId: "a" })])).toEqual(["a@0", "b@1"]);
  });

  it("nests deeper than two levels - nesting is real, the depth is not capped", () => {
    const fleet = [
      session({ sessionId: "a" }),
      child("b", "a"),
      child("c", "b"),
      child("d", "c"),
      child("e", "d"),
    ];
    expect(shape(fleet)).toEqual(["a@0", "b@1", "c@2", "d@3", "e@4"]);
  });

  it("keeps each parent's children together, depth-first", () => {
    const fleet = [
      session({ sessionId: "arch" }),
      child("m1", "arch"),
      child("m2", "arch"),
      child("w1", "m1"),
      child("w2", "m2"),
    ];
    expect(shape(fleet)).toEqual(["arch@0", "m1@1", "w1@2", "m2@1", "w2@2"]);
  });

  it("puts a child at the top level when its controller is not in this lane", () => {
    // The pivots slice the fleet, so a Worker's Manager can be filtered out of the lane entirely.
    expect(shape([child("b", "elsewhere")])).toEqual(["b@0"]);
  });

  it("does not indent under an exited controller", () => {
    // FleetRoleResolver already demotes a session whose controller exited; indenting under the corpse
    // would say the opposite of what the roster says.
    const fleet = [session({ sessionId: "a", activityState: "Exited" }), child("b", "a")];
    expect(shape(fleet)).toEqual(["a@0", "b@0"]);
  });

  it("treats a self-referencing session as its own root", () => {
    expect(shape([child("a", "a")])).toEqual(["a@0"]);
  });

  it("does not hang or lose cards on a cycle", () => {
    // Neither member of a cycle can reach a root, so BOTH are promoted to roots and render flat. That
    // is the point: a cycle should never be able to hang the view or swallow a card, and a flat pair is
    // an honest rendering of a relationship that does not actually have a top.
    const fleet = [child("a", "b"), child("b", "a"), session({ sessionId: "c" })];
    const out = shape(fleet);
    expect(out).toHaveLength(3);
    expect([...out].sort()).toEqual(["a@0", "b@0", "c@0"]);
  });

  it("renders every session exactly once - a lost card is worse than a misindented one", () => {
    const fleet = [
      session({ sessionId: "a" }),
      child("b", "a"),
      child("c", "b"),
      child("d", "gone"),
      session({ sessionId: "e", activityState: "Exited" }),
      child("f", "e"),
    ];
    const out = buildControllerTree(fleet, byId);
    expect(out).toHaveLength(6);
    expect(new Set(out.map((n) => n.session.sessionId)).size).toBe(6);
  });

  it("ignores a controller id when isControlled is not set", () => {
    // isControlled is the fact; a stale controller id without it is not an edge.
    const fleet = [session({ sessionId: "a" }), session({ sessionId: "b", controllerSessionId: "a" })];
    expect(shape(fleet)).toEqual(["a@0", "b@0"]);
  });

  it("returns nothing for an empty lane", () => {
    expect(buildControllerTree([], byId)).toEqual([]);
  });
});

function director(overrides: Partial<DirectorReachability> = {}): DirectorReachability {
  return { directorId: "d1", machineName: "SOREN", state: REACHABILITY_ONLINE, ...overrides };
}

describe("machineKeyOf", () => {
  it("keys a session and a Director with the same machine name to the same lane", () => {
    // The whole join depends on this: a session on "SOREN" and a Director advertising " soren " must
    // collapse onto one key, or the idle Director would open a second, empty "SOREN" lane.
    expect(machineKeyOf("SOREN").key).toBe(machineKeyOf(" soren ").key);
  });

  it("falls back to (unknown machine) when the name is blank", () => {
    expect(machineKeyOf("").title).toBe("(unknown machine)");
    expect(machineKeyOf(null).title).toBe("(unknown machine)");
    expect(machineKeyOf(undefined).title).toBe("(unknown machine)");
  });
});

describe("directorsByMachine", () => {
  it("groups Directors by their machine key", () => {
    const out = directorsByMachine([
      director({ directorId: "a", machineName: "SOREN" }),
      director({ directorId: "b", machineName: "SOREN" }),
      director({ directorId: "c", machineName: "MAC" }),
    ]);
    const soren = out.find((m) => m.key === "soren");
    const mac = out.find((m) => m.key === "mac");
    expect(soren?.directors.map((d) => d.directorId)).toEqual(["a", "b"]);
    expect(mac?.directors.map((d) => d.directorId)).toEqual(["c"]);
  });

  it("INCLUDES an offline Director - an unreachable machine is dimmed on the map, never dropped from it", () => {
    // This used to drop the offline entry, which deleted the machine from the Fleet Map entirely once its
    // tunnel went down. The Gateway now serves that machine's sessions with their age, so the map has to
    // show it: the state and the last-seen line are what the sub-header renders instead of a free slot.
    const out = directorsByMachine([
      director({ directorId: "on", machineName: "M", state: REACHABILITY_ONLINE }),
      director({ directorId: "wob", machineName: "M", state: REACHABILITY_WOBBLY }),
      director({ directorId: "off", machineName: "M", state: REACHABILITY_OFFLINE }),
    ]);
    const m = out.find((x) => x.key === "m");
    expect(m?.directors.map((d) => d.directorId)).toEqual(["on", "wob", "off"]);
  });

  it("gives an offline-only machine a lane of its own, so the machine still appears", () => {
    const out = directorsByMachine([
      director({ directorId: "off", machineName: "ASLEEP", state: REACHABILITY_OFFLINE }),
    ]);
    expect(out.map((m) => m.title)).toEqual(["ASLEEP"]);
  });
});

describe("groupByDirector", () => {
  it("folds an idle Director in as an empty group - a free slot", () => {
    const sessions = [session({ sessionId: "s1", directorId: "busy" })];
    const out = groupByDirector(sessions, byId, [
      director({ directorId: "busy" }),
      director({ directorId: "idle" }),
    ]);
    const busy = out.find((g) => g.key === "busy");
    const idle = out.find((g) => g.key === "idle");
    expect(busy?.sessions.map((s) => s.sessionId)).toEqual(["s1"]);
    expect(idle?.sessions).toEqual([]); // idle Director renders as a free slot
  });

  it("does not duplicate a Director that already has sessions", () => {
    const sessions = [session({ sessionId: "s1", directorId: "d1" })];
    const out = groupByDirector(sessions, byId, [director({ directorId: "d1" })]);
    expect(out.filter((g) => g.key === "d1")).toHaveLength(1);
    expect(out[0].sessions).toHaveLength(1);
  });

  it("skips a Director with no id - it is not an addressable slot", () => {
    const out = groupByDirector([], byId, [director({ directorId: "" })]);
    expect(out).toEqual([]);
  });

  it("folds in an OFFLINE Director and carries its state, so the panel can dim and date it", () => {
    const out = groupByDirector([], byId, [
      director({ directorId: "off", state: REACHABILITY_OFFLINE, lastSeenAgeSeconds: 400 }),
    ]);
    expect(out.map((g) => g.key)).toEqual(["off"]);
    expect(out[0].reachability?.state).toBe(REACHABILITY_OFFLINE);
    expect(out[0].reachability?.lastSeenAgeSeconds).toBe(400);
  });

  it("carries the state onto a group that already has sessions, without duplicating it", () => {
    const sessions = [session({ sessionId: "s1", directorId: "d1" })];
    const out = groupByDirector(sessions, byId, [director({ directorId: "d1", state: REACHABILITY_OFFLINE })]);
    expect(out).toHaveLength(1);
    expect(out[0].sessions.map((s) => s.sessionId)).toEqual(["s1"]);
    expect(out[0].reachability?.state).toBe(REACHABILITY_OFFLINE);
  });

  it("groups sessions by Director when no idle list is given", () => {
    const sessions = [
      session({ sessionId: "s1", directorId: "a" }),
      session({ sessionId: "s2", directorId: "a" }),
      session({ sessionId: "s3", directorId: "b" }),
    ];
    const out = groupByDirector(sessions, byId);
    expect(out.map((g) => g.key)).toEqual(["a", "b"]);
    expect(out[0].sessions).toHaveLength(2);
  });

  it("labels a group with the Director's display name when the envelope reports one (devthrottle_internal#1176)", () => {
    const sessions = [session({ sessionId: "s1", directorId: "d1" })];
    const out = groupByDirector(sessions, byId, [
      director({ directorId: "d1", displayName: "SOREN_NORTH_SLOT_2" }),
    ]);
    expect(out[0].label).toBe("SOREN_NORTH_SLOT_2");
  });
});

describe("directorLabelOf", () => {
  it("prefers the user-editable display name", () => {
    expect(directorLabelOf("abcd1234-guid", director({ displayName: "SOREN_NORTH_SLOT_2" }))).toBe(
      "SOREN_NORTH_SLOT_2",
    );
  });

  it("falls back to the historical short-id label when unnamed or when reachability is missing", () => {
    // An unnamed Director (or one behind an older Gateway that strips the field) must render exactly
    // as it always did - the display name is additive, never a regression.
    expect(directorLabelOf("32c4851e", director({ displayName: "" }))).toBe("Director 32c4851e");
    expect(directorLabelOf("32c4851e", undefined)).toBe("Director 32c4851e");
  });

  it("ignores a whitespace-only display name", () => {
    expect(directorLabelOf("32c4851e", director({ displayName: "   " }))).toBe("Director 32c4851e");
  });

  it("labels an empty id as unknown", () => {
    expect(directorLabelOf("", undefined)).toBe("Director (unknown)");
  });
});

describe("agentBadgeText", () => {
  it("shows the agent on the machine, repo, and list pivots", () => {
    for (const pivot of ["machine", "repo", "list"]) {
      expect(agentBadgeText(session(), pivot)).toBe("ClaudeCode");
    }
  });

  it("shows nothing on the agent pivot, where the lane header already states it", () => {
    expect(agentBadgeText(session(), "agent")).toBeNull();
  });

  it("shows a question mark rather than nothing when the agent is unknown", () => {
    expect(agentBadgeText(session({ agent: "" }), "machine")).toBe("?");
    expect(agentBadgeText(session({ agent: "   " }), "machine")).toBe("?");
    expect(agentBadgeText(session({ agent: undefined }), "machine")).toBe("?");
  });
});
