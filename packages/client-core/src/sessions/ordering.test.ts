import { describe, expect, it } from "vitest";
import type { SessionDto } from "../api/client";
import { classify, dotColor, effectiveColor, inBucket } from "./ordering";

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
});
