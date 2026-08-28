import { useCallback, useState } from "react";
import { holdScreenAwake, type ScreenWakeLock } from "../screenWakeLock";
import {
  runBenchmark,
  type BenchmarkReport,
  type Combination,
  type CombinationResult,
} from "./runBenchmark";

// The whole measurement in one button. No microphone, no quiet room, no reading numbers off a screen
// and repeating them to somebody. It runs the same four clips through every combination and sends
// the result away by itself.

const MODELS = [
  "onnx-community/whisper-tiny.en",
  "onnx-community/whisper-base.en",
  "onnx-community/whisper-small.en",
];

// Four-bit and eight-bit decoders side by side, because the 28 August phone run produced a
// transcript truncated after the first word at four-bit while the same model on a desktop did not.
// Running both in one go answers that with a comparison rather than a swap.
const ALL: Combination[] = [
  ...MODELS.flatMap((modelId) =>
    (["q4", "q8"] as const).map((decoderPrecision) => ({ modelId, device: "webgpu" as const, decoderPrecision })),
  ),
  ...MODELS.map((modelId) => ({ modelId, device: "wasm" as const, decoderPrecision: "q8" as const })),
];

function keyOf(c: Combination): string {
  return `${c.modelId}|${c.device}|${c.decoderPrecision}`;
}

function shortName(modelId: string): string {
  return modelId.replace("onnx-community/whisper-", "").replace(".en", "");
}

export function BenchmarkPanel() {
  // Tiny and base on WebGPU by default, and nothing else. Small is a 250 MB download that can exhaust
  // a phone browser's memory, and the WebAssembly runs take several minutes, so both are a deliberate
  // tick rather than something a first run on a phone walks into.
  const [chosen, setChosen] = useState<Set<string>>(
    () =>
      new Set(
        ALL.filter((c) => c.device === "webgpu" && c.modelId.includes("base")).map(keyOf),
      ),
  );
  const [running, setRunning] = useState(false);
  const [progress, setProgress] = useState("");
  const [notes, setNotes] = useState<string[]>([]);
  const [results, setResults] = useState<CombinationResult[]>([]);
  const [report, setReport] = useState<BenchmarkReport | null>(null);
  const [sent, setSent] = useState<string | null>(null);

  const toggle = useCallback((c: Combination) => {
    setChosen((previous) => {
      const next = new Set(previous);
      const key = keyOf(c);
      if (next.has(key)) {
        next.delete(key);
      } else {
        next.add(key);
      }
      return next;
    });
  }, []);

  const note = useCallback((message: string) => {
    setNotes((previous) => [message, ...previous].slice(0, 40));
  }, []);

  const run = useCallback(async () => {
    const combinations = ALL.filter((c) => chosen.has(keyOf(c)));
    if (combinations.length === 0) {
      note("Choose at least one combination.");
      return;
    }

    setRunning(true);
    setResults([]);
    setReport(null);
    setSent(null);
    setNotes([]);

    // Hold the screen on for the duration. A benchmark takes minutes, and a phone left alone will
    // sleep well before it finishes; a sleeping phone suspends the page and the run simply stops,
    // half done, with no error to explain it. Best effort: a browser that will not hold the lock
    // says so and the run continues, because a note is better than a refusal.
    let wakeLock: ScreenWakeLock | null = null;
    try {
      wakeLock = await holdScreenAwake(note);
      note("Holding the screen awake until the run finishes.");
    } catch (error) {
      note(
        `${error instanceof Error ? error.message : String(error)} Keep the screen on yourself, or the run will stop part way.`,
      );
    }

    try {
      const finished = await runBenchmark(import.meta.env.BASE_URL, combinations, {
        onCombinationStart(combination, index, total) {
          setProgress(`${index} of ${total}: ${shortName(combination.modelId)} on ${combination.device}, ${combination.decoderPrecision}`);
        },
        onCombinationDone(result) {
          setResults((previous) => [...previous, result]);
        },
        onNote: note,
      });
      setReport(finished);
      setProgress("Done.");

      // Sent by itself. A benchmark whose result has to be transcribed by hand is half a benchmark.
      try {
        const response = await fetch(`${import.meta.env.BASE_URL}api/result`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(finished),
        });
        if (response.ok) {
          const body = (await response.json()) as { receivedAt?: string };
          setSent(body.receivedAt ?? "sent");
        } else {
          note(`The report could not be sent: HTTP ${response.status}. Use Copy report instead.`);
        }
      } catch (error) {
        note(
          `The report could not be sent: ${error instanceof Error ? error.message : String(error)}. Use Copy report instead.`,
        );
      }
    } catch (error) {
      note(error instanceof Error ? error.message : String(error));
      setProgress("Stopped on a failure.");
    } finally {
      await wakeLock?.release();
      setRunning(false);
    }
  }, [chosen, note]);

  const copyReport = useCallback(async () => {
    if (report === null) {
      return;
    }
    try {
      await navigator.clipboard.writeText(JSON.stringify(report, null, 2));
      note("Report copied to the clipboard.");
    } catch (error) {
      note(`Could not copy: ${error instanceof Error ? error.message : String(error)}`);
    }
  }, [note, report]);

  return (
    <section>
      <h2>Benchmark &mdash; no microphone needed</h2>
      <p className="status">
        Four clips of known wording, run through each combination. Measures speed and accuracy, and
        sends the result by itself. This is the one to run on your phone.
      </p>

      <div className="grid">
        {ALL.map((c) => (
          <label className="check" key={keyOf(c)}>
            <input
              type="checkbox"
              checked={chosen.has(keyOf(c))}
              onChange={() => toggle(c)}
              disabled={running}
            />
            {shortName(c.modelId)} on {c.device}, {c.decoderPrecision}
          </label>
        ))}
      </div>

      <div className="row" style={{ marginTop: 12 }}>
        <button className="go" onClick={() => void run()} disabled={running}>
          {running ? "Running..." : "Run benchmark"}
        </button>
        {report !== null ? <button onClick={() => void copyReport()}>Copy report</button> : null}
        <span className="status">{progress}</span>
      </div>

      {sent !== null ? <p className="status sent">Report sent at {sent}. Nothing else to do.</p> : null}

      {results.length > 0 ? (
        <table>
          <thead>
            <tr>
              <th>Model</th><th>Runs on</th><th>Decoder</th><th>Load</th><th>Factor</th><th>Errors</th><th>Verdict</th>
            </tr>
          </thead>
          <tbody>
            {results.map((r) => (
              <tr key={keyOf(r)} className={r.status !== "ok" ? "slow" : undefined}>
                <td>{shortName(r.modelId)}</td>
                <td>{r.device}</td>
                <td>{r.decoderPrecision}</td>
                <td>{r.loadMs === undefined ? "-" : `${(r.loadMs / 1000).toFixed(1)} s`}</td>
                <td>{r.meanRealTimeFactor === undefined ? "-" : r.meanRealTimeFactor.toFixed(2)}</td>
                <td>{r.meanErrorRate === undefined ? "-" : `${Math.round(r.meanErrorRate * 100)}%`}</td>
                <td className="text">{verdictFor(r)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : null}

      {notes.length > 0 ? (
        <ul className="log quiet">
          {notes.map((n, i) => <li key={`${i}-${n}`}>{n}</li>)}
        </ul>
      ) : null}
    </section>
  );
}

function verdictFor(result: CombinationResult): string {
  if (result.status === "skipped") {
    return `Skipped. ${result.message ?? ""}`;
  }
  if (result.status === "failed") {
    return `Failed. ${result.message ?? ""}`;
  }
  const factor = result.meanRealTimeFactor ?? 0;
  const errors = result.meanErrorRate ?? 0;
  const speed =
    factor < 0.5 ? "fast enough to listen continuously" : factor < 1 ? "keeps up, but no headroom" : "too slow to listen continuously";
  const quality = errors <= 0.05 ? "accurate" : errors <= 0.15 ? "usable" : "too many mistakes";
  return `${speed}, ${quality}`;
}
