import { describe, expect, it } from "vitest";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import { agentBadgeText } from "./fleetMapFormat";

function session(overrides: Partial<SessionDto> = {}): SessionDto {
  return { sessionId: "s1", agent: "ClaudeCode", ...overrides } as SessionDto;
}

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
