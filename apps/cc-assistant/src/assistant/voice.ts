// Wilson's voice: a WAV stream from the server, played as it arrives.
//
// The server (api/speak.js) starts sending audio about a quarter of a second after being asked, and
// keeps sending while the rest of the sentence is still being made. Waiting for the whole file
// before playing would add the entire generation time, most of a second for one sentence, on top.
// So this reads the stream, turns each arriving block of 16-bit samples into an AudioBuffer, and
// schedules it to play exactly where the previous block ends. The ear hears one continuous voice.
//
// One AudioContext for the life of the page, created on the Start press: browsers only allow audio
// to start from a user gesture, and a context made later would begin suspended and play nothing.

const BASE = import.meta.env.BASE_URL;

/** Samples per scheduled block. 2400 at 24 kHz is 100 ms: small enough to start fast, large enough to be cheap. */
const BLOCK_SAMPLES = 2400;
/** How far ahead of "now" the first block is placed, to cover scheduling jitter. */
const LEAD_SECONDS = 0.04;

export const VOICES = ["austin", "daniel", "troy", "hannah", "diana", "autumn"] as const;
export type VoiceName = (typeof VOICES)[number];
export const DEFAULT_VOICE: VoiceName = "austin";

let context: AudioContext | null = null;
let current: { abort: AbortController; sources: AudioBufferSourceNode[] } | null = null;

/** Call from a user gesture, once. Makes and resumes the context so later speech is allowed to play. */
export async function unlockVoice(): Promise<void> {
  if (context === null) {
    context = new AudioContext();
  }
  if (context.state !== "running") {
    await context.resume();
  }
}

export interface Spoken {
  /** Milliseconds from asking to the first sound. The number that matters; shown in diagnostics. */
  readonly firstSoundMs: number;
  /** Seconds of audio played. */
  readonly seconds: number;
}

/**
 * Speak a sentence in Wilson's voice. Resolves when the last sample has played, or when cut off.
 * Rejects with a plain message when the voice cannot be reached, so the caller can show it.
 */
export function speakStreamed(text: string, voice: VoiceName, onStart?: () => void): Promise<Spoken> {
  return new Promise<Spoken>((resolve, reject) => {
    if (context === null) {
      reject(new Error("The voice was not unlocked. Press Start first."));
      return;
    }
    const ctx = context;
    stopVoice();
    const abort = new AbortController();
    const mine = { abort, sources: [] as AudioBufferSourceNode[] };
    current = mine;

    const askedAt = performance.now();
    let firstSoundMs = -1;
    let nextStart = 0;
    let totalSeconds = 0;
    let sampleRate = 24000;
    let headerDone = false;
    let held = new Uint8Array(0);
    let finishedReading = false;
    let scheduled = 0;
    let ended = 0;

    const maybeResolve = () => {
      if (finishedReading && ended >= scheduled) {
        if (current === mine) {
          current = null;
        }
        resolve({ firstSoundMs: firstSoundMs < 0 ? 0 : firstSoundMs, seconds: totalSeconds });
      }
    };

    const schedule = (pcm: Int16Array) => {
      const buffer = ctx.createBuffer(1, pcm.length, sampleRate);
      const channel = buffer.getChannelData(0);
      for (let i = 0; i < pcm.length; i += 1) {
        channel[i] = pcm[i] / 32768;
      }
      const source = ctx.createBufferSource();
      source.buffer = buffer;
      source.connect(ctx.destination);
      if (nextStart < ctx.currentTime + LEAD_SECONDS) {
        nextStart = ctx.currentTime + LEAD_SECONDS;
      }
      if (firstSoundMs < 0) {
        firstSoundMs = Math.round(performance.now() - askedAt + LEAD_SECONDS * 1000);
        onStart?.();
      }
      source.onended = () => {
        ended += 1;
        maybeResolve();
      };
      source.start(nextStart);
      nextStart += buffer.duration;
      totalSeconds += buffer.duration;
      scheduled += 1;
      mine.sources.push(source);
    };

    // Whole samples only: a chunk can end halfway through a 16-bit sample, and the odd byte is
    // carried to the next chunk rather than played as noise.
    let carry = new Uint8Array(0);
    const feed = (bytes: Uint8Array) => {
      const joined = new Uint8Array(carry.length + bytes.length);
      joined.set(carry);
      joined.set(bytes, carry.length);
      const usable = joined.length - (joined.length % 2);
      const samples = new Int16Array(joined.buffer.slice(0, usable));
      carry = joined.slice(usable);
      for (let at = 0; at < samples.length; at += BLOCK_SAMPLES) {
        schedule(samples.subarray(at, Math.min(at + BLOCK_SAMPLES, samples.length)));
      }
    };

    const parseHeader = (bytes: Uint8Array): Uint8Array | null => {
      const joined = new Uint8Array(held.length + bytes.length);
      joined.set(held);
      joined.set(bytes, held.length);
      held = joined;
      const dataAt = findAscii(held, "data");
      if (dataAt < 0 || held.length < dataAt + 8) {
        return null;
      }
      const fmtAt = findAscii(held, "fmt ");
      if (fmtAt >= 0) {
        const view = new DataView(held.buffer, held.byteOffset, held.byteLength);
        sampleRate = view.getUint32(fmtAt + 12, true);
      }
      headerDone = true;
      const rest = held.slice(dataAt + 8);
      held = new Uint8Array(0);
      return rest;
    };

    (async () => {
      const response = await fetch(`${BASE}api/speak`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ text, voice }),
        signal: abort.signal,
      });
      if (!response.ok || response.body === null) {
        let message = `The voice failed (${response.status}).`;
        try {
          const body = (await response.json()) as { error?: string };
          if (body.error) {
            message = body.error;
          }
        } catch {
          // The status is the message.
        }
        throw new Error(message);
      }
      const reader = response.body.getReader();
      for (;;) {
        const { done, value } = await reader.read();
        if (done) {
          break;
        }
        if (!headerDone) {
          const pcm = parseHeader(value);
          if (pcm !== null && pcm.length > 0) {
            feed(pcm);
          }
        } else {
          feed(value);
        }
      }
      finishedReading = true;
      maybeResolve();
    })().catch((error: unknown) => {
      if (current === mine) {
        current = null;
      }
      if (abort.signal.aborted) {
        resolve({ firstSoundMs: firstSoundMs < 0 ? 0 : firstSoundMs, seconds: totalSeconds });
        return;
      }
      reject(error instanceof Error ? error : new Error(String(error)));
    });
  });
}

/** Cut the voice off now, for when somebody interrupts. */
export function stopVoice(): void {
  if (current === null) {
    return;
  }
  const stopping = current;
  current = null;
  stopping.abort.abort();
  for (const source of stopping.sources) {
    try {
      source.onended = null;
      source.stop();
    } catch {
      // Already finished.
    }
  }
}

function findAscii(bytes: Uint8Array, text: string): number {
  outer: for (let i = 0; i + text.length <= bytes.length; i += 1) {
    for (let j = 0; j < text.length; j += 1) {
      if (bytes[i + j] !== text.charCodeAt(j)) {
        continue outer;
      }
    }
    return i;
  }
  return -1;
}
