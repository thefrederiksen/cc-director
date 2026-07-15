import { describe, expect, it } from "vitest";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import { agentBadgeText, buildControllerTree } from "./fleetMapFormat";

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
