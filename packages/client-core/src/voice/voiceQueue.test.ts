import { describe, expect, it, vi } from "vitest";
import type { SessionDto } from "../api/client";
import type { VoiceRowInputs } from "./voiceRowState";

// WHAT IS REAL HERE AND WHAT IS STUBBED, because getting this wrong is how a test proves nothing.
//
// REAL: voiceQueueFor itself - which sessions it selects, what it passes as the reachability input,
// and the order it returns them in. That composition is the thing inspection 1 finding 2 was about.
//
// STUBBED: the readiness RULE (voiceRowState/isVoiceReady). It is stubbed to return the reachability
// input it was handed, which turns this file into a test of "what does the queue ASK about each
// session" rather than "what does the rule ANSWER". That is deliberate and necessary:
//
//   - the rule already has a thorough suite of its own in voiceRowState.test.ts, and the adapter that
//     feeds it has one in rowVoiceInputs.test.ts;
//   - and the real rule can NEVER return "ready" in this environment. Readiness requires
//     phoneReadyForCurrentTurn, which reads the clip store's in-memory map, which is written only by
//     ensureClip, which downloads. rowVoiceInputs.test.ts records the same limitation in its own
//     header. A test written against the real rule here would assert against a permanently empty
//     queue and would pass just as happily if the queue were hard-coded to return nothing.
//
// So the stub is what makes the queue observable at all. What it costs is that this file says nothing
// about whether a genuinely ready session plays - and that is not its question.

vi.mock("./voiceRowState", () => ({
  // The reachability input, returned verbatim. Every assertion below is therefore about which value
  // voiceQueueFor CHOSE to pass - and the defect was that it chose a retention mark instead of the
  // Gateway's stamp.
  isVoiceReady: (i: VoiceRowInputs) => i.reachable,
}));

// A static import is correct despite the mock above: vitest hoists vi.mock calls above the imports, so
// voiceQueue.ts resolves the stubbed module when it loads. Preferred over a dynamic import here because
// the mock factory closes over nothing, and the static form is the well-trodden path.
import { voiceQueueFor } from "./voiceQueue";

const BASE = {
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

describe("the voice queue asks the Gateway about reachability, not the retention mark", () => {
  // THE DEFECT. A wobbly machine - tunnel up, pushes merely late - is stamped reachable by the Gateway
  // but carries a retention mark on the phone. The queue used to read the mark, so this session
  // silently left the hands-free lens and the owner stopped being told about work he could act on.
  it("KEEPS a session the Gateway stamped reachable", () => {
    const queue = voiceQueueFor([session({ sessionId: "wobbly", machineReachable: true })]);
    expect(queue.map((s) => s.sessionId)).toEqual(["wobbly"]);
  });

  // The other half. Without it, passing `true` unconditionally would satisfy the test above - and
  // "promise a dead machine can speak" is the defect on the other side of this same rule.
  it("DROPS a session the Gateway stamped unreachable", () => {
    const queue = voiceQueueFor([session({ sessionId: "gone", machineReachable: false })]);
    expect(queue).toEqual([]);
  });

  it("still includes a session an older Gateway never stamped at all", () => {
    // Absent means the Gateway did not say, and silence is not "unreachable" - machineCanBeActedOn is
    // `!== false` precisely so an older Gateway does not empty the queue.
    const queue = voiceQueueFor([session({ sessionId: "unstamped", machineReachable: undefined })]);
    expect(queue.map((s) => s.sessionId)).toEqual(["unstamped"]);
  });

  it("drops a session that is not waiting on the owner, however reachable its machine", () => {
    const queue = voiceQueueFor([
      session({ sessionId: "waiting", machineReachable: true }),
      session({ sessionId: "busy", machineReachable: true, triageBucket: "active", effectiveColor: "blue" }),
    ]);
    expect(queue.map((s) => s.sessionId)).toEqual(["waiting"]);
  });

  it("returns the queue oldest-waiting first, so it can be worked top to bottom by ear", () => {
    const queue = voiceQueueFor([
      session({ sessionId: "newer", machineReachable: true, needsYouSince: "2026-07-30T21:00:00Z", createdAt: "2026-07-30T21:00:00Z" }),
      session({ sessionId: "older", machineReachable: true, needsYouSince: "2026-07-30T18:00:00Z", createdAt: "2026-07-30T18:00:00Z" }),
    ]);
    expect(queue.map((s) => s.sessionId)).toEqual(["older", "newer"]);
  });
});
