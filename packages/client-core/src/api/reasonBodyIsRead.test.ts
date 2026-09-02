import { describe, it, expect } from "vitest";
import { GatewayError, GATEWAY_UNREACHABLE_MESSAGE, gatewayErrorMessage } from "./client";

// A SERVER REASON MUST NEVER LOSE TO A SENTENCE WE INVENT FROM A STATUS NUMBER.
//
// The Gateway's availability surfaces answer `{ available: false, reason }` - the statistics feed at
// GET /stats/data is the one this file was written for. `reason` was not among the keys the client read a
// failure sentence out of (it read error / detail / message / code), so the server's own explanation was
// parsed into nothing, and a 503 with no reason falls through to the "never reached a healthy backend"
// branch.
//
// The cost was an incident (devthrottle_internal, 2 September 2026). The Gateway was healthy and serving
// every other page; its statistics store had lost a database connection during a deploy and said exactly
// that, in a full sentence, in the body. Your Throttle displayed "Can't reach the Gateway - retrying." for
// two hours. The one line that would have named the real fault was on the wire the whole time, and the
// investigation it misdirected started at the Gateway's health and the deploy pipeline instead.
//
// These tests pin the fix at the level that matters - what a person reads on the page - rather than at the
// parser, so a later refactor that keeps a `reason` key working but stops surfacing it still fails here.
describe("a 503 carrying the server's own reason", () => {
  const REASON =
    "The hosted statistics store is unavailable (unreachable): The statistics store " +
    "(postgres) failed after the startup deadline (NpgsqlException). The Gateway is retrying it.";

  const from503 = async (body: unknown) =>
    GatewayError.from(
      new Response(JSON.stringify(body), {
        status: 503,
        headers: { "content-type": "application/json" },
      }),
      "load your throttle",
    );

  it("shows the server's reason instead of the generic unreachable line", async () => {
    const err = await from503({ available: false, reason: REASON });

    expect(err.serverReason).toBe(REASON);
    expect(gatewayErrorMessage(err)).toContain("statistics store");
    expect(gatewayErrorMessage(err)).not.toBe(GATEWAY_UNREACHABLE_MESSAGE);
  });

  it("does not tell the user to try again when the Gateway is already retrying for them", async () => {
    // The retry hint is advice to do something. When the server has said it is retrying by itself, that
    // advice is wrong - and two sentences that contradict each other about who acts next are worse than
    // either alone.
    const msg = gatewayErrorMessage(await from503({ available: false, reason: REASON }));

    expect(msg).toBe(REASON);
    expect(msg).not.toContain("Try again.");
  });

  // THE NEGATIVE CONTROL. Without this, the tests above would still pass against a build that had simply
  // stopped collapsing 503s at all - which would break every background poll that legitimately relies on
  // the shared line. The collapse is correct; reading the reason first is what was missing.
  it("still collapses a 503 that carries NO reason to the shared unreachable line", async () => {
    const err = await from503({ available: false });

    expect(err.serverReason).toBeUndefined();
    expect(gatewayErrorMessage(err)).toBe(GATEWAY_UNREACHABLE_MESSAGE);
  });

  it("still prefers `error` when a body carries one, so no existing surface changes", async () => {
    const err = await from503({ error: "the usual shape", reason: "the availability shape" });

    expect(err.serverReason).toBe("the usual shape");
  });
});
