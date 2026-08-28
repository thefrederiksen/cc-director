// Picking a configuration by measuring the device, once, and remembering the answer.
//
// The alternative is guessing from the user agent or the reported memory, and the 28 August runs
// showed exactly why that fails. An eight-gigabyte Android phone matched a twenty-four core desktop
// with a discrete graphics card, which no specification sheet would have predicted. And the failure
// that actually mattered was not speed at all: the same model on the same audio produced a good
// transcript on one machine and a truncated one on the other, because of how each graphics driver
// handles four-bit weights. No heuristic finds that. Running it does.
//
// So: walk a ladder of configurations from best to worst, stop at the first one that passes every
// gate, and store it. If none pass, say so. Do NOT quietly run the least bad one, because a device
// that cannot do this should say it cannot do this.

import { runBenchmark, type BenchmarkReport, type Combination, type CombinationResult } from "../benchmark/runBenchmark";
import { normaliseForMatching } from "../wakeWord/wakeWordMatcher";
import { judge, type GateVerdict } from "./gates";

/**
 * The ladder, best first.
 *
 * Ordered by what we lose by descending it. Accuracy first, so base outranks tiny. Then decoder
 * precision, so eight-bit outranks four-bit, because four-bit is what truncated. Then the backend,
 * because WebAssembly is much slower but works on devices with no WebGPU at all, which is the
 * situation on older phones.
 */
export const LADDER: Combination[] = [
  { modelId: "onnx-community/whisper-base.en", device: "webgpu", decoderPrecision: "q8" },
  { modelId: "onnx-community/whisper-base.en", device: "webgpu", decoderPrecision: "q4" },
  { modelId: "onnx-community/whisper-tiny.en", device: "webgpu", decoderPrecision: "q8" },
  { modelId: "onnx-community/whisper-base.en", device: "wasm", decoderPrecision: "q8" },
  { modelId: "onnx-community/whisper-tiny.en", device: "wasm", decoderPrecision: "q8" },
];

export interface Rung {
  readonly combination: Combination;
  readonly result: CombinationResult;
  readonly verdict: GateVerdict;
}

export interface Calibration {
  readonly version: 1;
  readonly decidedAt: string;
  readonly userAgent: string;
  /** The winner, or null when this device could not run any configuration well enough. */
  readonly chosen: Combination | null;
  /** Every rung tried, in order, with why each one failed. Shown, not hidden. */
  readonly tried: Rung[];
}

const STORAGE_KEY = "cc-assistant.calibration.v1";

export interface CalibrationProgress {
  onRungStart(combination: Combination, index: number, total: number): void;
  onRungDone(rung: Rung): void;
  onNote(message: string): void;
}

/**
 * Measure this device and choose.
 *
 * Stops at the first configuration that passes, so a device that runs the best one pays for exactly
 * one rung. A device that cannot run any of them pays for all of them and is then told plainly.
 */
export async function calibrate(
  baseUrl: string,
  progress: CalibrationProgress,
  ladder: Combination[] = LADDER,
): Promise<Calibration> {
  const tried: Rung[] = [];
  let chosen: Combination | null = null;

  for (let index = 0; index < ladder.length; index += 1) {
    const combination = ladder[index];
    progress.onRungStart(combination, index + 1, ladder.length);

    let report: BenchmarkReport;
    try {
      report = await runBenchmark(baseUrl, [combination], {
        onCombinationStart: () => undefined,
        onCombinationDone: () => undefined,
        onNote: progress.onNote,
      });
    } catch (error) {
      progress.onNote(error instanceof Error ? error.message : String(error));
      break;
    }

    const result = report.results[0];
    if (result === undefined) {
      progress.onNote("A configuration produced no result at all, which should not happen.");
      continue;
    }

    const verdict = judge(result, await expectedWordCounts(baseUrl));
    const rung: Rung = { combination, result, verdict };
    tried.push(rung);
    progress.onRungDone(rung);

    if (verdict.passed) {
      chosen = combination;
      break;
    }
  }

  const calibration: Calibration = {
    version: 1,
    decidedAt: new Date().toISOString(),
    userAgent: navigator.userAgent,
    chosen,
    tried,
  };
  save(calibration);
  return calibration;
}

/** How many words each clip should produce, so a truncated transcript can be recognised. */
async function expectedWordCounts(baseUrl: string): Promise<Map<string, number>> {
  const response = await fetch(`${baseUrl}clips/clips.json`);
  if (!response.ok) {
    throw new Error(`The clip wording could not be read: ${response.status}`);
  }
  const clips = (await response.json()) as Array<{ id: string; text: string }>;
  const counts = new Map<string, number>();
  for (const clip of clips) {
    const normalised = normaliseForMatching(clip.text);
    counts.set(clip.id, normalised.length === 0 ? 0 : normalised.split(" ").length);
  }
  return counts;
}

/** The stored decision, or null when this device has not been measured yet. */
export function loadStored(): Calibration | null {
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    if (raw === null) {
      return null;
    }
    const parsed = JSON.parse(raw) as Calibration;
    // A decision made by an older version of this code describes a world that no longer exists.
    return parsed.version === 1 ? parsed : null;
  } catch {
    // Private browsing and blocked site data both throw here. Not being able to REMEMBER the answer
    // is a nuisance; it is not a reason to refuse to work them out again.
    return null;
  }
}

export function forget(): void {
  try {
    window.localStorage.removeItem(STORAGE_KEY);
  } catch {
    // Nothing to do: if it cannot be written it was never stored.
  }
}

function save(calibration: Calibration): void {
  try {
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(calibration));
  } catch {
    // Same as above. The decision still applies for this session; it just will not survive a reload.
  }
}

/** A one-line description of a configuration, for showing a person. */
export function describeCombination(combination: Combination): string {
  const model = combination.modelId.replace("onnx-community/whisper-", "").replace(".en", "");
  const backend = combination.device === "webgpu" ? "the graphics processor" : "the processor";
  const precision = combination.decoderPrecision === "q4" ? "four-bit" : combination.decoderPrecision === "q8" ? "eight-bit" : "full precision";
  return `${model} on ${backend}, ${precision}`;
}
