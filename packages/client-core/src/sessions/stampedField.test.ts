import { describe, expect, it } from "vitest";
import { dotColor, requireGatewayField } from "./ordering";

// The fail-loud rule, tested directly - the rule every stamped surface extends rather than re-implements.
//
// This exists because of a real fallback on the Exes page: `dotColor(s.effectiveColor ?? "unknown")`.
// The `??` looks defensive and is the opposite. It is the "no fallback programming" law in miniature:
// there is no safe default for a colour, because a guessed colour is pixel-identical to a real one.
describe("a client that cannot get a stamped answer fails rather than guessing", () => {
  it("throws on a missing stamped field instead of substituting a default", () => {
    expect(() => requireGatewayField(undefined, "effectiveColor", "s1"))
      .toThrow("Gateway /sessions missing effectiveColor for session s1");
    expect(() => requireGatewayField(null, "stateLabel", "s1"))
      .toThrow("Gateway /sessions missing stateLabel for session s1");
    // Whitespace is not an answer either.
    expect(() => requireGatewayField("   ", "effectiveColor", "s1"))
      .toThrow("Gateway /sessions missing effectiveColor");
  });

  it("names the session so an unstamped row can be found, and says what to do", () => {
    expect(() => requireGatewayField(undefined, "effectiveColor", undefined))
      .toThrow("(unknown)");
    expect(() => requireGatewayField(undefined, "effectiveColor", "s1"))
      .toThrow("Redeploy Gateway and mobile together");
  });

  it("passes a real stamped value straight through", () => {
    expect(requireGatewayField("blue", "effectiveColor", "s1")).toBe("blue");
    expect(requireGatewayField("  Working  ", "stateLabel", "s1")).toBe("Working");
  });

  it("THE REASON: guessing 'unknown' would make an unstamped session claim to be PARKED", () => {
    // This is why the `??` was not harmless. "unknown" is a real Gateway colour name, so dotColor
    // happily renders it - as #6B7280. That is the SAME grey as "grey", which means "parked or
    // exited". So a session the Gateway never stamped did not render as broken or missing; it
    // rendered as a perfectly ordinary parked session, and no one would ever look at it.
    //
    // A working session behind that fallback would read as parked. That is the law's exact inversion,
    // arrived at by a default instead of a fold.
    expect(dotColor("unknown")).toBe("#6B7280");
    expect(dotColor("unknown")).toBe(dotColor("grey")); // indistinguishable from parked
    expect(dotColor("unknown")).not.toBe(dotColor("blue")); // and nothing like the working blue it may have been
  });
});
