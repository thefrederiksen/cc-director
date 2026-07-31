import { describe, expect, it } from "vitest";
import type { SessionDto } from "../api/client";
import { voiceQueueFor } from "./voiceQueue";

/**
 * Inspection 1, finding 2.
 *
 * These tests feed WHOLE SESSIONS carrying the Gateway's own reachability stamp, not a hand-built
 * `reachable` boolean. That distinction is the entire point: the tests that missed this defect passed
 * `isVoiceReady` a boolean the caller had already computed wrongly, so they could never have caught the
 * caller computing it wrongly. Here the reachability decision is inside the function under test.
 *
 * WHAT IS STILL NOT PROVEN BY THIS FILE: that the mobile Home page CALLS voiceQueueFor. There is no test
 * harness in apps/mobile at all - no vitest dependency, no configuration, no test file - so no rendered
 * wiring test exists for the phone. What the extraction buys instead is that the call site no longer has
 * a reachability argument to substitute: it passes sessions and nothing else. That is a smaller claim
 * than "the wire is pinned" and it is deliberately not written as if it were the larger one.
 */

const BASE: SessionDto = {
  sessionId: "s-1",
  name: "Architect",
  activityState: "Waiting",
  statusColor: "red",
  triageBucket: "needsYou",
  effectiveColor: "red",
  voiceMode: true,
  voiceAudioReady: true,
  needsYouSince: "2026-07-30T20:00:00Z",
  createdAt: "2026-07-30T19:00:00Z",
} as unknown as SessionDto;

function session(over: Partial<SessionDto>): SessionDto {
  return { ...BASE, ...over } as SessionDto;
}

describe("the voice queue reads reachability from the Gateway, not from push freshness", () => {
  it("keeps a session on a machine the Gateway says can be acted on", () => {
    const queue = voiceQueueFor([session({ sessionId: "s-1", machineReachable: true })]);
    expect(queue.map((s) => s.sessionId)).toEqual(["s-1"]);
  });

  // THE DEFECT. A wobbly machine - tunnel up, pushes merely late - was excluded from the hands-free lens,
  // so the owner was not told about work he could have acted on straight away. The Gateway stamps wobbly as
  // reachable; the queue must honour that.
  it("KEEPS a wobbly session, whose tunnel is up even though its pushes are late", () => {
    const queue = voiceQueueFor([session({ sessionId: "wobbly", machineReachable: true })]);
    expect(queue.map((s) => s.sessionId)).toEqual(["wobbly"]);
  });

  // The other half, and without it "return everything" would pass the tests above. A machine nobody can
  // reach kept its last-known voiceAudioReady and its already-downloaded clip, so it would otherwise sit in
  // this tab claiming it can speak - and this is the one surface where a false "ready" is read out loud.
  it("DROPS a session whose machine the Gateway says cannot be reached", () => {
    const queue = voiceQueueFor([session({ sessionId: "gone", machineReachable: false })]);
    expect(queue).toEqual([]);
  });

  it("still includes a session an older Gateway never stamped at all", () => {
    // machineReachable absent means the Gateway did not say. Treating silence as unreachable would empty
    // the queue against an older Gateway; machineCanBeActedOn is `!== false` for exactly this reason.
    const queue = voiceQueueFor([session({ sessionId: "unstamped", machineReachable: undefined })]);
    expect(queue.map((s) => s.sessionId)).toEqual(["unstamped"]);
  });

  it("sorts the queue oldest-waiting first, so it can be worked top to bottom by ear", () => {
    const queue = voiceQueueFor([
      session({ sessionId: "newer", machineReachable: true, needsYouSince: "2026-07-30T21:00:00Z", createdAt: "2026-07-30T21:00:00Z" }),
      session({ sessionId: "older", machineReachable: true, needsYouSince: "2026-07-30T18:00:00Z", createdAt: "2026-07-30T18:00:00Z" }),
    ]);
    expect(queue.map((s) => s.sessionId)).toEqual(["older", "newer"]);
  });

  it("mixes the two rules: only reachable, waiting sessions survive", () => {
    const queue = voiceQueueFor([
      session({ sessionId: "reachable-waiting", machineReachable: true }),
      session({ sessionId: "unreachable-waiting", machineReachable: false }),
      session({ sessionId: "reachable-not-waiting", machineReachable: true, triageBucket: "active", effectiveColor: "blue" }),
    ]);
    expect(queue.map((s) => s.sessionId)).toEqual(["reachable-waiting"]);
  });
});
