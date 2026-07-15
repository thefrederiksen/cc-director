import { describe, expect, it } from "vitest";
import type { SessionDto } from "../api/client";
import { classify, contextLine, dotColor, effectiveColor, inBucket, inWaitingOrder, isWorking, stateLabel } from "./ordering";

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

  it("stateLabel reads the Gateway-stamped label", () => {
    // stateLabel is a Gateway-stamped field not yet in the generated schema (like triageBucket above),
    // so the literal is cast the same way; the accessor reads it through the GatewayStampedSession cast.
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
