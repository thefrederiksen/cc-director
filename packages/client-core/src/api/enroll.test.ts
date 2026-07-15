import { describe, expect, it } from "vitest";
import { GATEWAY_NOT_SIGNED_IN, isGatewayNotSignedIn } from "./enroll";
import { GatewayError } from "./client";

// There are TWO different sign-ins in this product and they are easy to conflate:
//
//   1. the PERSON signs in at devthrottle.com (this is what the Sign in button does), and
//   2. the GATEWAY signs in to a DevThrottle account (without this it has no account to enroll onto).
//
// A device cannot enroll until BOTH have happened, and a fresh install has only ever done (1). The
// Gateway reports that as 409 from POST /m/enroll. Before this existed, the callback screen treated it
// as a generic error and offered only "Try again" - which returns to the sign-in screen and fails again
// for exactly the same reason, a loop with no exit. So 409 must stay distinguishable from every other
// enrollment failure, or that dead end comes straight back.
//
// Note this is the pure classification only. client-core has no DOM test tooling (no jsdom, no
// testing-library), so the screen that consumes it is not rendered here.

describe("isGatewayNotSignedIn", () => {
  it("recognises the Gateway-not-signed-in conflict", () => {
    expect(isGatewayNotSignedIn(new GatewayError(GATEWAY_NOT_SIGNED_IN, "not signed in"))).toBe(true);
  });

  it("is 409, the status the Gateway actually answers with", () => {
    expect(GATEWAY_NOT_SIGNED_IN).toBe(409);
  });

  it("does not swallow the other enrollment failures, which the person cannot fix by signing the Gateway in", () => {
    // 403 = this device is not on the Gateway's account; 502 = the cloud was unreachable. Neither is
    // fixed by signing the Gateway in, so neither may reach that screen.
    expect(isGatewayNotSignedIn(new GatewayError(403, "wrong account"))).toBe(false);
    expect(isGatewayNotSignedIn(new GatewayError(502, "cloud unreachable"))).toBe(false);
    expect(isGatewayNotSignedIn(new GatewayError(400, "bad request"))).toBe(false);
  });

  it("does not mistake a plain error or a non-error for the conflict", () => {
    expect(isGatewayNotSignedIn(new Error("boom"))).toBe(false);
    expect(isGatewayNotSignedIn({ status: 409 })).toBe(false);
    expect(isGatewayNotSignedIn(null)).toBe(false);
    expect(isGatewayNotSignedIn(undefined)).toBe(false);
  });
});
