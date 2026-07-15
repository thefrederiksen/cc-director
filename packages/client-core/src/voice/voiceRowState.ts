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
// Note what is deliberately NOT a gate: the Gateway currently holding the audio (voiceAudioReady).
// What plays is the clip in THIS PHONE's cache, and those are two different caches - gating "ready" on
// the Gateway's copy is the exact mistake voiceAvailability.ts documents at length (its window 2). The
// staleness fix is "the bytes must be for the CURRENT turn", not "the Gateway must still have them".

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

  // The promise the triangle makes: THIS turn's bytes, on this phone, playable with no wait.
  if (i.phoneReadyForCurrentTurn) return "ready";

  // Audio exists or is arriving, but this phone cannot play it yet.
  if (i.gatewayHasAudio || i.clipDownloading) return "preparing";

  return "none";
}

/** True when this row belongs in the roster's Voice tab: it has voice ready to play, right now. */
export function isVoiceReady(i: VoiceRowInputs): boolean {
  return voiceRowState(i) === "ready";
}
