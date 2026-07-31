import { describe, expect, it } from "vitest";
import { isVoiceReady, voiceRowState, type VoiceRowInputs } from "./voiceRowState";

// The reported bug, in the owner's words: "why would the list view show that it's ready with voice when
// there's no voice? If the voice doesn't work, why the fuck is it showing the green triangle so I can
// click it? ... I should never be able to get to this screen if I click a session that has a green
// triangle on it."
//
// The screen he could not get away from said "Voice service down". So these tests are written from the
// outage that produced it: the roster held LAST TURN's clip, still marked ready, and drew a triangle on
// top of a session whose voice the Gateway had already disowned. Each test below pins one of the facts
// the roster had in its hand and ignored, so an edit that goes back to "any bytes will do" fails here
// rather than on a phone.

/** The honest ready state: this turn's audio is on the phone, it has words, and nothing objects. */
const READY_FOR_THIS_TURN: VoiceRowInputs = {
  voiceMode: true,
  reachable: true,
  agentWorking: false,
  voiceUnavailable: false,
  gatewayGenerating: false,
  gatewayHasAudio: true,
  clipDownloading: false,
  phoneReadyForCurrentTurn: true,
  hasSpokenText: true,
};

describe("voiceRowState", () => {
  // The control. If this ever stops being "ready" the triangle has been guarded out of existence and
  // voice mode has no entry point at all - which would pass every test below while shipping nothing.
  it("shows the play triangle when this turn's audio is on the phone", () => {
    expect(voiceRowState(READY_FOR_THIS_TURN)).toBe("ready");
  });

  // THE BUG. The Gateway said "I cannot make voice for this session" (the speech service was down) and
  // the roster drew a green triangle anyway, because it never read the field. Tapping the row landed on
  // "Voice service down" - a screen the owner should never have been able to reach from a triangle.
  it("never offers a triangle when the Gateway says voice is unavailable, even holding ready bytes", () => {
    const state = voiceRowState({ ...READY_FOR_THIS_TURN, voiceUnavailable: true });
    expect(state).toBe("down");
    expect(state).not.toBe("ready");
  });

  // The same outage seen from the other side: during it, the sync stops downloading for the session, so
  // whatever is in the clip store is by definition last turn's. "Down" must beat every arrival signal -
  // a spinner here would promise audio that is never coming.
  it("says down rather than preparing when the Gateway has disowned the voice", () => {
    expect(
      voiceRowState({
        ...READY_FOR_THIS_TURN,
        voiceUnavailable: true,
        gatewayGenerating: true,
        clipDownloading: true,
        phoneReadyForCurrentTurn: false,
      }),
    ).toBe("down");
  });

  // The staleness rule itself, with no outage involved. Bytes on the phone that narrate an OLDER turn
  // are not a play triangle - this is precisely the check the Voice screen makes (phoneReady compares
  // clip.generatedAt to the current voice.generatedAt) and the roster did not.
  it("does not offer a triangle for a clip held from an older turn", () => {
    expect(voiceRowState({ ...READY_FOR_THIS_TURN, phoneReadyForCurrentTurn: false })).toBe("preparing");
  });

  // A newer narration being synthesized is a positive statement that anything held is stale. The roster
  // used to draw the triangle straight through this window, offering last turn's audio while this
  // turn's was being made.
  it("shows preparing, not ready, while the Gateway is generating a newer narration", () => {
    expect(voiceRowState({ ...READY_FOR_THIS_TURN, gatewayGenerating: true })).toBe("preparing");
  });

  // FOUND IN REVIEW, and it is the same bug hiding one level up. The roster's notion of "the current
  // turn" comes from the cached voice metadata, which syncVoiceSessions only refreshes for sessions
  // with voiceAudioReady. So when the Gateway stops advertising audio, the cached stamp AND the clip
  // go stale together and agree with each other perfectly - phoneReadyForCurrentTurn stays true while
  // describing last turn. A Gateway restart does exactly this (its voice cache is in memory) with no
  // outage stamped, so voiceUnavailable does not save us here.
  //
  // The Voice screen is safe from this because it fetches the current stamp live; the roster cannot,
  // so voiceAudioReady is its only live evidence that a current narration exists at all. This is the
  // one place the roster deliberately DIVERGES from voiceAvailability.ts's advice - see the header.
  // Asserted as an EXACT state, not `not.toBe("ready")`. A negative assertion here passes for "down"
  // and "none" too, so it would keep passing through a rewrite that silently changed what the reader
  // sees - it only pins that the bug is absent, not that the replacement is right. Found in review.
  it("does not offer a triangle once the Gateway stops advertising audio, however ready the phone looks", () => {
    expect(voiceRowState({ ...READY_FOR_THIS_TURN, gatewayHasAudio: false })).toBe("none");
  });

  // FOUND IN REVIEW. ready and spoken are independent fields on the WingmanVoice contract, so a
  // ready-but-wordless narration is representable - and the Voice screen refuses to speak one
  // (speaking requires voice.spoken.length > 0). A triangle here would point at exactly the state the
  // screen declines to play, which is this bug wearing a different hat.
  // Exact state, for the reason given above: this lands on "preparing" (the Gateway holds audio, this
  // phone just has nothing playable to offer from it), and that is worth pinning rather than merely
  // asserting the triangle is gone.
  it("does not offer a triangle for a narration with no spoken words", () => {
    expect(voiceRowState({ ...READY_FOR_THIS_TURN, hasSpokenText: false })).toBe("preparing");
  });

  // FOUND IN REVIEW (Claude and Codex, independently). The unbounded stale triangle, and the reason
  // `reachable` exists. The keep-and-mark merge RETAINS a session whose Director went offline, holding
  // its last-known DTO - which says voiceAudioReady: true - and the phone still holds that turn's clip.
  // syncVoiceSessions cannot refresh the cached stamp (getWingmanVoice cannot reach a dead machine,
  // and the failure is swallowed), so the stamp and the clip stay stale TOGETHER and agree with each
  // other perfectly. Every input below is therefore exactly what the honest ready state looks like -
  // that is the whole point: last-known health is indistinguishable from health. Without this gate the
  // row shows a green triangle for as long as the machine stays down, and the Voice tab reads it out.
  it("shows nothing on a retained session whose machine is unreachable, however ready its last-known state looks", () => {
    expect(voiceRowState({ ...READY_FOR_THIS_TURN, reachable: false })).toBe("none");
  });

  // Unreachable beats every arrival signal too. A spinner would promise an arrival that cannot come:
  // nothing is being downloaded from a machine that is not answering.
  it("stays quiet on an unreachable session even with audio and a download apparently in flight", () => {
    expect(
      voiceRowState({
        ...READY_FOR_THIS_TURN,
        reachable: false,
        gatewayGenerating: true,
        clipDownloading: true,
        phoneReadyForCurrentTurn: false,
      }),
    ).toBe("none");
  });

  it("shows preparing while the Gateway holds audio this phone has not pulled down yet", () => {
    expect(
      voiceRowState({ ...READY_FOR_THIS_TURN, phoneReadyForCurrentTurn: false, gatewayHasAudio: true }),
    ).toBe("preparing");
  });

  it("shows preparing while this phone is downloading the clip", () => {
    expect(
      voiceRowState({
        ...READY_FOR_THIS_TURN,
        phoneReadyForCurrentTurn: false,
        gatewayHasAudio: false,
        clipDownloading: true,
      }),
    ).toBe("preparing");
  });

  // The working gate, unchanged from the old roster behavior: the instant the agent resumes, the
  // finished-turn narration is stale and the whole indicator goes quiet.
  it("shows nothing while the agent is working again", () => {
    expect(voiceRowState({ ...READY_FOR_THIS_TURN, agentWorking: true })).toBe("none");
  });

  // A working session's row already says "working". An outage pill on top of it would report a problem
  // on a row that is not waiting on anyone and cannot be acted on.
  it("stays quiet on a working session even when voice is unavailable", () => {
    expect(voiceRowState({ ...READY_FOR_THIS_TURN, agentWorking: true, voiceUnavailable: true })).toBe("none");
  });

  it("shows nothing for a session that is not in voice mode", () => {
    expect(voiceRowState({ ...READY_FOR_THIS_TURN, voiceMode: false })).toBe("none");
  });

  // A voice session that has simply never produced a turn yet: nothing is wrong, and nothing is coming.
  it("shows nothing for a voice session with no narration and none on its way", () => {
    expect(
      voiceRowState({
        ...READY_FOR_THIS_TURN,
        phoneReadyForCurrentTurn: false,
        gatewayHasAudio: false,
        gatewayGenerating: false,
        clipDownloading: false,
      }),
    ).toBe("none");
  });
});

describe("isVoiceReady", () => {
  // The Voice tab's membership rule. It is defined as "voiceRowState === ready" precisely so the tab
  // and the triangle can never disagree: if a session is in the Voice tab it HAS a triangle, and if it
  // has a triangle it is in the Voice tab. One rule, one answer.
  it("is exactly the sessions that show a play triangle", () => {
    expect(isVoiceReady(READY_FOR_THIS_TURN)).toBe(true);
    expect(isVoiceReady({ ...READY_FOR_THIS_TURN, voiceUnavailable: true })).toBe(false);
    expect(isVoiceReady({ ...READY_FOR_THIS_TURN, agentWorking: true })).toBe(false);
    expect(isVoiceReady({ ...READY_FOR_THIS_TURN, gatewayGenerating: true })).toBe(false);
    expect(isVoiceReady({ ...READY_FOR_THIS_TURN, phoneReadyForCurrentTurn: false })).toBe(false);
    expect(isVoiceReady({ ...READY_FOR_THIS_TURN, gatewayHasAudio: false })).toBe(false);
    expect(isVoiceReady({ ...READY_FOR_THIS_TURN, hasSpokenText: false })).toBe(false);
    expect(isVoiceReady({ ...READY_FOR_THIS_TURN, reachable: false })).toBe(false);
  });

  // THE VOICE QUEUE ITSELF, pinned as a queue and not only as a single row (Epic #1159 step A). The
  // Gateway now SERVES the sessions of a machine nobody can reach - dimmed and dated instead of deleted -
  // so unreachable rows are on the roster in normal operation rather than only after a client-side
  // retention. The queue is built by filtering on isVoiceReady, and the hands-free lens is the one place
  // a false "ready" is read ALOUD rather than looked at, so this pins the whole filter: a session on a
  // machine nobody can act on never enters the queue, however healthy its last-known state looks.
  it("excludes an unreachable session from the voice queue, keeping the reachable ones in order", () => {
    const rows = [
      { id: "awake-1", inputs: READY_FOR_THIS_TURN },
      { id: "asleep", inputs: { ...READY_FOR_THIS_TURN, reachable: false } },
      { id: "awake-2", inputs: READY_FOR_THIS_TURN },
    ];
    const queue = rows.filter((r) => isVoiceReady(r.inputs)).map((r) => r.id);
    expect(queue).toEqual(["awake-1", "awake-2"]);
  });

  it("empties the voice queue when every waiting session is on an unreachable machine", () => {
    const rows = [
      { id: "asleep-1", inputs: { ...READY_FOR_THIS_TURN, reachable: false } },
      { id: "asleep-2", inputs: { ...READY_FOR_THIS_TURN, reachable: false } },
    ];
    expect(rows.filter((r) => isVoiceReady(r.inputs))).toEqual([]);
  });
});
