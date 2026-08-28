// The speech model, running on its own thread.
//
// It has to be off the main thread. Transcription is hundreds of milliseconds of solid computation
// per chunk, and on the main thread that is hundreds of milliseconds where the page does not repaint
// and the microphone graph is competing for the same thread. On a device sitting in a kitchen
// charger showing a status light, a frozen page reads as a dead appliance.
//
// This file measures the one number the whole design hangs on: how long the model takes to
// transcribe a chunk, against how much real time that chunk represents. Under one and continuous
// listening is possible. Over one and it falls behind and never catches up, and no amount of tuning
// anywhere else recovers from that.

import { pipeline, type AutomaticSpeechRecognitionPipeline } from "@huggingface/transformers";

export type TranscriberDevice = "webgpu" | "wasm";

/** How hard the decoder weights are squeezed. The encoder always runs at full precision. */
export type DecoderPrecision = "q4" | "q8" | "fp32";

export interface LoadRequest {
  kind: "load";
  modelId: string;
  device: TranscriberDevice;
  decoderPrecision: DecoderPrecision;
}

export interface TranscribeRequest {
  kind: "transcribe";
  id: number;
  samples: Float32Array;
  seconds: number;
}

export type WorkerRequest = LoadRequest | TranscribeRequest;

export interface LoadingMessage {
  kind: "loading";
  /** 0 to 100 across all the model's files, or null before the total size is known. */
  percent: number | null;
  file: string;
}

export interface LoadedMessage {
  kind: "loaded";
  modelId: string;
  device: TranscriberDevice;
  loadMs: number;
}

export interface ResultMessage {
  kind: "result";
  id: number;
  text: string;
  /** Time the model itself spent, in milliseconds. */
  transcribeMs: number;
  /** transcribeMs divided by the real time the chunk covers. Under 1 means it keeps up. */
  realTimeFactor: number;
}

export interface FailureMessage {
  kind: "failure";
  /** Set when the failure belongs to one chunk rather than to the model as a whole. */
  id: number | null;
  message: string;
}

export type WorkerMessage = LoadingMessage | LoadedMessage | ResultMessage | FailureMessage;

let transcriber: AutomaticSpeechRecognitionPipeline | null = null;

// The encoder always runs at full precision; the decoder is the part worth squeezing, and the part
// that goes wrong when it is squeezed too far.
//
// Four-bit was the starting choice because it is what the WebGPU Whisper demos use. On 28 August 2026
// the same model, on the same audio, produced a good transcript on a desktop and a transcript
// truncated after the first word on an Android phone - the difference being how each graphics driver
// handles four-bit weights. So this is now a parameter that a benchmark can compare, not a constant
// somebody once picked. WebAssembly is unaffected: it runs eight-bit throughout, which is what fits in
// the heap a phone browser hands a WebAssembly module.
function dtypeFor(device: TranscriberDevice, decoderPrecision: DecoderPrecision) {
  return device === "webgpu"
    ? { encoder_model: "fp32" as const, decoder_model_merged: decoderPrecision }
    : ("q8" as const);
}

function post(message: WorkerMessage): void {
  self.postMessage(message);
}

function describe(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

async function load(request: LoadRequest): Promise<void> {
  // Asked for, then checked. A browser without WebGPU that is asked for WebGPU should say so plainly
  // rather than quietly run twenty times slower and leave someone wondering why the numbers are bad.
  if (request.device === "webgpu" && (navigator as unknown as { gpu?: unknown }).gpu === undefined) {
    post({
      kind: "failure",
      id: null,
      message: "This browser has no WebGPU, so it cannot run the model that way. Choose WebAssembly, which works everywhere and is slower.",
    });
    return;
  }

  transcriber = null;
  const startedAt = performance.now();

  try {
    transcriber = (await pipeline("automatic-speech-recognition", request.modelId, {
      device: request.device,
      dtype: dtypeFor(request.device, request.decoderPrecision),
      progress_callback: (progress: unknown) => {
        const p = progress as { status?: string; file?: string; progress?: number };
        if (p.status === "progress" || p.status === "download" || p.status === "initiate") {
          post({
            kind: "loading",
            percent: typeof p.progress === "number" ? Math.round(p.progress) : null,
            file: p.file ?? request.modelId,
          });
        }
      },
    })) as AutomaticSpeechRecognitionPipeline;
  } catch (error) {
    post({
      kind: "failure",
      id: null,
      message: `The model "${request.modelId}" would not load on ${request.device} at ${request.decoderPrecision}: ${describe(error)}`,
    });
    return;
  }

  post({
    kind: "loaded",
    modelId: request.modelId,
    device: request.device,
    loadMs: Math.round(performance.now() - startedAt),
  });
}

async function transcribe(request: TranscribeRequest): Promise<void> {
  if (transcriber === null) {
    post({ kind: "failure", id: request.id, message: "A chunk arrived before the model had loaded." });
    return;
  }

  const startedAt = performance.now();
  try {
    const output = await transcriber(request.samples);
    const transcribeMs = Math.round(performance.now() - startedAt);
    const text = Array.isArray(output)
      ? output.map((each) => String((each as { text?: string }).text ?? "")).join(" ")
      : String((output as { text?: string }).text ?? "");
    post({
      kind: "result",
      id: request.id,
      text: text.trim(),
      transcribeMs,
      realTimeFactor: Number((transcribeMs / (request.seconds * 1000)).toFixed(3)),
    });
  } catch (error) {
    post({ kind: "failure", id: request.id, message: `Transcription failed: ${describe(error)}` });
  }
}

// Chunks arrive faster than they can be transcribed when the model is too big for the device, which
// is exactly the case being measured. They are handled strictly in order, one at a time, so the
// timings mean what they say rather than measuring several overlapping runs fighting for the device.
let queue: Promise<void> = Promise.resolve();

self.onmessage = (event: MessageEvent<WorkerRequest>) => {
  const request = event.data;
  queue = queue.then(() => (request.kind === "load" ? load(request) : transcribe(request)));
};
