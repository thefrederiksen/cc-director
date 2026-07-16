import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { rowVoiceInputs, saveVoiceMeta } from "./clips";
import { voiceRowState } from "./voiceRowState";
import { type SessionDto } from "../api/client";

// The ADAPTER's tests. voiceRowState.ts is a pure rule with a thorough suite, but a pure rule is only
// as honest as the thing that feeds it, and until now nothing fed-side was pinned at all: every test
// in voiceRowState.test.ts hands the rule a hand-written VoiceRowInputs, so all of them would keep
// passing if rowVoiceInputs mapped the session wrong. That is the gap this file closes. Found in
// review (Claude and Codex independently, 2026-07-15).
//
// It matters here more than it usually would, because the bug this whole module exists to fix was
// never in the rule - the old roster had no rule, it had `if (clip.phase === "ready")` inline. The
// bug was, and still is, entirely in what the roster BELIEVES about a session. So this is the layer
// where it can come back.
//
// The clip store's in-memory map has no test seam (it is written only by ensureClip, which downloads),
// so these tests do not drive phoneReadyForCurrentTurn true. They cover what they can reach: the
// session->inputs mapping, the metadata cache, and the reachability gate - and the reachability gate
// short-circuits before phone-readiness is consulted, so the headline case below is fully exercised.

/** A session that looks, in every field the roster reads, exactly like a healthy voice session. */
function healthySession(overrides: Partial<SessionDto> = {}): SessionDto {
  return {
    sessionId: "sid-1",
    voiceMode: true,
    voiceGenerating: false,
    voiceAudioReady: true,
    voiceUnavailable: null,
    ...overrides,
  } as unknown as SessionDto;
}

let store: Map<string, string>;

beforeEach(() => {
  // node is vitest's default environment here (there is no vitest config), so localStorage does not
  // exist and getVoiceMeta would take its capability-detection path and return null for everything -
  // which would make these tests pass for the wrong reason. Stub a real one.
  store = new Map<string, string>();
  vi.stubGlobal("localStorage", {
    getItem: (k: string) => store.get(k) ?? null,
    setItem: (k: string, v: string) => void store.set(k, v),
    removeItem: (k: string) => void store.delete(k),
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("rowVoiceInputs", () => {
  // THE HEADLINE CASE, and the reason `reachable` was added. This is the retained-unreachable session
  // in full: the keep-and-mark merge hands the roster the last-known DTO of a machine that has gone
  // offline, and that DTO is healthy in every field, because it IS the last healthy one. The phone
  // still holds the clip and the cached narration from before the outage. Nothing in the session can
  // reveal the problem - only the caller knows the row was retained.
  //
  // Composed through voiceRowState, because the bug was only ever visible in the composition: the
  // adapter reporting reachable:false is worthless if the rule then ignores it.
  it("reports a retained unreachable session as not reachable, and the row falls silent", () => {
    saveVoiceMeta("sid-1", { ready: true, spoken: "the agent is waiting for you", reply: "", generatedAt: "T1" });
    const session = healthySession();

    expect(rowVoiceInputs(session, false, true).reachable).toBe(true);
    expect(rowVoiceInputs(session, false, false).reachable).toBe(false);
    // The same session, the same cached narration, the same clip - only reachability differs.
    expect(voiceRowState(rowVoiceInputs(session, false, false))).toBe("none");
  });

  // The classic adapter bug spot: voiceUnavailable is an OBJECT on the wire (the shared hosted-AI
  // reason), not a boolean, and the rule takes a boolean. `!= null` is doing the work, and it has to
  // treat a present-but-falsy-looking reason as unavailable.
  it("maps a present voiceUnavailable reason to true, and its absence to false", () => {
    expect(rowVoiceInputs(healthySession({ voiceUnavailable: null }), false, true).voiceUnavailable).toBe(false);
    expect(
      rowVoiceInputs(healthySession({ voiceUnavailable: undefined } as Partial<SessionDto>), false, true)
        .voiceUnavailable,
    ).toBe(false);
    expect(
      rowVoiceInputs(
        healthySession({ voiceUnavailable: { state: "OutOfCredits", message: "" } } as unknown as Partial<SessionDto>),
        false,
        true,
      ).voiceUnavailable,
    ).toBe(true);
  });

  // hasSpokenText comes from the CACHED metadata, not the session - the session never carries the
  // narration text. A wordless narration must not earn a triangle (the Voice screen refuses to speak
  // one), so this mapping is load-bearing.
  it("reads spoken text from the cached metadata, and reports none when there is no cache", () => {
    expect(rowVoiceInputs(healthySession(), false, true).hasSpokenText).toBe(false);

    saveVoiceMeta("sid-1", { ready: true, spoken: "", reply: "", generatedAt: "T1" });
    expect(rowVoiceInputs(healthySession(), false, true).hasSpokenText).toBe(false);

    saveVoiceMeta("sid-1", { ready: true, spoken: "some words", reply: "", generatedAt: "T1" });
    expect(rowVoiceInputs(healthySession(), false, true).hasSpokenText).toBe(true);
  });

  // A session with no cached metadata has never had a narration this phone knows of, so it cannot be
  // holding the current turn's bytes - whatever the clip store happens to contain.
  it("cannot be phone-ready for the current turn with no cached metadata", () => {
    expect(rowVoiceInputs(healthySession(), false, true).phoneReadyForCurrentTurn).toBe(false);
  });

  // meta.ready false means the cached stamp does not describe a playable narration, so it must not be
  // used as "the current turn" to compare a held clip against.
  it("cannot be phone-ready when the cached metadata is not ready", () => {
    saveVoiceMeta("sid-1", { ready: false, spoken: "", reply: "", generatedAt: "T1" });
    expect(rowVoiceInputs(healthySession(), false, true).phoneReadyForCurrentTurn).toBe(false);
  });

  // The straight passthroughs. Cheap to assert, and they are how the rule learns anything at all -
  // a Boolean() coercion dropped here would silently disarm a guard the rule's own tests still prove.
  it("passes the Gateway's voice booleans through to the rule", () => {
    const i = rowVoiceInputs(healthySession({ voiceGenerating: true, voiceAudioReady: true }), true, true);
    expect(i.voiceMode).toBe(true);
    expect(i.gatewayGenerating).toBe(true);
    expect(i.gatewayHasAudio).toBe(true);
    expect(i.agentWorking).toBe(true);

    const off = rowVoiceInputs(healthySession({ voiceMode: false, voiceGenerating: false, voiceAudioReady: false }), false, true);
    expect(off.voiceMode).toBe(false);
    expect(off.gatewayGenerating).toBe(false);
    expect(off.gatewayHasAudio).toBe(false);
    expect(off.agentWorking).toBe(false);
  });

  // An id-less session must not describe itself using whatever sits under the "" key. Every lookup in
  // the adapter is keyed by session id, and "" is a perfectly valid key in localStorage and in the clip
  // map - so before the guard, this row would have reported another entry's narration as its own.
  //
  // Written first as "documents today's behavior" with the wrong assertion baked in; Codex called that
  // out in review - a test that enshrines a wart is not a reassurance, it just makes the wart harder to
  // remove. The behavior was fixed instead, and this now pins the fix.
  it("does not read cached metadata for a session with no id", () => {
    saveVoiceMeta("", { ready: true, spoken: "someone else's words", reply: "", generatedAt: "T1" });
    const i = rowVoiceInputs(healthySession({ sessionId: undefined } as Partial<SessionDto>), false, true);
    expect(i.hasSpokenText).toBe(false);
    expect(i.gatewayHasAudio).toBe(false);
    expect(i.phoneReadyForCurrentTurn).toBe(false);
    expect(voiceRowState(i)).toBe("none");
  });
});
