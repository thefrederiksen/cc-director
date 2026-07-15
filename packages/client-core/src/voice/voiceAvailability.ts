// The "voice is unavailable" verdict for the Voice mode screen, as a pure function.
//
// It lives in its own module, with no imports, because it is the one rule on this screen that was
// getting silently mis-derived and it needs to be directly testable without a React tree.
//
// THE RULE: "unavailable" is a POSITIVE finding. It is what we render when something actually told us
// there is no narration - never the gap left over when no other state happened to match.
//
// It used to be pure elimination:
//
//   audioUnavailable = voiceOn && pollDone && !speaking && !agentWorking
//                      && !gatewayPreparing && !phoneDownloadPending && enableNote.length === 0
//
// Nothing in that asserts the narration is missing. So the screen announced "No narration is ready to
// play" during every window in which it had simply not looked yet - and then played the narration a
// second later when the answer arrived. Two windows did it, and both are real:
//
//   1. pollDone means "I know the SESSION", not "I know the VOICE". The poll sets it the moment
//      listSessions resolves, which is BEFORE it awaits getWingmanVoice - so the entire voice round
//      trip ran with pollDone already true and the verdict already firing. `voiceKnown` closes that:
//      it is set only once the voice fetch has actually RESOLVED.
//
//   2. The only download guard, phoneDownloadPending, is stamped from session.voiceAudioReady - the
//      GATEWAY's cache (GatewayHost wires it to WingmanVoiceService.HasVoice). But what actually
//      plays is the clip held on THIS PHONE, in Cache Storage, warmed by ensureClip with no network
//      at all. Those are two different caches. While the phone was warming a clip the Gateway no
//      longer had, every guard was false and the red banner rendered on top of audio that was about
//      to play. `clipDownloading` closes that: to know what the phone has, ask the phone.
//
// Absence of evidence was being rendered as evidence of absence. While we do not know, the caller
// falls through to the working card and stays quiet; we only say "unavailable" once something said so.

export interface VoiceAvailabilityInputs {
  /** This is a voice session (confirmed by the roster, or optimistically just switched on). */
  voiceOn: boolean;
  /** A poll has resolved THIS SESSION. Not a statement about its voice. */
  sessionKnown: boolean;
  /** The getWingmanVoice fetch has RESOLVED for this session - we have actually asked. */
  voiceKnown: boolean;
  /** A phone-ready clip is playable right now. */
  speaking: boolean;
  /** The agent has resumed: the finished-turn narration is stale and is not offered. */
  agentWorking: boolean;
  /** The Gateway says it is synthesizing this turn's narration now. */
  gatewayPreparing: boolean;
  /** The Gateway holds audio this phone has not finished pulling down. */
  phoneDownloadPending: boolean;
  /** This phone is fetching or warming the clip bytes right now (clips.ts "downloading"). */
  clipDownloading: boolean;
  /** The Gateway told us there is genuinely nothing to narrate yet (a fresh/text-only session). */
  hasEnableNote: boolean;
}

/** True only when we have looked and something told us there is no narration to play. */
export function isAudioUnavailable(i: VoiceAvailabilityInputs): boolean {
  // Not a voice session: the screen shows the off card, not a failure.
  if (!i.voiceOn) return false;

  // We have not looked yet. Not knowing is not the same as nothing being there - this is the whole
  // bug. Both facts are required: the session AND the voice.
  if (!i.sessionKnown) return false;
  if (!i.voiceKnown) return false;

  // Audio is playable, or is deliberately withheld because the turn moved on.
  if (i.speaking) return false;
  if (i.agentWorking) return false;

  // Audio is on its way - from the Gateway, or into this phone's own cache.
  if (i.gatewayPreparing) return false;
  if (i.phoneDownloadPending) return false;
  if (i.clipDownloading) return false;

  // There is nothing to narrate yet, which the working card already says truthfully in its own words.
  if (i.hasEnableNote) return false;

  return true;
}
