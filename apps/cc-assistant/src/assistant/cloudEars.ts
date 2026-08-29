// Hearing a command with Wilson's own microphone, and having it written down in the cloud.
//
// The browser's recogniser still hears the wake word (it is free, it is already running, and the
// Pi will get a local wake-word model of its own later). From the wake on, THIS takes over:
//
//   1. start collecting from the voice ring, including the second before the wake fired, because
//      the wake is detected from an interim transcript and the command has usually already begun;
//   2. keep collecting until the person has spoken and then gone quiet for a while, or a ceiling
//      is hit; a person who says the wake word and nothing else gets a short clip and a follow-up;
//   3. send the clip to api/hear (Whisper on Groq) with the household's spellings as hints;
//   4. the wake word is cut off the front of what comes back, and the rest is the command.
//
// Endpointing is done on loudness per quarter-second chunk. It is crude and it is honest: the
// numbers are on the debug screen, and a clip that was cut early or late can be seen and heard
// about. The Pi will do exactly this, which is the point of doing it here first.

import { MODEL_SAMPLE_RATE } from "../audio/pcmCapture";
import type { VoiceRing } from "./speakerId";

/** Audio kept from before the wake fired. */
export const PRE_ROLL_SECONDS = 1.0;
/** A chunk louder than this is speech. Below it, for long enough, the person has stopped. */
export const SPEECH_PEAK = 0.03;
/** Quiet chunks in a row that end the utterance, once speech has been heard. 3 x 0.25 s. */
export const END_SILENCE_CHUNKS = 3;
/** Chunks to wait for speech to begin after the wake before giving up. 12 x 0.25 s. */
export const START_TIMEOUT_CHUNKS = 12;
/** The longest a command may be, in chunks. 48 x 0.25 s. */
export const MAX_CHUNKS = 48;

export interface Captured {
  readonly samples: Float32Array;
  readonly seconds: number;
  /** Why the capture ended: what the debug screen shows. */
  readonly endedBy: "silence" | "ceiling" | "no-speech" | "stopped";
  readonly heardSpeech: boolean;
}

/**
 * Collect one utterance from the ring, starting now (plus pre-roll), ending on silence.
 * `stop` can be called to end it early (the assistant was stopped). Never throws for a quiet room:
 * a clip that holds no speech comes back marked "no-speech" and the caller decides.
 */
export function captureUtterance(ring: VoiceRing): { done: Promise<Captured>; stop(): void } {
  const parts: Float32Array[] = [];
  const preRoll = ring.recent(PRE_ROLL_SECONDS);
  if (preRoll !== null) {
    parts.push(preRoll);
  }
  let chunks = 0;
  let quiet = 0;
  let heardSpeech = false;
  let unsubscribe = () => undefined as void;
  let finish: (why: Captured["endedBy"]) => void = () => undefined;

  const done = new Promise<Captured>((resolve) => {
    finish = (why) => {
      unsubscribe();
      const total = parts.reduce((n, p) => n + p.length, 0);
      const samples = new Float32Array(total);
      let at = 0;
      for (const p of parts) {
        samples.set(p, at);
        at += p.length;
      }
      resolve({ samples, seconds: total / MODEL_SAMPLE_RATE, endedBy: why, heardSpeech });
    };
    unsubscribe = ring.subscribe((chunk) => {
      parts.push(chunk.samples.slice());
      chunks += 1;
      if (chunk.peak >= SPEECH_PEAK) {
        heardSpeech = true;
        quiet = 0;
      } else {
        quiet += 1;
      }
      if (heardSpeech && quiet >= END_SILENCE_CHUNKS) {
        finish("silence");
      } else if (!heardSpeech && chunks >= START_TIMEOUT_CHUNKS) {
        finish("no-speech");
      } else if (chunks >= MAX_CHUNKS) {
        finish("ceiling");
      }
    });
  });

  return { done, stop: () => finish("stopped") };
}

/** 16-bit mono WAV bytes from float samples, for the hearing service. */
export function toWav(samples: Float32Array, sampleRate = MODEL_SAMPLE_RATE): Uint8Array {
  const bytes = new Uint8Array(44 + samples.length * 2);
  const view = new DataView(bytes.buffer);
  const ascii = (offset: number, text: string) => {
    for (let i = 0; i < text.length; i += 1) {
      view.setUint8(offset + i, text.charCodeAt(i));
    }
  };
  ascii(0, "RIFF");
  view.setUint32(4, 36 + samples.length * 2, true);
  ascii(8, "WAVE");
  ascii(12, "fmt ");
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  ascii(36, "data");
  view.setUint32(40, samples.length * 2, true);
  for (let i = 0; i < samples.length; i += 1) {
    const s = Math.max(-1, Math.min(1, samples[i]));
    view.setInt16(44 + i * 2, s < 0 ? s * 32768 : s * 32767, true);
  }
  return bytes;
}

export interface Heard {
  readonly text: string;
  readonly elapsedMs: number;
  readonly seconds: number;
}

/** Send a clip to be written down. Rejects with a plain message when the service cannot. */
export async function transcribeClip(samples: Float32Array, hints: string[]): Promise<Heard> {
  const query = hints.length > 0 ? `?hints=${encodeURIComponent(hints.join(","))}` : "";
  const response = await fetch(`${import.meta.env.BASE_URL}api/hear${query}`, {
    method: "POST",
    headers: { "Content-Type": "audio/wav" },
    body: toWav(samples),
  });
  const body = (await response.json()) as Heard & { error?: string };
  if (!response.ok) {
    throw new Error(body.error ?? `Hearing failed (${response.status}).`);
  }
  return body;
}
