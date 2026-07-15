// What a session's voice control on the ROSTER is allowed to say, as a pure function.
//
// THE RULE: the roster's play-triangle must mean exactly what the Voice screen means by "speaking".
// A triangle is a promise - "tap this and you will hear this session's latest turn, right now, with no
// wait". The roster is not allowed to make that promise on weaker evidence than the screen it hands
// you to.
//
// It used to make it on much weaker evidence. Home.tsx asked one question:
//
//   if (clip.phase === "ready") -> draw the triangle
//
// which is "do I hold ANY bytes for this session?" - with no regard for WHICH TURN those bytes narrate.
// The Voice screen asks the harder question (useVoiceMode.ts, phoneReady):
//
//   clip.phase === "ready" && clip.generatedAt === voice.generatedAt
//
// "do I hold the bytes for the turn that is waiting RIGHT NOW?" Two different questions, and on
// 2026-07-15 they gave two different answers on the same session at the same moment.
//
// How they came apart: the clip store is sticky. syncVoiceSessions only downloads for sessions the
// Gateway says have audio (voiceAudioReady), so when the speech service went down and the Gateway
// stopped producing narration, those sessions dropped out of the sync entirely - and the PREVIOUS
// turn's clip sat in the store still marked "ready". The roster saw "ready", drew a green triangle,
// and the owner tapped it. The Voice screen then checked that clip against the current turn, correctly
// refused it as stale, and rendered "Voice service down". The triangle was not lying about holding a
// file; it was lying about which turn the file was for.
//
// Two guards close that, and both are inputs the roster already had in its hand and ignored:
//
//   1. voiceUnavailable - the Gateway TELLS the roster when it cannot make voice (GatewayEndpoints
//      stamps SessionDto.VoiceUnavailable on every session in the poll: out of credits, no key, cap
//      reached, or the speech service is down). The roster rendered play controls straight through it.
//      A session whose voice the Gateway has disowned shows "down", never a triangle.
//
//   2. gatewayGenerating - a NEWER narration is being synthesized, which is a positive statement that
//      the clip on the phone is last turn's. Held bytes are not offered while a replacement is coming.
//
//   3. gatewayHasAudio - the Gateway says it holds narration for this session's latest turn.
//
// Guard 3 deserves its own note, because voiceAvailability.ts argues the OPPOSITE for the Voice screen
// and is right to: what plays is the clip in THIS PHONE's cache, so the screen must never withhold
// playable audio just because the Gateway has since dropped its copy (that module's window 2).
//
// The reasoning does not carry over, because the two surfaces know different things. The Voice screen
// fetches getWingmanVoice live, so it learns the CURRENT turn's stamp first-hand and can compare the
// phone's clip against it. The roster has no such fetch - it reads the stamp from the same cached voice
// metadata that syncVoiceSessions writes, and that sync only runs for sessions with voiceAudioReady.
// So the moment the Gateway stops advertising audio, the roster's "current turn" and the clip it is
// comparing go stale TOGETHER, and agree with each other perfectly while both describe last turn.
// (A Gateway restart does exactly this: its voice cache is in memory, so voiceAudioReady drops across
// the fleet with no outage stamped anywhere.) voiceAudioReady is the only LIVE evidence the roster has
// that a current narration exists at all, so on this surface it is a gate. Found in review.
//
// The cost is a false NEGATIVE - a missing triangle where the phone could in fact have played - which
// is the right way for this to fail. The roster promises out loud; a promise it cannot substantiate is
// the bug being fixed here, and silence is not.
//
// RESIDUAL, known and accepted: between the poll that first sees voiceAudioReady for a NEW turn and the
// getWingmanVoice that refreshes the cached stamp (one Gateway round trip later), the roster still
// holds last turn's stamp and last turn's ready clip, which match - so a stale triangle can flash for
// well under a second. It closes itself: ensureClip immediately re-stamps the clip store to the new
// turn as "downloading", which flips the row to the spinner. Tapping inside that window lands on the
// working card (the screen's own live fetch sees the mismatch), never the "Voice service down" screen
// this fix is about. Closing it completely needs a per-turn narration id on SessionDto, which is not
// worth a new contract field for a sub-second flash with a benign landing.

/** The clip download phases, mirrored from clips.ts (kept structural so this module imports nothing). */
export type VoiceClipPhase = "none" | "downloading" | "ready" | "error";

export interface VoiceRowInputs {
  /** This session is in voice mode (SessionDto.voiceMode). */
  voiceMode: boolean;
  /** The agent has resumed (blue): the finished-turn narration is stale and is not offered. */
  agentWorking: boolean;
  /**
   * The Gateway reported that it cannot make voice for this session - out of credits, no key, cap
   * reached, or the speech service is down (SessionDto.voiceUnavailable is non-null).
   */
  voiceUnavailable: boolean;
  /** The Gateway is synthesizing this turn's narration now (SessionDto.voiceGenerating). */
  gatewayGenerating: boolean;
  /** The Gateway holds audio for this session's latest turn (SessionDto.voiceAudioReady). */
  gatewayHasAudio: boolean;
  /** This phone is pulling clip bytes down right now (clips.ts "downloading"). */
  clipDownloading: boolean;
  /**
   * This phone holds playable bytes FOR THE CURRENT TURN - the whole point of this module. Callers
   * derive it with isPhoneReady(sid, currentGeneratedAt); a clip held for an older turn is false.
   */
  phoneReadyForCurrentTurn: boolean;
  /**
   * The narration has spoken TEXT (WingmanVoice.spoken is non-empty). `ready` and `spoken` are
   * independent fields on the contract, so a ready-but-wordless narration is representable - and the
   * Voice screen refuses to speak one (its `speaking` requires voice.spoken.length > 0). The roster
   * must refuse it too, or it would draw a triangle onto exactly the state the screen declines to
   * play, which is this bug wearing a different hat. Found in review.
   */
  hasSpokenText: boolean;
}

/**
 * What the roster shows for a session's voice:
 * - "ready"     a green play-triangle: this turn's audio is on the phone, tap and it speaks.
 * - "preparing" a yellow spinner: audio for this turn is being made or downloaded.
 * - "down"      the Gateway cannot make voice for this session and says so.
 * - "none"      nothing to show (not a voice session, or the agent is working again).
 */
export type VoiceRowState = "none" | "down" | "preparing" | "ready";

export function voiceRowState(i: VoiceRowInputs): VoiceRowState {
  // Not a voice session: the roster says nothing about voice.
  if (!i.voiceMode) return "none";

  // The agent has resumed, so the finished-turn narration is stale. This is checked BEFORE
  // voiceUnavailable: a working session's row already says "working", and stacking a voice-down pill
  // onto it would report an outage the reader can do nothing about on a row that is not waiting.
  if (i.agentWorking) return "none";

  // The Gateway has disowned this session's voice. Never a triangle, never a spinner - a triangle here
  // is the reported bug, and a spinner would promise an arrival that is not coming.
  if (i.voiceUnavailable) return "down";

  // A newer narration is on its way, which means anything held is last turn's. Not offered.
  if (i.gatewayGenerating) return "preparing";

  // The promise the triangle makes, in full: the Gateway confirms a narration exists for the latest
  // turn, this phone holds THAT turn's bytes, and there are words in it. Every clause is load-bearing;
  // dropping any one of them is a way this has already gone wrong.
  if (i.gatewayHasAudio && i.phoneReadyForCurrentTurn && i.hasSpokenText) return "ready";

  // Audio exists or is arriving, but this phone cannot play it yet.
  if (i.gatewayHasAudio || i.clipDownloading) return "preparing";

  return "none";
}

/** True when this row belongs in the roster's Voice tab: it has voice ready to play, right now. */
export function isVoiceReady(i: VoiceRowInputs): boolean {
  return voiceRowState(i) === "ready";
}
