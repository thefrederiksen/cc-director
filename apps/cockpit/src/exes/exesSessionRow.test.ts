import { describe, expect, it } from "vitest";
import type { ExesSession } from "@devthrottle/client-core/exes/exesClient";
import { exesSessionRow } from "./ExesView";

// Defect 15's twin, on the client side: the Exes row guessed when the Gateway did not stamp.
//   dotColor(s.effectiveColor ?? "unknown")   and   {s.stateLabel ?? "-"}
// Both are fallbacks, and the colour one is the dangerous kind - see the last test for why.
function exesSession(fields: Partial<ExesSession> = {}): ExesSession {
  return { sessionId: "s1", ...fields } as ExesSession;
}

describe("the Exes row renders the Gateway's stamp or fails - it never guesses", () => {
  it("renders a stamped session from the fold", () => {
    const row = exesSessionRow(exesSession({ effectiveColor: "blue", stateLabel: "Working" }));
    expect(row.dot).toBe("#3B82F6");
    expect(row.state).toBe("Working");
  });

  it("throws on an unstamped colour instead of quietly painting it parked-grey", () => {
    // THE DEFECT: `?? "unknown"` swallowed this. Note the raw activityState is present and says
    // Working - exactly the case where a guess does the most damage.
    expect(() => exesSessionRow(exesSession({ stateLabel: "Working", activityState: "Working" })))
      .toThrow("Gateway /sessions missing effectiveColor for session s1");
  });

  it("throws on an unstamped label instead of rendering a dash", () => {
    // THE DEFECT: `?? "-"`. Less dangerous than the colour, same class, same row.
    expect(() => exesSessionRow(exesSession({ effectiveColor: "blue" })))
      .toThrow("Gateway /sessions missing stateLabel for session s1");
  });

  it("treats a blank stamp as no stamp", () => {
    expect(() => exesSessionRow(exesSession({ effectiveColor: "   ", stateLabel: "Working" })))
      .toThrow("Gateway /sessions missing effectiveColor");
  });
});
