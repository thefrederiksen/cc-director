import { describe, expect, it } from "vitest";
import { isAudioUnavailable, type VoiceAvailabilityInputs } from "./voiceAvailability";

// The reported bug, in the owner's words: "when I go into voice mode, it often shows that I don't have
// the voice generated, even though it's there and it plays it. So it says voice unavailable, no
// narration to play, and then it plays the voice."
//
// That is one defect wearing two hats, and both are pinned below: the screen rendered its failure copy
// during windows in which it had not yet looked. These tests are written from the two windows, so a
// future edit that goes back to deriving "unavailable" by elimination fails here rather than on a phone.

/** The state in which voice genuinely IS unavailable: we looked, and nothing is coming. */
const LOOKED_AND_NOTHING_THERE: VoiceAvailabilityInputs = {
  voiceOn: true,
  sessionKnown: true,
  voiceKnown: true,
  speaking: false,
  agentWorking: false,
  gatewayPreparing: false,
  phoneDownloadPending: false,
  clipDownloading: false,
  hasEnableNote: false,
};

describe("isAudioUnavailable", () => {
  // The control. If this ever goes false the verdict has been guarded into never firing at all, and a
  // genuinely voiceless session would spin on the working card forever.
  it("says unavailable once we have looked and nothing is on its way", () => {
    expect(isAudioUnavailable(LOOKED_AND_NOTHING_THERE)).toBe(true);
  });

  // Window 1: pollDone means "I know the SESSION", not "I know the VOICE". The poll sets it when
  // listSessions resolves, which is before it awaits getWingmanVoice - so the whole voice round trip
  // ran with the verdict already firing, and the narration arrived immediately after and played.
  it("stays silent while the voice fetch has not resolved, even though the session is known", () => {
    expect(isAudioUnavailable({ ...LOOKED_AND_NOTHING_THERE, voiceKnown: false })).toBe(false);
  });

  // Window 2: the only download guard read session.voiceAudioReady - the GATEWAY's cache. What plays
  // is the clip in THIS PHONE's Cache Storage. When the Gateway had dropped its copy but the phone was
  // warming its own, every guard was false, the red banner rendered - and then the clip played.
  it("stays silent while this phone is warming a clip the Gateway no longer holds", () => {
    expect(
      isAudioUnavailable({
        ...LOOKED_AND_NOTHING_THERE,
        phoneDownloadPending: false, // the Gateway says it has no audio...
        clipDownloading: true, // ...but the phone is pulling the bytes it does have
      }),
    ).toBe(false);
  });

  it("stays silent before the session itself is known", () => {
    expect(isAudioUnavailable({ ...LOOKED_AND_NOTHING_THERE, sessionKnown: false })).toBe(false);
  });

  it("never fires for a session that is not in voice mode - that is the off card, not a failure", () => {
    expect(isAudioUnavailable({ ...LOOKED_AND_NOTHING_THERE, voiceOn: false, voiceKnown: false })).toBe(false);
  });

  it("stays silent while a clip is playable", () => {
    expect(isAudioUnavailable({ ...LOOKED_AND_NOTHING_THERE, speaking: true })).toBe(false);
  });

  it("stays silent while the agent is working, where the narration is withheld on purpose", () => {
    expect(isAudioUnavailable({ ...LOOKED_AND_NOTHING_THERE, agentWorking: true })).toBe(false);
  });

  it("stays silent while the Gateway is synthesizing the narration", () => {
    expect(isAudioUnavailable({ ...LOOKED_AND_NOTHING_THERE, gatewayPreparing: true })).toBe(false);
  });

  it("stays silent while the Gateway holds audio the phone has not pulled yet", () => {
    expect(isAudioUnavailable({ ...LOOKED_AND_NOTHING_THERE, phoneDownloadPending: true })).toBe(false);
  });

  it("stays silent when the Gateway said there is nothing to narrate yet", () => {
    expect(isAudioUnavailable({ ...LOOKED_AND_NOTHING_THERE, hasEnableNote: true })).toBe(false);
  });
});
