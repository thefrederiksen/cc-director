// Car Mode End Word Test - the interactive harness for the spoken end-of-turn phrase (the ending
// "over and out"). This page is BOTH the test rig and the seed of the eventual Cockpit "Car Mode"
// settings tab: the owner sets his own end phrase, taps Start, speaks, and sees whether and how fast
// it is detected. It proves the detection approach and the felt delay on the real phone before any of
// it is wired into Car Mode's actual turn-ending.
//
// The approach under test (recommended by the Architect, proven 9/9 on the real transcription path):
// during Listening, roughly once a second, transcribe what has been captured so far through the one
// Gateway speech-to-text front door and fire when the transcript ENDS with the owner's phrase. No
// finicky silence/endpoint detection - it leans on the reliable transcription. There is no self-trigger
// risk here because the assistant is silent while the owner is talking.

import { useCallback, useEffect, useRef, useState } from "react";
import { MicRecorder } from "@devthrottle/client-core/dictation/recorder";
import { blobToWav16kMono } from "@devthrottle/client-core/dictation/wav";
import { transcribeCarModeAudio } from "@devthrottle/client-core/carmode/carModeApi";
import { playReadyCue } from "@devthrottle/client-core/dictation/readyCue";
import { detectPhraseAtEnd } from "@devthrottle/client-core/carmode/controlPhrases";

const PHRASE_KEY = "carmode.endphrase";
const DEFAULT_PHRASE = "over and out";
const POLL_MS = 1000; // how often the captured audio is re-transcribed while listening
const LEVEL_MS = 100; // how often the input level is sampled to track when the owner stops speaking
const LEVEL_SPEAKING = 0.06; // input level above this counts as "still speaking"
const MIN_CLIP_BYTES = 2000; // skip a transcribe until there is at least this much audio

interface LogEntry {
  atMs: number; // ms since Start
  transcript: string;
  matched: boolean;
  transcribeMs: number;
}

interface DetectResult {
  latencyMs: number; // from when the owner stopped speaking to the fire (the felt delay)
  command: string;
  transcript: string;
}

export function EndWordTest() {
  const [phrase, setPhrase] = useState<string>(() => localStorage.getItem(PHRASE_KEY) || DEFAULT_PHRASE);
  const [listening, setListening] = useState(false);
  const [status, setStatus] = useState("Set your end phrase, then tap Start and speak.");
  const [transcript, setTranscript] = useState("");
  const [log, setLog] = useState<LogEntry[]>([]);
  const [result, setResult] = useState<DetectResult | null>(null);
  const [level, setLevel] = useState(0);

  const recorderRef = useRef<MicRecorder | null>(null);
  const pollRef = useRef<number | null>(null);
  const levelRef = useRef<number | null>(null);
  const busyRef = useRef(false);
  const listeningRef = useRef(false);
  const startAtRef = useRef(0);
  const lastLoudAtRef = useRef(0);
  const phraseRef = useRef(phrase);
  phraseRef.current = phrase;

  useEffect(() => {
    localStorage.setItem(PHRASE_KEY, phrase);
  }, [phrase]);

  const clearTimers = useCallback(() => {
    if (pollRef.current !== null) { clearInterval(pollRef.current); pollRef.current = null; }
    if (levelRef.current !== null) { clearInterval(levelRef.current); levelRef.current = null; }
  }, []);

  const stopListening = useCallback(async () => {
    listeningRef.current = false;
    setListening(false);
    clearTimers();
    const rec = recorderRef.current;
    recorderRef.current = null;
    if (rec && rec.isRecording) {
      try { await rec.stop(); } catch { /* tearing down */ }
    }
  }, [clearTimers]);

  const onDetected = useCallback((command: string, heard: string) => {
    playReadyCue();
    const latencyMs = Math.max(0, Math.round(performance.now() - lastLoudAtRef.current));
    setResult({ latencyMs, command, transcript: heard });
    setStatus(`DETECTED "${phraseRef.current}" - fired ${latencyMs} ms after you stopped speaking.`);
    void stopListening();
  }, [stopListening]);

  // One rolling check: transcribe what has been captured so far and test the end phrase.
  const poll = useCallback(async () => {
    if (busyRef.current || !listeningRef.current) return;
    const rec = recorderRef.current;
    if (rec === null) return;
    busyRef.current = true;
    try {
      const clip = rec.snapshot();
      if (clip.size < MIN_CLIP_BYTES) return;
      const t0 = performance.now();
      const { wav } = await blobToWav16kMono(clip);
      const heard = (await transcribeCarModeAudio(wav)).trim();
      const transcribeMs = Math.round(performance.now() - t0);
      if (!listeningRef.current) return;
      const det = detectPhraseAtEnd(heard, phraseRef.current);
      setTranscript(heard);
      setLog((prev) => [
        { atMs: Math.round(performance.now() - startAtRef.current), transcript: heard, matched: det.ended, transcribeMs },
        ...prev,
      ].slice(0, 20));
      if (det.ended) onDetected(det.command, heard);
    } catch (err) {
      setStatus(`Transcription error: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      busyRef.current = false;
    }
  }, [onDetected]);

  const startListening = useCallback(async () => {
    if (listeningRef.current) return;
    setResult(null);
    setLog([]);
    setTranscript("");
    setStatus("Opening the microphone...");
    const rec = new MicRecorder();
    try {
      await rec.start();
    } catch (err) {
      setStatus(`Could not open the microphone: ${err instanceof Error ? err.message : String(err)}`);
      return;
    }
    recorderRef.current = rec;
    listeningRef.current = true;
    setListening(true);
    const now = performance.now();
    startAtRef.current = now;
    lastLoudAtRef.current = now;
    setStatus(`Listening. Speak, and finish with "${phraseRef.current}".`);

    levelRef.current = window.setInterval(() => {
      const lvl = recorderRef.current?.level() ?? 0;
      setLevel(lvl);
      if (lvl > LEVEL_SPEAKING) lastLoudAtRef.current = performance.now();
    }, LEVEL_MS);
    pollRef.current = window.setInterval(() => { void poll(); }, POLL_MS);
  }, [poll]);

  useEffect(() => () => { void stopListening(); }, [stopListening]);

  return (
    <div style={S.page}>
      <h1 style={S.h1}>Car Mode - End Word Test</h1>
      <p style={S.sub}>
        Set the phrase that ends your turn, tap Start, speak a command, and finish with the phrase. It
        checks about once a second and fires the moment your words end with the phrase.
      </p>

      <label style={S.label}>End phrase</label>
      <input
        style={S.input}
        value={phrase}
        disabled={listening}
        onChange={(e) => setPhrase(e.target.value)}
        placeholder="over and out"
        autoCapitalize="none"
        autoCorrect="off"
      />

      <button
        style={{ ...S.button, ...(listening ? S.buttonStop : S.buttonStart) }}
        onClick={() => (listening ? void stopListening() : void startListening())}
      >
        {listening ? "Stop listening" : "Start listening"}
      </button>

      {listening && (
        <div style={S.meterWrap}>
          <div style={{ ...S.meterFill, width: `${Math.min(100, Math.round(level * 200))}%` }} />
        </div>
      )}

      <p style={S.status}>{status}</p>

      {result && (
        <div style={S.result}>
          <div style={S.resultTitle}>DETECTED</div>
          <div>Delay after you stopped speaking: <b>{result.latencyMs} ms</b></div>
          <div>Command (phrase stripped): <b>{result.command || "(none)"}</b></div>
          <div style={S.dim}>heard: "{result.transcript}"</div>
        </div>
      )}

      {transcript && !result && <p style={S.live}>heard so far: "{transcript}"</p>}

      {log.length > 0 && (
        <div style={S.logWrap}>
          <div style={S.label}>Checks (newest first)</div>
          {log.map((e, i) => (
            <div key={i} style={{ ...S.logRow, color: e.matched ? "#0a7d28" : "#444" }}>
              <span style={S.logT}>{(e.atMs / 1000).toFixed(1)}s</span>
              <span style={S.logMs}>{e.transcribeMs}ms</span>
              <span style={S.logMatch}>{e.matched ? "[MATCH]" : "[  -  ]"}</span>
              <span style={S.logText}>"{e.transcript}"</span>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

const S: Record<string, React.CSSProperties> = {
  page: { minHeight: "100dvh", boxSizing: "border-box", padding: "16px 16px calc(24px + env(safe-area-inset-bottom))", maxWidth: 640, margin: "0 auto", fontFamily: "system-ui, sans-serif" },
  h1: { fontSize: 20, margin: "4px 0 8px" },
  sub: { fontSize: 13, color: "#555", margin: "0 0 16px", lineHeight: 1.4 },
  label: { display: "block", fontSize: 12, textTransform: "uppercase", letterSpacing: 0.5, color: "#666", margin: "12px 0 6px" },
  input: { width: "100%", boxSizing: "border-box", fontSize: 18, padding: "12px 14px", borderRadius: 10, border: "1px solid #bbb" },
  button: { width: "100%", boxSizing: "border-box", fontSize: 18, fontWeight: 600, padding: "16px", borderRadius: 12, border: "none", color: "#fff", marginTop: 16, cursor: "pointer" },
  buttonStart: { background: "#1565c0" },
  buttonStop: { background: "#c62828" },
  meterWrap: { height: 8, background: "#e0e0e0", borderRadius: 4, overflow: "hidden", marginTop: 12 },
  meterFill: { height: "100%", background: "#1565c0", transition: "width 80ms linear" },
  status: { fontSize: 14, margin: "14px 0", minHeight: 20 },
  result: { background: "#e8f5e9", border: "1px solid #a5d6a7", borderRadius: 12, padding: 14, fontSize: 15, lineHeight: 1.6 },
  resultTitle: { fontSize: 18, fontWeight: 700, color: "#0a7d28", marginBottom: 6 },
  dim: { color: "#666", fontSize: 13, marginTop: 4 },
  live: { fontSize: 15, color: "#333", fontStyle: "italic" },
  logWrap: { marginTop: 18 },
  logRow: { display: "flex", gap: 8, fontSize: 12, fontFamily: "ui-monospace, monospace", padding: "3px 0", borderTop: "1px solid #eee", alignItems: "baseline" },
  logT: { width: 42, color: "#999", flex: "0 0 auto" },
  logMs: { width: 52, color: "#999", flex: "0 0 auto" },
  logMatch: { width: 60, flex: "0 0 auto" },
  logText: { flex: 1, overflow: "hidden", textOverflow: "ellipsis", whiteSpace: "nowrap" },
};
