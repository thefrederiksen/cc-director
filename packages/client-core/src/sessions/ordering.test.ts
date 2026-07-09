import { describe, expect, it } from "vitest";
import type { SessionDto } from "../api/client";
import { classify, dotColor, effectiveColor, inBucket, isWorking, stateLabel } from "./ordering";

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

  it("isWorking uses the Gateway effectiveColor, not the raw statusColor", () => {
    // Blue effectiveColor is working, regardless of the raw Director statusColor.
    expect(isWorking(session({ effectiveColor: "blue", statusColor: "red" }))).toBe(true);
    // A non-blue effective color at a working activity state still counts (mid-turn before color settles).
    expect(isWorking(session({ effectiveColor: "yellow", activityState: "Working" }))).toBe(true);
    // Red and not working -> not working.
    expect(isWorking(session({ effectiveColor: "red", activityState: "WaitingForInput" }))).toBe(false);
    // On-hold is never working, even if blue.
    expect(isWorking(session({ effectiveColor: "blue", onHold: true }))).toBe(false);
  });
});
