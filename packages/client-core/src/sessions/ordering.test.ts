import { describe, expect, it } from "vitest";
import type { SessionDto } from "../api/client";
import { classify, contextLine, dotColor, effectiveColor, groupByDirector, inBucket, inWaitingOrder, isWorking, stateLabel } from "./ordering";

function session(fields: Partial<SessionDto> & { sessionId?: string } = {}): SessionDto {
  return {
    sessionId: "s1",
    createdAt: "2026-07-08T00:00:00Z",
    sortOrder: 0,
    ...fields,
  } as unknown as SessionDto;
}

describe("Gateway-stamped session presentation state", () => {
  it("uses effectiveColor and triageBucket from /sessions", () => {
    const s = session({ effectiveColor: "yellow", triageBucket: "active" });

    expect(effectiveColor(s)).toBe("yellow");
    expect(classify(s)).toBe("active");
  });

  it("fails loudly when effectiveColor is missing", () => {
    expect(() => effectiveColor(session({ triageBucket: "active" })))
      .toThrow("Gateway /sessions missing effectiveColor");
  });

  it("fails loudly when triageBucket is missing", () => {
    expect(() => classify(session({ effectiveColor: "red" })))
      .toThrow("Gateway /sessions missing triageBucket");
  });

  it("fails loudly when triageBucket is invalid", () => {
    expect(() => classify(session({ effectiveColor: "red", triageBucket: "waiting" } as Partial<SessionDto>)))
      .toThrow("invalid triageBucket");
  });

  it("filters by the Gateway-stamped bucket", () => {
    const sessions = [
      session({ sessionId: "a", effectiveColor: "red", triageBucket: "needsYou", sortOrder: 2 }),
      session({ sessionId: "b", effectiveColor: "blue", triageBucket: "active", sortOrder: 1 }),
    ];

    expect(inBucket(sessions, "needsYou").map((s) => s.sessionId)).toEqual(["a"]);
  });

  it("fails loudly on unknown Gateway colors", () => {
    expect(() => dotColor("chartreuse")).toThrow("Unknown Gateway effectiveColor");
  });

  it("renders the 'unknown' and 'grey' Gateway colors as gray", () => {
    // "unknown" is a real Gateway effectiveColor (an indeterminate activity state) and must render, not throw.
    expect(dotColor("unknown")).toBe("#6B7280");
    expect(dotColor("grey")).toBe("#6B7280");
  });

  it("gives snoozed and exited the SAME grey - the dot draws no distinction the Gateway did not make", () => {
    // The Gateway folds BOTH a parked session and an exited one to "grey": it has no snoozed colour and
    // no exited colour. So the two must be the same pixel here. The difference between them is
    // lifecycle, and lifecycle travels on the stamped label and a badge, never on the dot.
    //
    // This is what the desktop rail got wrong: it took the one name "grey" and split it into two hexes
    // (#9CA3AF when the raw onHold flag was set, #6A6A6A otherwise), inventing a distinction the fold
    // never emitted. Same name, two pixels, from a client re-reading a raw field.
    expect(dotColor("grey")).toBe(dotColor("unknown"));
  });

  it("resolves every Gateway colour name to exactly one canonical hex", () => {
    // THE CANONICAL PALETTE (law 7: every device shows the same thing, always). These exact values are
    // the desktop rail's brushes too. A check that compares fold ANSWERS ("red" === "red") cannot see a
    // surface rendering a different red, so the agreement has to be pinned to the PIXEL here.
    expect(dotColor("red")).toBe("#EF4444");
    expect(dotColor("yellow")).toBe("#EAB308");
    expect(dotColor("orange")).toBe("#F97316");
    expect(dotColor("green")).toBe("#22C55E");
    expect(dotColor("blue")).toBe("#3B82F6");
    expect(dotColor("purple")).toBe("#A855F7");
    expect(dotColor("supporting")).toBe("#64748B");
    expect(dotColor("error")).toBe("#B91C1C");
    expect(dotColor("grey")).toBe("#6B7280");
    expect(dotColor("unknown")).toBe("#6B7280");
  });

  it("never paints a working session anything but blue", () => {
    // The law, as a pixel. The schedule picker used to render a working session GREEN (its own local
    // fold returned "run", and .sched-sdot.run painted --sched-green) - while green means "ready,
    // parked at its prompt" in the shared vocabulary. Same colour, opposite meaning.
    expect(dotColor(effectiveColor(session({ effectiveColor: "blue" })))).toBe("#3B82F6");
    expect(dotColor("blue")).not.toBe(dotColor("green"));
  });

  it("stateLabel reads the Gateway-stamped label", () => {
    // stateLabel IS in the generated schema now that it has been regenerated from the C# DTOs, so the
    // cast is no longer load-bearing - kept only because `session()` takes Partial<SessionDto> and the
    // accessor still reads through the GatewayStampedSession cast.
    expect(stateLabel(session({ stateLabel: "Needs you" } as Partial<SessionDto>))).toBe("Needs you");
  });

  it("stateLabel fails loudly when missing", () => {
    expect(() => stateLabel(session({ effectiveColor: "red" })))
      .toThrow("Gateway /sessions missing stateLabel");
  });

  it("isWorking is exactly the Gateway's blue - nothing else gets a vote", () => {
    // THE LAW (2026-07-14): a working session is BLUE, always. So blue IS working, and the client
    // asks the Gateway and nothing else.
    //
    // Two assertions here used to encode the OLD law and were deliberately removed:
    //   - `isWorking({ effectiveColor: "yellow", activityState: "Working" })` -> true, on the theory
    //     that a working session's colour might not have "settled" yet. It cannot: the Gateway's fold
    //     returns blue for ANY working session, so yellow-while-working is a DTO it can never emit.
    //   - `isWorking({ effectiveColor: "blue", onHold: true })` -> false, commented "on-hold is never
    //     working, even if blue". That is the defect itself, written down as a requirement: it is the
    //     reason a snoozed session that woke up and started working still read as parked.

    // Blue effectiveColor is working, regardless of the raw Director statusColor.
    expect(isWorking(session({ effectiveColor: "blue", statusColor: "red" }))).toBe(true);
    // Red and not working -> not working.
    expect(isWorking(session({ effectiveColor: "red", activityState: "WaitingForInput" }))).toBe(false);
    // Blue AND snoozed is working: the Gateway stamped blue, so the session is running. Snooze is a
    // statement about a session that has stopped; it cannot un-work a running one.
    expect(isWorking(session({ effectiveColor: "blue", onHold: true }))).toBe(true);
  });

  it("contextLine renders the Gateway's stamped label instead of re-deriving one", () => {
    // The row's words come from the same fold as its dot, so they cannot contradict it.
    // stateLabel is Gateway-stamped and not in the generated schema, so each literal needs the same
    // cast the stateLabel tests above use.
    expect(contextLine(session({ effectiveColor: "blue", stateLabel: "Working", onHold: true } as Partial<SessionDto>)))
      .toBe("Working");
    expect(contextLine(session({ effectiveColor: "grey", stateLabel: "Snoozed", onHold: true } as Partial<SessionDto>)))
      .toBe("Snoozed");
    // A working session with dictation in flight reads "Working", not "Transcribing...": the local
    // ladder that produced a blue dot beside the word "Snoozed" is gone.
    expect(contextLine(session({ effectiveColor: "blue", stateLabel: "Working", transcribing: true } as Partial<SessionDto>)))
      .toBe("Working");
  });
});

describe("needs-you waiting-line order", () => {
  const needsYou = (id: string, needsYouSince?: string, extra: Partial<SessionDto> = {}) =>
    session({ sessionId: id, effectiveColor: "red", triageBucket: "needsYou", needsYouSince, ...extra } as Partial<SessionDto>);

  it("puts the longest wait at the top and the newest wait at the bottom", () => {
    const sessions = [
      needsYou("new", "2026-07-09T12:00:00Z"),
      needsYou("oldest", "2026-07-09T09:00:00Z"),
      needsYou("middle", "2026-07-09T10:30:00Z"),
    ];

    expect(inWaitingOrder(sessions).map((s) => s.sessionId)).toEqual(["oldest", "middle", "new"]);
  });

  it("keeps only needs-you sessions and ignores manual desktop sortOrder", () => {
    const sessions = [
      needsYou("waited-most", "2026-07-09T08:00:00Z", { sortOrder: 99 }),
      session({ sessionId: "active", effectiveColor: "blue", triageBucket: "active" }),
      needsYou("waited-least", "2026-07-09T11:00:00Z", { sortOrder: 1 }),
    ];

    expect(inWaitingOrder(sessions).map((s) => s.sessionId)).toEqual(["waited-most", "waited-least"]);
  });

  it("sorts a session with no wait stamp to the bottom", () => {
    const sessions = [
      needsYou("no-stamp", undefined),
      needsYou("has-stamp", "2026-07-09T09:00:00Z"),
    ];

    expect(inWaitingOrder(sessions).map((s) => s.sessionId)).toEqual(["has-stamp", "no-stamp"]);
  });
});

describe("groupByDirector", () => {
  it("groups sessions by their owning Director and labels each with its port", () => {
    const sessions = [
      session({ sessionId: "a", directorId: "d1", machineName: "SOREN_NORTH", sortOrder: 1 }),
      session({ sessionId: "b", directorId: "d2", machineName: "Sorens-Mac-mini", sortOrder: 0 }),
      session({ sessionId: "c", directorId: "d1", machineName: "SOREN_NORTH", sortOrder: 0 }),
    ];
    const ports = new Map([
      ["d1", "7880"],
      ["d2", "7880"],
    ]);

    const groups = groupByDirector(sessions, ports);

    expect(groups.map((g) => `${g.machineName}:${g.port}`)).toEqual([
      "SOREN_NORTH:7880",
      "Sorens-Mac-mini:7880",
    ]);
    // Within the first Director, sessions come back in desktop (sortOrder) order.
    expect(groups[0].sessions.map((s) => s.sessionId)).toEqual(["c", "a"]);
  });

  it("keeps two Directors on the same machine as separate groups, ordered by port", () => {
    const sessions = [
      session({ sessionId: "a", directorId: "hi", machineName: "SOREN_NORTH" }),
      session({ sessionId: "b", directorId: "lo", machineName: "SOREN_NORTH" }),
    ];
    const ports = new Map([
      ["hi", "7885"],
      ["lo", "7880"],
    ]);

    const groups = groupByDirector(sessions, ports);

    expect(groups.map((g) => g.port)).toEqual(["7880", "7885"]);
  });

  it("degrades to the bare machine name when the port is unknown", () => {
    const sessions = [session({ sessionId: "a", directorId: "d1", machineName: "SOREN_NORTH" })];

    const groups = groupByDirector(sessions, new Map());

    expect(groups[0].port).toBe("");
    expect(groups[0].machineName).toBe("SOREN_NORTH");
  });
});
