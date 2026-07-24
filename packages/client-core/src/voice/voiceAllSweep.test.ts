import { describe, expect, it } from "vitest";
import type { SessionDto } from "../api/client";
import { sessionsNeedingVoice } from "./voiceAllSweep";

// The Voice tab's fleet-wide sweep decision (owner, 2026-07-24). The behaviour that matters is the
// one the screen cannot show you: an offline session is skipped by the Gateway and stays "not a
// voice session", so without the attempted-memory the roster would fan out a fleet-wide write every
// 5 seconds for as long as that machine stayed down.

function session(sessionId: string, voiceMode: boolean): SessionDto {
  return { sessionId, voiceMode } as unknown as SessionDto;
}

describe("sessionsNeedingVoice (Voice tab fleet-wide sweep)", () => {
  it("names every session that is not yet a voice session", () => {
    const roster = [session("a", false), session("b", true), session("c", false)];
    expect(sessionsNeedingVoice(roster, new Set())).toEqual(["a", "c"]);
  });

  it("asks for nothing once the whole roster is on voice", () => {
    const roster = [session("a", true), session("b", true)];
    expect(sessionsNeedingVoice(roster, new Set())).toEqual([]);
  });

  it("does not re-ask for a session already attempted - the skipped-offline loop guard", () => {
    // "b" is on an offline computer: the Gateway skipped it, so it is STILL not a voice session on
    // the next poll. Having been asked once, it must not trigger another fleet-wide write.
    const roster = [session("a", true), session("b", false)];
    expect(sessionsNeedingVoice(roster, new Set(["b"]))).toEqual([]);
  });

  it("still picks up a session that appears after the first sweep", () => {
    // The point of the whole change: a session created while you stand on the Voice tab joins the
    // queue by itself, which the old "one button that flips direction" control could never do.
    const attempted = new Set(["b"]);
    const roster = [session("a", true), session("b", false), session("new", false)];
    expect(sessionsNeedingVoice(roster, attempted)).toEqual(["new"]);
  });

  it("ignores sessions with no id - there is nothing to ask about", () => {
    const roster = [session("", false), session("  ", false), session("a", false)];
    expect(sessionsNeedingVoice(roster, new Set())).toEqual(["a"]);
  });

  it("keeps roster order, so the note counts what the queue shows", () => {
    const roster = [session("c", false), session("a", false), session("b", false)];
    expect(sessionsNeedingVoice(roster, new Set())).toEqual(["c", "a", "b"]);
  });
});
