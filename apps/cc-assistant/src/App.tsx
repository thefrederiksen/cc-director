import { useCallback, useEffect, useRef, useState } from "react";
import { startPcmCapture, type PcmCapture } from "./audio/pcmCapture";
import { createTranscriber, type DecoderPrecision, type Transcriber, type TranscriberDevice } from "./transcribe/transcriberClient";
import { BenchmarkPanel } from "./benchmark/BenchmarkPanel";
import { CalibrationPanel } from "./calibration/CalibrationPanel";
import { PlatformSpeechPanel } from "./platformSpeech/PlatformSpeechPanel";

// STEP ONE. One question, one screen: can this phone transcribe speech continuously, and how well?
//
// Everything else in CC Assistant sits on top of the answer. The wake word is a text match on this
// transcript. The command is this transcript. If the model cannot keep up with the room, none of it
// is possible at this model size, and that is worth knowing in an evening rather than after the rest
// is built on the assumption.
//
// Deliberately plain. This is an instrument, not a product screen.

const MODELS = [
  "onnx-community/whisper-tiny.en",
  "onnx-community/whisper-base.en",
  "onnx-community/whisper-small.en",
];

const WORKLET_URL = `${import.meta.env.BASE_URL}pcm-worklet.js`;

interface Line {
  readonly id: number;
  readonly at: string;
  readonly text: string;
  readonly transcribeMs: number;
  readonly realTimeFactor: number;
  readonly backlog: number;
  readonly peak: number;
}

export function App() {
  const [modelId, setModelId] = useState(MODELS[1]);
  const [device, setDevice] = useState<TranscriberDevice>("webgpu");
  const [chunkSeconds, setChunkSeconds] = useState(2);
  const [decoderPrecision, setDecoderPrecision] = useState<DecoderPrecision>("q8");

  const [status, setStatus] = useState("Idle. Load a model to begin.");
  const [loadMs, setLoadMs] = useState<number | null>(null);
  const [ready, setReady] = useState(false);
  const [listening, setListening] = useState(false);
  const [deviceSampleRate, setDeviceSampleRate] = useState<number | null>(null);
  const [lines, setLines] = useState<Line[]>([]);
  const [problems, setProblems] = useState<string[]>([]);

  const transcriberRef = useRef<Transcriber | null>(null);
  const captureRef = useRef<PcmCapture | null>(null);
  const lineIdRef = useRef(0);
  const peakRef = useRef(0);

  const hasWebGpu = typeof navigator !== "undefined" && (navigator as unknown as { gpu?: unknown }).gpu !== undefined;

  const note = useCallback((message: string) => {
    setProblems((previous) => [`${new Date().toLocaleTimeString()}  ${message}`, ...previous].slice(0, 30));
  }, []);

  const ensureTranscriber = useCallback((): Transcriber => {
    if (transcriberRef.current !== null) {
      return transcriberRef.current;
    }
    const made = createTranscriber({
      onLoading(percent, file) {
        setStatus(percent === null ? `Downloading ${file}` : `Downloading ${file}  ${percent}%`);
      },
      onLoaded(loadedModel, loadedDevice, ms) {
        setLoadMs(ms);
        setReady(true);
        setStatus(`Ready. ${loadedModel} on ${loadedDevice}.`);
      },
      onResult(text, transcribeMs, realTimeFactor, backlog) {
        lineIdRef.current += 1;
        const line: Line = {
          id: lineIdRef.current,
          at: new Date().toLocaleTimeString(),
          text,
          transcribeMs,
          realTimeFactor,
          backlog,
          peak: peakRef.current,
        };
        setLines((previous) => [line, ...previous].slice(0, 60));
      },
      onFailure(message) {
        note(message);
        setStatus("Stopped on a failure. See the log.");
      },
    });
    transcriberRef.current = made;
    return made;
  }, [note]);

  const loadModel = useCallback(() => {
    setReady(false);
    setLoadMs(null);
    setLines([]);
    setStatus(`Loading ${modelId} on ${device}. The first load downloads the model.`);
    ensureTranscriber().load(modelId, device, decoderPrecision);
  }, [decoderPrecision, device, ensureTranscriber, modelId]);

  const stopListening = useCallback(async () => {
    const capture = captureRef.current;
    captureRef.current = null;
    setListening(false);
    if (capture !== null) {
      await capture.stop();
    }
  }, []);

  const startListening = useCallback(async () => {
    const transcriber = transcriberRef.current;
    if (transcriber === null || !ready) {
      note("Load a model before listening.");
      return;
    }
    try {
      const capture = await startPcmCapture(chunkSeconds, WORKLET_URL, (chunk) => {
        peakRef.current = chunk.peak;
        transcriber.submit(chunk.samples, chunk.seconds);
      });
      captureRef.current = capture;
      setDeviceSampleRate(capture.deviceSampleRate);
      setListening(true);
      note(
        capture.microphone.echoCancellationEnabled
          ? `Microphone open on ${capture.microphone.deviceLabel}, echo cancellation on, device rate ${capture.deviceSampleRate} hertz.`
          : `Microphone open on ${capture.microphone.deviceLabel} but echo cancellation is OFF. Talking over your own audio will not work.`,
      );
    } catch (error) {
      note(error instanceof Error ? error.message : String(error));
    }
  }, [chunkSeconds, note, ready]);

  useEffect(() => {
    return () => {
      void captureRef.current?.stop();
      transcriberRef.current?.dispose();
    };
  }, []);

  const measured = lines.slice(0, 10);
  const averageFactor =
    measured.length === 0
      ? null
      : measured.reduce((sum, line) => sum + line.realTimeFactor, 0) / measured.length;
  const averageMs =
    measured.length === 0
      ? null
      : Math.round(measured.reduce((sum, line) => sum + line.transcribeMs, 0) / measured.length);
  const backlog = lines.length === 0 ? 0 : lines[0].backlog;

  let verdict = "No measurement yet.";
  let verdictClass = "verdict";
  if (averageFactor !== null) {
    if (averageFactor < 0.5 && backlog <= 1) {
      verdict = "KEEPS UP COMFORTABLY. This model can listen continuously on this device.";
      verdictClass = "verdict good";
    } else if (averageFactor < 1 && backlog <= 3) {
      verdict = "KEEPS UP, BUT NOT BY MUCH. Usable; try a smaller model or shorter chunks for headroom.";
      verdictClass = "verdict warn";
    } else {
      verdict = "FALLS BEHIND. The backlog grows and it never catches up. Use a smaller model here.";
      verdictClass = "verdict bad";
    }
  }

  return (
    <main>
      <h1>Wilson &mdash; diagnostics</h1>
      <p className="sub">
        Measures whether this device can transcribe speech continuously, and picks the configuration
        that works here. Everything else depends on the answer, so nothing else is built yet.
      </p>

      <PlatformSpeechPanel />

      <h2 className="divider">If it cannot, which model should this device run</h2>

      <CalibrationPanel />

      <h2 className="divider">Or compare everything, by hand</h2>

      <BenchmarkPanel />

      <h2 className="divider">Or measure it live, with your own voice</h2>

      <section>
        <h2>1. Model</h2>
        <div className="row">
          <label>
            Model
            <select value={modelId} onChange={(e) => setModelId(e.target.value)} disabled={listening}>
              {MODELS.map((m) => (
                <option key={m} value={m}>{m}</option>
              ))}
            </select>
          </label>
          <label>
            Runs on
            <select
              value={device}
              onChange={(e) => setDevice(e.target.value as TranscriberDevice)}
              disabled={listening}
            >
              <option value="webgpu">WebGPU {hasWebGpu ? "(available)" : "(NOT available here)"}</option>
              <option value="wasm">WebAssembly (works everywhere, slower)</option>
            </select>
          </label>
          <label>
            Decoder
            <select
              value={decoderPrecision}
              onChange={(e) => setDecoderPrecision(e.target.value as DecoderPrecision)}
              disabled={listening}
            >
              <option value="q8">Eight bit</option>
              <option value="q4">Four bit (smaller, less exact)</option>
              <option value="fp32">Full precision (largest)</option>
            </select>
          </label>
          <label>
            Chunk
            <select
              value={chunkSeconds}
              onChange={(e) => setChunkSeconds(Number(e.target.value))}
              disabled={listening}
            >
              <option value={1}>1 second</option>
              <option value={2}>2 seconds</option>
              <option value={3}>3 seconds</option>
              <option value={5}>5 seconds</option>
            </select>
          </label>
          <button onClick={loadModel} disabled={listening}>Load model</button>
        </div>
        <p className="status">{status}</p>
        {loadMs !== null ? <p className="status">Loaded in {loadMs} ms.</p> : null}
      </section>

      <section>
        <h2>2. Listen</h2>
        <div className="row">
          {listening ? (
            <button className="stop" onClick={() => void stopListening()}>Stop listening</button>
          ) : (
            <button className="go" onClick={() => void startListening()} disabled={!ready}>
              Start listening
            </button>
          )}
          <span className="status">
            {deviceSampleRate === null ? "" : `Device runs at ${deviceSampleRate} hertz, converted to 16000.`}
          </span>
        </div>
      </section>

      <section>
        <h2>3. The number that decides everything</h2>
        <div className="numbers">
          <div className="num">
            <span className="k">Real-time factor</span>
            <span className="v">{averageFactor === null ? "-" : averageFactor.toFixed(2)}</span>
            <span className="h">time to transcribe / time of audio. Under 1 keeps up.</span>
          </div>
          <div className="num">
            <span className="k">Per chunk</span>
            <span className="v">{averageMs === null ? "-" : `${averageMs} ms`}</span>
            <span className="h">average of the last 10</span>
          </div>
          <div className="num">
            <span className="k">Backlog</span>
            <span className="v">{backlog}</span>
            <span className="h">chunks waiting. Growing means falling behind.</span>
          </div>
        </div>
        <p className={verdictClass}>{verdict}</p>
      </section>

      <section>
        <h2>4. What it heard</h2>
        {lines.length === 0 ? (
          <p className="status">Nothing yet.</p>
        ) : (
          <table>
            <thead>
              <tr>
                <th>Time</th><th>ms</th><th>Factor</th><th>Queue</th><th>Peak</th><th>Text</th>
              </tr>
            </thead>
            <tbody>
              {lines.map((line) => (
                <tr key={line.id} className={line.realTimeFactor >= 1 ? "slow" : undefined}>
                  <td>{line.at}</td>
                  <td>{line.transcribeMs}</td>
                  <td>{line.realTimeFactor.toFixed(2)}</td>
                  <td>{line.backlog}</td>
                  <td>{line.peak.toFixed(2)}</td>
                  <td className="text">{line.text.length > 0 ? line.text : <em>silence</em>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>

      <section>
        <h2>5. Log</h2>
        {problems.length === 0 ? (
          <p className="status">Nothing yet.</p>
        ) : (
          <ul className="log">
            {problems.map((p) => <li key={p}>{p}</li>)}
          </ul>
        )}
      </section>
    </main>
  );
}
