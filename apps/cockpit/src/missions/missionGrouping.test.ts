import { describe, expect, it } from "vitest";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import type { MissionDto } from "@devthrottle/client-core/missions/missions";
import { displayRole, groupByMission } from "./missionGrouping";

// The Missions board groups by the mission a session is ATTACHED to (SessionDto.missionId), with the role
// the Gateway resolved (SessionDto.sessionRole). These tests lock that, and in particular they lock the
// regression that made the rewrite necessary: this module used to derive missions by pattern-matching the
// session NAME for "<Mission> - <Role>", so an attached session whose name did not fit the convention fell
// to Standalone and no amount of attaching could move it. See the first test in `groupByMission`.

const M_RELEASE: MissionDto = { missionId: "m-release", missionName: "Release 2.0.1" };
const M_EMPTY: MissionDto = { missionId: "m-empty", missionName: "Website truth report" };

// A minimal SessionDto factory - only the fields the grouping and the sort read.
function session(
  fields: {
    name: string;
    number: number;
    sessionId: string;
    missionId?: string | null;
    missionName?: string | null;
    sessionRole?: string | null;
  },
): SessionDto {
  return fields as unknown as SessionDto;
}

describe("displayRole", () => {
  it("returns the Gateway's role in canonical casing", () => {
    expect(displayRole(session({ name: "a", number: 1, sessionId: "a", sessionRole: "architect" })))
      .toBe("Architect");
    expect(displayRole(session({ name: "a", number: 1, sessionId: "a", sessionRole: "MANAGER" })))
      .toBe("Manager");
    expect(displayRole(session({ name: "a", number: 1, sessionId: "a", sessionRole: "Worker" })))
      .toBe("Worker");
  });

  it("treats 'Standalone' as no role, not as a role", () => {
    expect(displayRole(session({ name: "a", number: 1, sessionId: "a", sessionRole: "Standalone" })))
      .toBeNull();
  });

  it("returns null when the Gateway sent no role", () => {
    expect(displayRole(session({ name: "a", number: 1, sessionId: "a" }))).toBeNull();
    expect(displayRole(session({ name: "a", number: 1, sessionId: "a", sessionRole: "  " }))).toBeNull();
  });

  it("passes an unknown role through rather than overruling the Gateway", () => {
    expect(displayRole(session({ name: "a", number: 1, sessionId: "a", sessionRole: "Inspector" })))
      .toBe("Inspector");
  });
});

describe("groupByMission", () => {
  // THE REGRESSION. Every one of these sessions is attached to the same mission; only one is NAMED in the
  // old "<Mission> - <Role>" convention. Under the name parser the other four fell to Standalone, which is
  // exactly what the owner saw: a mission reading "1 session" with its other members listed below it as
  // unrelated work.
  it("groups every attached session, whatever it is called", () => {
    const { missions, standalone } = groupByMission(
      [
        session({ name: "Release 2.0.1 - Architect", number: 124, sessionId: "a",
          missionId: "m-release", sessionRole: "Architect" }),
        session({ name: "fix: 2481 delete bias path", number: 113, sessionId: "b",
          missionId: "m-release", sessionRole: "Worker" }),
        session({ name: "fix: 2487 wingman card", number: 114, sessionId: "c",
          missionId: "m-release", sessionRole: "Worker" }),
        session({ name: "fix: 2483 dictionary fail-open", number: 117, sessionId: "d",
          missionId: "m-release", sessionRole: "Worker" }),
        session({ name: "fix: 2482 tenant glossary", number: 122, sessionId: "e",
          missionId: "m-release", sessionRole: "Worker" }),
      ],
      [M_RELEASE],
    );

    expect(missions).toHaveLength(1);
    expect(missions[0].name).toBe("Release 2.0.1");
    expect(missions[0].members).toHaveLength(5);
    expect(standalone).toHaveLength(0);
  });

  it("puts a session attached to no mission in Standalone", () => {
    const { missions, standalone } = groupByMission(
      [
        session({ name: "Release 2.0.1 - Architect", number: 124, sessionId: "a",
          missionId: "m-release", sessionRole: "Architect" }),
        session({ name: "Working Mac", number: 101, sessionId: "b" }),
        session({ name: "Tidy branches", number: 102, sessionId: "c", missionId: "   " }),
      ],
      [M_RELEASE],
    );

    expect(missions).toHaveLength(1);
    expect(missions[0].members).toHaveLength(1);
    expect(standalone.map((s) => s.number)).toEqual([101, 102]);
  });

  // A name that LOOKS like the old convention must not create a mission - the attachment is the only thing
  // that decides. This is the guard against the parser creeping back in.
  it("never invents a mission from a session's name", () => {
    const { missions, standalone } = groupByMission(
      [session({ name: "Gateway Cleanup - Manager", number: 130, sessionId: "a" })],
      [],
    );

    expect(missions).toHaveLength(0);
    expect(standalone.map((s) => s.number)).toEqual([130]);
  });

  it("shows a mission with no sessions attached to it yet", () => {
    const { missions } = groupByMission([], [M_RELEASE, M_EMPTY]);

    expect(missions.map((m) => m.name)).toEqual(["Release 2.0.1", "Website truth report"]);
    expect(missions[0].members).toHaveLength(0);
  });

  // The mission records and the session roster are two different reads, and the mission stores are already
  // observed to disagree. An attachment we cannot resolve to a record is still an attachment.
  it("keeps a session whose mission is not in the record list, using the cached name", () => {
    const { missions, standalone } = groupByMission(
      [
        session({ name: "worker one", number: 140, sessionId: "a",
          missionId: "m-ghost", missionName: "BPM Studio QA cleanup", sessionRole: "Worker" }),
      ],
      [M_RELEASE],
    );

    expect(standalone).toHaveLength(0);
    const ghost = missions.find((m) => m.key === "m-ghost");
    expect(ghost?.name).toBe("BPM Studio QA cleanup");
    expect(ghost?.fromSessionOnly).toBe(true);
    expect(missions.find((m) => m.key === "m-release")?.fromSessionOnly).toBe(false);
  });

  it("prefers the record's name over the copy cached on the session (which a rename would stale)", () => {
    const { missions } = groupByMission(
      [
        session({ name: "w", number: 1, sessionId: "a",
          missionId: "m-release", missionName: "Release 2.0.0 (old name)" }),
      ],
      [M_RELEASE],
    );

    expect(missions[0].name).toBe("Release 2.0.1");
  });

  it("joins the roster to the records regardless of id casing", () => {
    const { missions } = groupByMission(
      [session({ name: "w", number: 1, sessionId: "a", missionId: "M-RELEASE" })],
      [M_RELEASE],
    );

    expect(missions).toHaveLength(1);
    expect(missions[0].members).toHaveLength(1);
  });

  it("orders members Architect, Manager, Worker, then no-role, then by number", () => {
    const { missions } = groupByMission(
      [
        session({ name: "d", number: 50, sessionId: "a", missionId: "m-release", sessionRole: "Worker" }),
        session({ name: "b", number: 20, sessionId: "b", missionId: "m-release", sessionRole: "Manager" }),
        session({ name: "c", number: 30, sessionId: "c", missionId: "m-release", sessionRole: "Architect" }),
        session({ name: "a", number: 10, sessionId: "d", missionId: "m-release", sessionRole: "Worker" }),
        session({ name: "e", number: 5, sessionId: "e", missionId: "m-release" }),
      ],
      [M_RELEASE],
    );

    expect(missions[0].members.map((m) => `${m.role ?? "-"}:${m.session.number}`)).toEqual([
      "Architect:30",
      "Manager:20",
      "Worker:10",
      "Worker:50",
      "-:5",
    ]);
  });

  it("sorts missions alphabetically by display name, case-insensitively", () => {
    const { missions } = groupByMission(
      [],
      [
        { missionId: "m3", missionName: "Zebra" },
        { missionId: "m1", missionName: "apple" },
        { missionId: "m2", missionName: "Banya" },
      ],
    );

    expect(missions.map((m) => m.name)).toEqual(["apple", "Banya", "Zebra"]);
  });

  it("labels a mission whose name is unknown rather than rendering a blank card", () => {
    const { missions } = groupByMission(
      [session({ name: "w", number: 1, sessionId: "a", missionId: "m-x" })],
      [],
    );

    expect(missions[0].name).toBe("(unnamed mission)");
  });

  it("returns an empty fleet unchanged", () => {
    expect(groupByMission([], [])).toEqual({ missions: [], standalone: [] });
  });
});
