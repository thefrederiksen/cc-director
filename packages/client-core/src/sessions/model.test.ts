import { describe, expect, it } from "vitest";
import type { SessionDto } from "../api/client";
import { modelChipOf } from "./model";

// Issue devthrottle_internal#1340. This reader exists so the Fleet Map card, the session roster and the
// session header cannot word one session three ways - so what is pinned here is that it composes NOTHING.
// Every string it returns must be the Gateway's, and the one decision it makes is whether there is a
// verdict at all.

function session(overrides: Partial<SessionDto> = {}): SessionDto {
  return { sessionId: "s1", agent: "ClaudeCode", ...overrides } as SessionDto;
}

describe("modelChipOf", () => {
  it("returns the Gateway's words verbatim", () => {
    const s = session({
      modelDisplay: { kind: "reported", text: "fable-5", modelId: "claude-fable-5", tooltip: "claude-fable-5" },
    });
    expect(modelChipOf(s)).toEqual({ text: "fable-5", title: "claude-fable-5", absent: false });
  });

  it("marks both absences absent while keeping their different words", () => {
    const notYet = session({
      modelDisplay: {
        kind: "notRecordedYet",
        text: "no model yet",
        tooltip: "No model recorded yet. It is read from the agent's own records at each turn-end.",
        isAbsent: true,
      },
    });
    const never = session({
      modelDisplay: {
        kind: "notReported",
        text: "model not reported",
        tooltip: "This agent does not report the model it is running.",
        isAbsent: true,
      },
    });
    expect(modelChipOf(notYet)?.absent).toBe(true);
    expect(modelChipOf(never)?.absent).toBe(true);
    expect(modelChipOf(notYet)?.text).not.toBe(modelChipOf(never)?.text);
    expect(modelChipOf(notYet)?.title).not.toBe(modelChipOf(never)?.title);
  });

  it("renders nothing when the Gateway stamped no verdict", () => {
    // A missing VERDICT is not a missing model. An older Gateway said nothing, and inventing a placeholder
    // here would be this client ruling in the Gateway's place.
    expect(modelChipOf(session())).toBeNull();
    expect(modelChipOf(session({ modelDisplay: null }))).toBeNull();
  });

  it("renders nothing for a verdict with no words in it", () => {
    expect(modelChipOf(session({ modelDisplay: { kind: "reported", text: "" } }))).toBeNull();
    expect(modelChipOf(session({ modelDisplay: { kind: "reported", text: "   " } }))).toBeNull();
  });

  it("treats a missing isAbsent as a fact, not an absence", () => {
    // Only an explicit true mutes the chip; an older shape that omits the flag renders as a normal model.
    expect(modelChipOf(session({ modelDisplay: { kind: "reported", text: "opus-5" } }))?.absent).toBe(false);
  });
});
