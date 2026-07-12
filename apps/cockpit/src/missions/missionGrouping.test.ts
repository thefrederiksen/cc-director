import { describe, expect, it } from "vitest";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import { groupByMission, parseMissionName, stripBracketTag } from "./missionGrouping";

// Phase 1a of the Mission Screen (issue #1405) derives missions purely from the session name. These
// lock the two rules the Architect pinned before it was built: strip a leading bracket tag, and match
// on the LAST " - Role" - plus the non-role dash (#108 "mindzieWeb - remove Ask Mindzie") staying
// Standalone rather than becoming a bogus "mindzieWeb" mission.

describe("stripBracketTag", () => {
  it("removes one leading bracket tag and its trailing space", () => {
    expect(stripBracketTag("[Moving Started] Gateway Cleanup - Manager")).toBe(
      "Gateway Cleanup - Manager",
    );
    expect(stripBracketTag("[moving] Gateway Connection - Architect")).toBe(
      "Gateway Connection - Architect",
    );
  });

  it("leaves a name without a leading tag untouched", () => {
    expect(stripBracketTag("Car Mode - Architect")).toBe("Car Mode - Architect");
  });

  it("only strips a LEADING bracket - brackets elsewhere are kept", () => {
    expect(stripBracketTag("Car Mode [beta] - Manager")).toBe("Car Mode [beta] - Manager");
  });
});

describe("parseMissionName", () => {
  it("parses <Mission> - <Role> for each known role, case-insensitively", () => {
    expect(parseMissionName("Gateway Cleanup - Manager")).toEqual({
      mission: "Gateway Cleanup",
      role: "Manager",
    });
    expect(parseMissionName("Car Mode - architect")).toEqual({
      mission: "Car Mode",
      role: "Architect",
    });
    expect(parseMissionName("Docs - WORKER")).toEqual({ mission: "Docs", role: "Worker" });
  });

  it("strips a leading bracket tag before parsing (the move markers)", () => {
    expect(parseMissionName("[Moving Started] Gateway Cleanup - Manager")).toEqual({
      mission: "Gateway Cleanup",
      role: "Manager",
    });
    expect(parseMissionName("[moving] Gateway Connection - Architect")).toEqual({
      mission: "Gateway Connection",
      role: "Architect",
    });
  });

  it("matches the LAST ' - Role', so the mission keeps its own inner dash", () => {
    expect(parseMissionName("Foo - Bar - Manager")).toEqual({
      mission: "Foo - Bar",
      role: "Manager",
    });
  });

  it("returns null for a trailing token that is not a known role (stays Standalone)", () => {
    // The live #108 case: a dash, but the trailing text is not a role.
    expect(parseMissionName("mindzieWeb - remove Ask Mindzie")).toBeNull();
  });

  it("returns null when there is no ' - ' separator at all", () => {
    expect(parseMissionName("Mac Cleanup")).toBeNull();
    expect(parseMissionName("Working Mac")).toBeNull();
  });

  it("returns null for empty, whitespace, or missing names", () => {
    expect(parseMissionName("")).toBeNull();
    expect(parseMissionName("   ")).toBeNull();
    expect(parseMissionName(null)).toBeNull();
    expect(parseMissionName(undefined)).toBeNull();
  });

  it("returns null when the mission part is empty (only a role after the dash)", () => {
    expect(parseMissionName(" - Manager")).toBeNull();
  });
});

// A minimal SessionDto factory for the grouping tests - only the fields the parser and sort read.
function session(name: string, number: number, sessionId: string): SessionDto {
  return { name, number, sessionId } as unknown as SessionDto;
}

describe("groupByMission", () => {
  it("groups members under their mission and puts non-members in Standalone", () => {
    const { missions, standalone } = groupByMission([
      session("Gateway Cleanup - Manager", 102, "a"),
      session("Gateway Cleanup - Architect", 107, "b"),
      session("mindzieWeb - remove Ask Mindzie", 108, "c"),
      session("Working Mac", 101, "d"),
    ]);

    expect(missions).toHaveLength(1);
    expect(missions[0].name).toBe("Gateway Cleanup");
    expect(missions[0].members.map((m) => m.role)).toEqual(["Architect", "Manager"]);
    expect(standalone.map((s) => s.number)).toEqual([101, 108]);
  });

  it("sorts missions alphabetically (case-insensitive) and groups case-insensitively", () => {
    const { missions } = groupByMission([
      session("Zebra - Manager", 3, "a"),
      session("apple - Manager", 1, "b"),
      session("APPLE - Architect", 2, "c"),
    ]);

    expect(missions.map((m) => m.name)).toEqual(["apple", "Zebra"]);
    // "apple" and "APPLE" collapse into one mission (display = first seen).
    expect(missions[0].members).toHaveLength(2);
  });

  it("orders members Architect, Manager, Worker then by number", () => {
    const { missions } = groupByMission([
      session("Big - Worker", 50, "a"),
      session("Big - Manager", 20, "b"),
      session("Big - Architect", 30, "c"),
      session("Big - Worker", 10, "d"),
    ]);

    expect(missions[0].members.map((m) => `${m.role}:${m.session.number}`)).toEqual([
      "Architect:30",
      "Manager:20",
      "Worker:10",
      "Worker:50",
    ]);
  });

  it("groups mid-move sessions with their mission (bracket tag stripped)", () => {
    const { missions, standalone } = groupByMission([
      session("[Moving Started] Gateway Cleanup - Manager", 115, "a"),
      session("Gateway Cleanup - Architect", 107, "b"),
    ]);

    expect(standalone).toHaveLength(0);
    expect(missions).toHaveLength(1);
    expect(missions[0].name).toBe("Gateway Cleanup");
    expect(missions[0].members).toHaveLength(2);
  });
});
