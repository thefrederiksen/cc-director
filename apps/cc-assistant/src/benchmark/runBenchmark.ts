// The self-running measurement.
//
// The live listening screen needs a person, a microphone and a quiet room, and it produces numbers
// somebody then has to read off and repeat. This does the same measurement with no microphone at
// all: four clips whose exact wording we wrote, played through every model and backend worth trying,
// producing one structured result that can be sent somewhere instead of squinted at.
//
// Because the wording is known, it measures ACCURACY as well as speed, which the live screen cannot.
// The clips are synthetic speech, so they are cleaner than a person talking over a boiling kettle.
// That makes the accuracy numbers OPTIMISTIC in absolute terms and still useful for RANKING models
// against each other, which is what the benchmark exists to do.

import { createTranscriber, type DecoderPrecision, type TranscriberDevice } from "../transcribe/transcriberClient";
import { wordErrorRate } from "./wordErrorRate";

export interface Clip {
  readonly id: string;
  readonly file: string;
  readonly text: string;
  readonly seconds: number;
}

export interface ClipOutcome {
  readonly clipId: string;
  readonly heard: string;
  readonly transcribeMs: number;
  readonly realTimeFactor: number;
  readonly errorRate: number;
}

export interface CombinationResult {
  readonly modelId: string;
  readonly device: TranscriberDevice;
  readonly decoderPrecision: DecoderPrecision;
  readonly status: "ok" | "failed" | "skipped";
  readonly message?: string;
  readonly loadMs?: number;
  readonly clips: ClipOutcome[];
  /** Mean across the clips. The two numbers the decision is actually made on. */
  readonly meanRealTimeFactor?: number;
  readonly meanErrorRate?: number;
}

export interface BenchmarkReport {
  readonly startedAt: string;
  readonly finishedAt: string;
  readonly userAgent: string;
  readonly hasWebGpu: boolean;
  readonly deviceMemoryGb: number | null;
  readonly processors: number | null;
  readonly totalAudioSeconds: number;
  readonly results: CombinationResult[];
}

export interface Combination {
  readonly modelId: string;
  readonly device: TranscriberDevice;
  readonly decoderPrecision: DecoderPrecision;
}

/** Load the clips and their known wording. */
export async function loadClips(baseUrl: string): Promise<Clip[]> {
  const response = await fetch(`${baseUrl}clips/clips.json`);
  if (!response.ok) {
    throw new Error(`The benchmark clips could not be listed: ${response.status} from ${baseUrl}clips/clips.json`);
  }
  return (await response.json()) as Clip[];
}

/**
 * Read one clip into the samples the model wants.
 *
 * The clips were generated as 16,000 hertz single-channel sixteen-bit audio, which is exactly the
 * model's format, so this reads the samples straight out of the file rather than going through the
 * browser's audio decoder. Decoding would run them up to the device's rate and back down again,
 * quietly changing the audio and making the accuracy numbers measure the resampler as much as the
 * model. The format is asserted rather than assumed; a clip in the wrong format fails loudly.
 */
export async function readClip(baseUrl: string, clip: Clip): Promise<Float32Array> {
  const response = await fetch(`${baseUrl}${clip.file}`);
  if (!response.ok) {
    throw new Error(`Clip ${clip.id} could not be fetched: ${response.status}`);
  }
  const bytes = new DataView(await response.arrayBuffer());

  if (bytes.byteLength < 44) {
    throw new Error(`Clip ${clip.id} is too short to be a wave file.`);
  }
  const channels = bytes.getUint16(22, true);
  const sampleRate = bytes.getUint32(24, true);
  const bitsPerSample = bytes.getUint16(34, true);
  if (channels !== 1 || sampleRate !== 16000 || bitsPerSample !== 16) {
    throw new Error(
      `Clip ${clip.id} is ${channels} channel, ${sampleRate} hertz, ${bitsPerSample} bit. It has to be 1 channel, 16000 hertz, 16 bit.`,
    );
  }

  // Walk the chunk list rather than assuming the samples start at byte 44. Some encoders insert
  // extra chunks before the data, and reading from a fixed offset then produces noise.
  let offset = 12;
  while (offset + 8 <= bytes.byteLength) {
    const id = String.fromCharCode(
      bytes.getUint8(offset), bytes.getUint8(offset + 1), bytes.getUint8(offset + 2), bytes.getUint8(offset + 3),
    );
    const size = bytes.getUint32(offset + 4, true);
    if (id === "data") {
      const count = Math.floor(size / 2);
      const samples = new Float32Array(count);
      for (let i = 0; i < count; i += 1) {
        samples[i] = bytes.getInt16(offset + 8 + i * 2, true) / 32768;
      }
      return samples;
    }
    offset += 8 + size + (size % 2);
  }
  throw new Error(`Clip ${clip.id} has no audio data chunk in it.`);
}

export interface BenchmarkProgress {
  onCombinationStart(combination: Combination, index: number, total: number): void;
  onCombinationDone(result: CombinationResult): void;
  onNote(message: string): void;
}

/**
 * Run every combination in turn and report.
 *
 * Sequential on purpose. Two models loaded at once on a phone compete for the same graphics memory
 * and each makes the other look slower than it is, which would make the whole exercise meaningless.
 * A combination that fails is recorded as failed and the run continues, because "whisper-small runs
 * out of memory on this phone" is a result worth having, not a reason to lose the other five.
 */
export async function runBenchmark(
  baseUrl: string,
  combinations: Combination[],
  progress: BenchmarkProgress,
): Promise<BenchmarkReport> {
  const startedAt = new Date().toISOString();
  const clips = await loadClips(baseUrl);
  const samples: Array<{ clip: Clip; audio: Float32Array }> = [];
  for (const clip of clips) {
    samples.push({ clip, audio: await readClip(baseUrl, clip) });
  }
  progress.onNote(`Loaded ${clips.length} clips, ${clips.reduce((s, c) => s + c.seconds, 0).toFixed(1)} seconds of speech.`);

  const hasWebGpu = (navigator as unknown as { gpu?: unknown }).gpu !== undefined;
  const results: CombinationResult[] = [];

  for (let index = 0; index < combinations.length; index += 1) {
    const combination = combinations[index];
    progress.onCombinationStart(combination, index + 1, combinations.length);

    if (combination.device === "webgpu" && !hasWebGpu) {
      const skipped: CombinationResult = {
        ...combination,
        status: "skipped",
        message: "This browser has no WebGPU.",
        clips: [],
      };
      results.push(skipped);
      progress.onCombinationDone(skipped);
      continue;
    }

    const result = await runOne(combination, samples, progress);
    results.push(result);
    progress.onCombinationDone(result);
  }

  const navigatorAny = navigator as unknown as { deviceMemory?: number; hardwareConcurrency?: number };
  return {
    startedAt,
    finishedAt: new Date().toISOString(),
    userAgent: navigator.userAgent,
    hasWebGpu,
    deviceMemoryGb: typeof navigatorAny.deviceMemory === "number" ? navigatorAny.deviceMemory : null,
    processors: typeof navigatorAny.hardwareConcurrency === "number" ? navigatorAny.hardwareConcurrency : null,
    totalAudioSeconds: Number(clips.reduce((s, c) => s + c.seconds, 0).toFixed(2)),
    results,
  };
}

async function runOne(
  combination: Combination,
  samples: Array<{ clip: Clip; audio: Float32Array }>,
  progress: BenchmarkProgress,
): Promise<CombinationResult> {
  // A fresh worker per combination, so a model that wedges its backend cannot poison the next one.
  let settleLoad: ((value: number) => void) | null = null;
  let failLoad: ((reason: Error) => void) | null = null;
  let settleClip: ((value: { text: string; transcribeMs: number; realTimeFactor: number }) => void) | null = null;
  let failClip: ((reason: Error) => void) | null = null;

  const transcriber = createTranscriber({
    onLoading(percent, file) {
      if (percent !== null && percent % 25 === 0) {
        progress.onNote(`${combination.modelId} on ${combination.device} at ${combination.decoderPrecision}: ${file} ${percent}%`);
      }
    },
    onLoaded(_modelId, _device, loadMs) {
      settleLoad?.(loadMs);
    },
    onResult(text, transcribeMs, realTimeFactor) {
      settleClip?.({ text, transcribeMs, realTimeFactor });
    },
    onFailure(message) {
      const error = new Error(message);
      // Whichever step is waiting gets the failure; a load failure and a clip failure are both fatal
      // for this combination and neither should hang.
      if (failLoad !== null) {
        failLoad(error);
      } else if (failClip !== null) {
        failClip(error);
      }
    },
  });

  try {
    const loadMs = await new Promise<number>((resolve, reject) => {
      settleLoad = resolve;
      failLoad = reject;
      transcriber.load(combination.modelId, combination.device, combination.decoderPrecision);
    });
    settleLoad = null;
    failLoad = null;

    const outcomes: ClipOutcome[] = [];
    for (const { clip, audio } of samples) {
      const outcome = await new Promise<{ text: string; transcribeMs: number; realTimeFactor: number }>(
        (resolve, reject) => {
          settleClip = resolve;
          failClip = reject;
          // A copy per run: the worker takes ownership of what it is sent, and the same clip is used
          // again by the next combination.
          transcriber.submit(audio.slice(0), clip.seconds);
        },
      );
      settleClip = null;
      failClip = null;
      outcomes.push({
        clipId: clip.id,
        heard: outcome.text,
        transcribeMs: outcome.transcribeMs,
        realTimeFactor: outcome.realTimeFactor,
        errorRate: wordErrorRate(clip.text, outcome.text).rate,
      });
    }

    const mean = (pick: (o: ClipOutcome) => number) =>
      Number((outcomes.reduce((sum, o) => sum + pick(o), 0) / outcomes.length).toFixed(4));

    return {
      ...combination,
      status: "ok",
      loadMs,
      clips: outcomes,
      meanRealTimeFactor: mean((o) => o.realTimeFactor),
      meanErrorRate: mean((o) => o.errorRate),
    };
  } catch (error) {
    return {
      ...combination,
      status: "failed",
      message: error instanceof Error ? error.message : String(error),
      clips: [],
    };
  } finally {
    transcriber.dispose();
  }
}
