// Turning a live microphone into fixed-length blocks of audio the speech model can read.
//
// The model wants exactly one thing: mono samples between minus one and one, at 16,000 samples per
// second. The microphone gives us whatever the device runs at, which is usually 48,000. So this
// module does three jobs and nothing else: open the microphone with echo cancellation, pull the raw
// samples off the audio thread through the worklet, and hand out complete chunks at the rate the
// model wants.
//
// The sample rate conversion is done here, explicitly, rather than by asking the browser for a
// 16,000 hertz audio context. Asking is unreliable: several browsers accept the request and then
// quietly run at their own rate, which would feed the model audio at three times the speed it
// expects and produce transcripts that look like the model is broken rather than the plumbing.

import { openMicrophone, type MicrophoneCapture } from "./microphoneCapture";

/** The sample rate every Whisper model expects. Not configurable; it is baked into the model. */
export const MODEL_SAMPLE_RATE = 16000;

export interface PcmChunk {
  /** Mono samples at 16,000 hertz, ready to hand to the model. */
  readonly samples: Float32Array;
  /** How many seconds of real time this chunk represents. */
  readonly seconds: number;
  /** Loudest absolute sample in the chunk, 0 to 1. Enough to tell speech from a silent room. */
  readonly peak: number;
}

export interface PcmCapture {
  readonly microphone: MicrophoneCapture;
  /** The rate the device actually ran at, before conversion. Worth showing; it explains a lot. */
  readonly deviceSampleRate: number;
  stop(): Promise<void>;
}

export class PcmCaptureError extends Error {
  constructor(message: string, readonly cause?: unknown) {
    super(message);
    this.name = "PcmCaptureError";
  }
}

/**
 * Start capturing, calling `onChunk` every `chunkSeconds` of audio.
 *
 * `workletUrl` is passed in rather than computed here so the caller owns the deployed path. Getting
 * it wrong is the most common way this fails, and it fails at the point of loading, loudly.
 */
export async function startPcmCapture(
  chunkSeconds: number,
  workletUrl: string,
  onChunk: (chunk: PcmChunk) => void,
): Promise<PcmCapture> {
  if (chunkSeconds <= 0) {
    throw new PcmCaptureError("A chunk has to be longer than zero seconds.");
  }

  const microphone = await openMicrophone();

  let context: AudioContext;
  try {
    context = new AudioContext();
  } catch (error) {
    microphone.stop();
    throw new PcmCaptureError("The browser would not open an audio context.", error);
  }

  try {
    await context.audioWorklet.addModule(workletUrl);
  } catch (error) {
    microphone.stop();
    await context.close();
    throw new PcmCaptureError(
      `The audio worklet at ${workletUrl} would not load. This is the path being wrong far more often than it is anything else.`,
      error,
    );
  }

  // Some browsers open an audio context suspended until a user gesture has been seen. Starting from
  // a button press means the gesture has happened, but resuming is still required.
  if (context.state === "suspended") {
    await context.resume();
  }

  const deviceSampleRate = context.sampleRate;
  const samplesPerChunk = Math.round(chunkSeconds * MODEL_SAMPLE_RATE);

  // Everything below runs at the DEVICE rate; conversion happens when a chunk is complete.
  const deviceSamplesPerChunk = Math.round(chunkSeconds * deviceSampleRate);
  let pending = new Float32Array(deviceSamplesPerChunk);
  let filled = 0;

  const source = context.createMediaStreamSource(microphone.stream);
  const collector = new AudioWorkletNode(context, "pcm-collector");

  collector.port.onmessage = (event: MessageEvent<Float32Array>) => {
    const incoming = event.data;
    for (let i = 0; i < incoming.length; i += 1) {
      pending[filled] = incoming[i];
      filled += 1;
      if (filled === deviceSamplesPerChunk) {
        const samples = resample(pending, deviceSampleRate, MODEL_SAMPLE_RATE, samplesPerChunk);
        onChunk({ samples, seconds: chunkSeconds, peak: peakOf(samples) });
        pending = new Float32Array(deviceSamplesPerChunk);
        filled = 0;
      }
    }
  };

  source.connect(collector);
  // The worklet produces no output, but an unconnected node is not guaranteed to be pulled, so it is
  // connected to the destination. It writes nothing, so nothing is heard.
  collector.connect(context.destination);

  return {
    microphone,
    deviceSampleRate,
    async stop() {
      collector.port.onmessage = null;
      source.disconnect();
      collector.disconnect();
      microphone.stop();
      await context.close();
    },
  };
}

/**
 * Linear interpolation between sample rates.
 *
 * Good enough on purpose. Speech recognition front-ends throw away far more detail than this loses
 * when they turn the waveform into a spectrogram, so a higher-order filter here would cost real time
 * on every chunk and change no transcript.
 */
function resample(
  input: Float32Array,
  fromRate: number,
  toRate: number,
  outputLength: number,
): Float32Array {
  if (fromRate === toRate) {
    return input.slice(0, outputLength);
  }
  const output = new Float32Array(outputLength);
  const ratio = fromRate / toRate;
  const lastIndex = input.length - 1;
  for (let i = 0; i < outputLength; i += 1) {
    const position = i * ratio;
    const low = Math.floor(position);
    const high = Math.min(low + 1, lastIndex);
    const fraction = position - low;
    output[i] = input[low] * (1 - fraction) + input[high] * fraction;
  }
  return output;
}

function peakOf(samples: Float32Array): number {
  let peak = 0;
  for (let i = 0; i < samples.length; i += 1) {
    const value = Math.abs(samples[i]);
    if (value > peak) {
      peak = value;
    }
  }
  return peak;
}
