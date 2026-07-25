import { authHeaders, gatewayFetch } from "../api/client";
import { analyzeMicQuality, judgeMicQuality } from "./micQuality";

// Background microphone-quality reporting for ORDINARY dictation.
//
// The Test microphone check answers "how is my microphone right now", once, when someone goes
// looking. This answers the question nobody goes looking for: how is my microphone across all the
// dictating I actually do, and is a particular headset the reason my transcripts are poor. That is
// the one a user cannot answer for themselves, because the defect that matters most - a Bluetooth
// hands-free link - sounds merely dull to a human ear.
//
// THREE RULES THIS FILE EXISTS TO KEEP:
//
// 1. IT NEVER DELAYS A WORD. Measuring runs after the caller already has its transcript on the way,
//    and the send is fire-and-forget. A user's dictation must never wait on our analytics.
// 2. IT NEVER FAILS A DICTATION. Every path here swallows its own errors to a console line. A
//    Gateway that is unreachable, an account with no tenant, a decode that went wrong - none of them
//    may turn a working dictation into an error the user sees.
// 3. IT COSTS NO SECOND DECODE. The samples come from the transcode the dictation path already
//    performed (wav.ts hands back the native-rate mono buffer), so the only new work is the analysis
//    itself over a clip already in memory.
//
// WHAT IS SENT: measurements and the microphone's name. No audio, and no transcript. The clip itself
// stays where it was; this is a handful of numbers per dictation.

/** Below this there is not enough audio for a stable reading, and a shaky measurement reported as
 *  fact is worse than no measurement. Chosen from real dictation: the Gateway's own archive of 212
 *  real clips had nothing shorter than 2.3 seconds, so this discards almost nothing in practice
 *  while keeping the short-utterance case - which has never been measured - out of the data. */
const MIN_REPORTABLE_SECONDS = 3;

export interface DictationQualitySample {
  /** Which surface produced it, so a phone and a desktop can be told apart later. */
  source: string;
  /** The microphone's name as the operating system reports it. Empty when the browser withholds it. */
  device: string;
  durationSeconds: number;
  sampleRate: number;
  speechLevelDb: number;
  noiseFloorDb: number;
  signalToNoiseDb: number;
  clippedFraction: number;
  highBandRatioDb: number;
  narrowband: boolean;
  /** "good" | "fair" | "poor", folded by the same rules the Test microphone screen renders. */
  rating: string;
  /** Identifiers of the issues found, joined by "+". Empty when the microphone is good. */
  issues: string;
}

/**
 * Measure a finished dictation clip and post the result. Returns the sample it sent, or null when
 * there was nothing worth reporting - callers ignore the return; it exists so tests can assert what
 * would have been sent without a network.
 *
 * A clip with no detectable speech is deliberately NOT reported. It is far more likely to be a
 * false start or a moment of silence than a broken microphone, and recording it would drag every
 * average down and eventually produce a warning about a microphone that is fine.
 */
export function measureDictationQuality(
  samples: Float32Array,
  sampleRate: number,
  device: string,
  source: string,
): DictationQualitySample | null {
  if (samples.length / sampleRate < MIN_REPORTABLE_SECONDS) return null;

  const report = analyzeMicQuality(samples, sampleRate);
  if (!report.heardSpeech) return null;

  const verdict = judgeMicQuality(report);
  return {
    source,
    device,
    durationSeconds: Math.round(report.durationSeconds * 10) / 10,
    sampleRate: report.sampleRate,
    speechLevelDb: Math.round(report.speechLevelDb * 10) / 10,
    noiseFloorDb: Math.round(report.noiseFloorDb * 10) / 10,
    signalToNoiseDb: Math.round(report.signalToNoiseDb * 10) / 10,
    clippedFraction: report.clippedFraction,
    // A silent high band reads as -Infinity, which is not JSON. Floor it at a value that still says
    // "nothing up there" without becoming null on the wire.
    highBandRatioDb: Number.isFinite(report.highBandRatioDb) ? Math.round(report.highBandRatioDb * 10) / 10 : -120,
    narrowband: report.narrowband,
    rating: verdict.rating,
    issues: verdict.issues.map((i) => i.id).join("+"),
  };
}

/**
 * Measure and send, swallowing every failure. Call this AFTER the dictation is already on its way.
 * Deliberately returns void: there is no outcome a caller should branch on, and offering one would
 * invite somebody to await it on the path that carries the user's words.
 */
export function reportDictationQuality(
  samples: Float32Array,
  sampleRate: number,
  device: string,
  source: string,
): void {
  let sample: DictationQualitySample | null;
  try {
    sample = measureDictationQuality(samples, sampleRate, device, source);
  } catch (err) {
    console.warn(`[quality] could not measure the clip: ${err instanceof Error ? err.message : String(err)}`);
    return;
  }
  if (sample === null) return;

  void gatewayFetch("/voice-quality/sample", {
    method: "POST",
    headers: { "Content-Type": "application/json", ...authHeaders() },
    body: JSON.stringify(sample),
  }).catch((err: unknown) => {
    console.warn(`[quality] could not report the sample: ${err instanceof Error ? err.message : String(err)}`);
  });
}
