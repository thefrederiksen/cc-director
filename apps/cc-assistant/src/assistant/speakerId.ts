// Who is speaking, from how the voice sounds.
//
// A speaker-verification model (WavLM base plus SV, via transformers.js) turns a few seconds of
// audio into a vector of 512 numbers that describes the voice, not the words. Two clips of the same
// person land close together; two people land apart. Enrolment stores a few of those vectors per
// person on the Wilson service; every command is then matched against them (api/voice.js).
//
// Raw audio never leaves the device. Only the vector does, and only to the local service.
//
// The platform recogniser gives us text and swallows the audio, so this keeps its own microphone
// open in parallel and remembers the last few seconds. When a command is heard, the seconds that
// just went by are the utterance.
//
// The model is fetched from the Hugging Face hub on first use (about 90 MB, cached by the browser
// afterwards). Until it has loaded, identification reports "not ready" and the turn proceeds without
// a name, which is the correct answer rather than a guess.

import { startPcmCapture, type PcmCapture, MODEL_SAMPLE_RATE } from "../audio/pcmCapture";

const MODEL_ID = "Xenova/wavlm-base-plus-sv";
const RING_SECONDS = 8;
const CHUNK_SECONDS = 0.25;
/** Below this peak the clip is silence, and a silence embedding matches nobody, so do not try. */
const MIN_PEAK = 0.02;

export interface SpeakerModelStatus {
  readonly state: "idle" | "loading" | "ready" | "failed";
  readonly detail: string;
}

type Embedder = (samples: Float32Array) => Promise<number[]>;

let embedder: Embedder | null = null;
let loading: Promise<Embedder> | null = null;
let status: SpeakerModelStatus = { state: "idle", detail: "not loaded" };
const watchers = new Set<(s: SpeakerModelStatus) => void>();

function setStatus(next: SpeakerModelStatus): void {
  status = next;
  for (const w of watchers) {
    w(next);
  }
}

export function speakerModelStatus(): SpeakerModelStatus {
  return status;
}

export function watchSpeakerModel(watcher: (s: SpeakerModelStatus) => void): () => void {
  watchers.add(watcher);
  watcher(status);
  return () => watchers.delete(watcher);
}

/** Load the model once. Safe to call repeatedly; later calls share the first load. */
export function loadSpeakerModel(): Promise<Embedder> {
  if (embedder !== null) {
    return Promise.resolve(embedder);
  }
  if (loading !== null) {
    return loading;
  }
  setStatus({ state: "loading", detail: "fetching the voice model" });
  loading = (async () => {
    const startedAt = performance.now();
    const transformers = await import("@huggingface/transformers");
    const { AutoProcessor, WavLMForXVector } = transformers as unknown as {
      AutoProcessor: { from_pretrained(id: string): Promise<(audio: Float32Array) => Promise<Record<string, unknown>>> };
      WavLMForXVector: { from_pretrained(id: string, options?: Record<string, unknown>): Promise<(inputs: Record<string, unknown>) => Promise<{ embeddings: { data: Float32Array } }>> };
    };
    const processor = await AutoProcessor.from_pretrained(MODEL_ID);
    const model = await WavLMForXVector.from_pretrained(MODEL_ID);
    const made: Embedder = async (samples) => {
      const inputs = await processor(samples);
      const { embeddings } = await model(inputs);
      const out = Array.from(embeddings.data);
      // Unit length, so cosine similarity on the server is a plain dot product of comparable vectors.
      const norm = Math.sqrt(out.reduce((s, v) => s + v * v, 0)) || 1;
      return out.map((v) => v / norm);
    };
    embedder = made;
    setStatus({ state: "ready", detail: `voice model ready in ${Math.round(performance.now() - startedAt)} ms` });
    return made;
  })();
  loading.catch((error: unknown) => {
    loading = null;
    setStatus({ state: "failed", detail: `voice model failed to load: ${error instanceof Error ? error.message : String(error)}` });
  });
  return loading;
}

/** A rolling window of the last few seconds from the microphone, at the model's sample rate. */
export class VoiceRing {
  private capture: PcmCapture | null = null;
  private ring = new Float32Array(RING_SECONDS * MODEL_SAMPLE_RATE);
  private filled = 0;
  private lastPeak = 0;

  async start(workletUrl: string): Promise<void> {
    if (this.capture !== null) {
      return;
    }
    this.capture = await startPcmCapture(CHUNK_SECONDS, workletUrl, (chunk) => {
      const n = chunk.samples.length;
      if (n >= this.ring.length) {
        this.ring.set(chunk.samples.subarray(n - this.ring.length));
      } else {
        this.ring.copyWithin(0, n);
        this.ring.set(chunk.samples, this.ring.length - n);
      }
      this.filled = Math.min(this.ring.length, this.filled + n);
      this.lastPeak = chunk.peak;
    });
  }

  async stop(): Promise<void> {
    const c = this.capture;
    this.capture = null;
    this.filled = 0;
    if (c !== null) {
      await c.stop();
    }
  }

  get running(): boolean {
    return this.capture !== null;
  }

  /** The most recent `seconds` of audio, or null when there is not enough yet or it is silence. */
  recent(seconds: number): Float32Array | null {
    const wanted = Math.min(this.ring.length, Math.round(seconds * MODEL_SAMPLE_RATE));
    if (this.filled < wanted) {
      return null;
    }
    const clip = this.ring.slice(this.ring.length - wanted);
    let peak = 0;
    for (let i = 0; i < clip.length; i += 1) {
      const a = Math.abs(clip[i]);
      if (a > peak) {
        peak = a;
      }
    }
    return peak < MIN_PEAK ? null : clip;
  }

  get peak(): number {
    return this.lastPeak;
  }
}

/** Embed a clip. Rejects when the model is not loaded yet, rather than blocking a turn on a download. */
export async function embedClip(samples: Float32Array): Promise<number[]> {
  if (embedder === null) {
    void loadSpeakerModel();
    throw new Error(status.state === "loading" ? "the voice model is still loading" : "the voice model is not loaded");
  }
  return embedder(samples);
}

export interface Identified {
  readonly name: string | null;
  readonly confidence: number;
  readonly reason: string | null;
  readonly scores: ReadonlyArray<{ name: string; score: number }>;
}

/** Ask the service whose voice this is. */
export async function identifyVoice(embedding: number[]): Promise<Identified> {
  const response = await fetch(`${import.meta.env.BASE_URL}api/voice`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ action: "identify", embedding }),
  });
  const body = (await response.json()) as Identified & { error?: string };
  if (!response.ok) {
    throw new Error(body.error ?? `Identification failed (${response.status}).`);
  }
  return body;
}

/** Store one enrolment sample for a person. Returns how many they now have. */
export async function enrolVoice(name: string, embedding: number[], label: string): Promise<number> {
  const response = await fetch(`${import.meta.env.BASE_URL}api/voice`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ action: "enrol", name, embedding, label }),
  });
  const body = (await response.json()) as { samples?: number; error?: string };
  if (!response.ok || typeof body.samples !== "number") {
    throw new Error(body.error ?? `Enrolment failed (${response.status}).`);
  }
  return body.samples;
}

export async function clearVoice(name: string): Promise<void> {
  const response = await fetch(`${import.meta.env.BASE_URL}api/voice`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ action: "clear", name }),
  });
  if (!response.ok) {
    throw new Error(`Clearing the voice failed (${response.status}).`);
  }
}

/** The sentences a person reads when enrolling. Varied sounds, short enough to say in four seconds. */
export const ENROLMENT_LINES = [
  "Wilson, set a timer for ten minutes and tell me the weather.",
  "The quick brown fox jumps over the lazy dog every single morning.",
  "I would like a cup of coffee, two eggs, and the news from Toronto.",
];
