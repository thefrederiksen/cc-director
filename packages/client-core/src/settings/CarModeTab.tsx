import { useCallback, useEffect, useRef, useState } from "react";
import {
  type AiModel,
  type AiProviderSnapshot,
  getAiModels,
  getAiProvider,
  setCarModeEndPhrase,
  setCarModeModel,
} from "../api/ai";
import { transcribeCarModeAudio } from "../carmode/carModeApi";
import { detectPhraseAtEnd } from "../carmode/controlPhrases";
import { MicRecorder } from "../dictation/recorder";
import { blobToWav16kMono } from "../dictation/wav";
import { playReadyCue, primeCueAudio, releaseCueAudio } from "../dictation/readyCue";
import { ACCOUNT_SCOPE, CardHead, ensureIds, errText } from "./settingsShared";
import "./settings.css";

// ---- "Car Mode" tab: the phone's hands-free fleet control in one place ----------------------------
//
// Its model, its sign-off phrase, and a live phrase tester. Every one of them is a Gateway setting, so
// what is set here reaches the phone where Car Mode actually runs - which is why this tab has to exist
// on the desktop too, and why the phone must be able to reach it without a laptop.
//
// Shared by both surfaces. Before they were unified the phone carried only the Car Mode MODEL, on its
// AI screen, and had no way at all to set or test the end phrase.

export function CarModeTab() {
  const [snap, setSnap] = useState<AiProviderSnapshot | null>(null);
  const [chatModels, setChatModels] = useState<AiModel[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState("");
  const [phraseDraft, setPhraseDraft] = useState("");

  const load = useCallback(async () => {
    try {
      setError(null);
      const s = await getAiProvider();
      setSnap(s);
      setPhraseDraft(s.carModeEndPhrase);
      // The model catalog stays denied on hosted (issue #2022); skip it there so the tab renders clean.
      if (s.catalogAvailable !== false) setChatModels(await getAiModels("chat"));
    } catch (e) {
      setError(errText(e));
    }
  }, []);
  useEffect(() => {
    void load();
  }, [load]);

  if (error !== null) {
    return <div className="settings-error">Could not load Car Mode settings: {error}</div>;
  }
  if (snap === null) {
    return <p className="settings-loading">Loading...</p>;
  }

  // Gateway-owned (issue #2022): false on hosted, where model browsing is disabled with a concise note.
  const catalogAvailable = snap.catalogAvailable !== false;

  const chooseModel = async (model: string) => {
    setBusy(true);
    setMsg("Saving...");
    try {
      await setCarModeModel(model);
      setSnap({ ...snap, carModeModel: model });
      setMsg("Car Mode model set.");
    } catch (e) {
      setMsg(errText(e));
    } finally {
      setBusy(false);
    }
  };

  const savePhrase = async () => {
    setBusy(true);
    setMsg("Saving...");
    try {
      const { phrase } = await setCarModeEndPhrase(phraseDraft);
      setSnap({ ...snap, carModeEndPhrase: phrase });
      setPhraseDraft(phrase);
      setMsg(`End phrase set to "${phrase}". Applies to the next Car Mode turn, on every device.`);
    } catch (e) {
      setMsg(errText(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="settings-card">
      <CardHead title="Car Mode" scope={ACCOUNT_SCOPE} />
      <p className="settings-hint">
        Hands-free fleet control from your phone. Set the model it thinks with, the phrase that ends your
        turn, and test that the phrase is heard reliably - all in one place.
      </p>

      <div className="settings-field">
        <label htmlFor="settings-carmode-model">Model</label>
        <select
          id="settings-carmode-model"
          className="settings-select"
          value={snap.carModeModel}
          disabled={busy || !catalogAvailable}
          onChange={(e) => void chooseModel(e.target.value)}
        >
          {ensureIds(snap.carModeModel, chatModels).map((id) => (
            <option key={id} value={id}>
              {id}
            </option>
          ))}
        </select>
        <div className="settings-actions">
          <span className="settings-inline-msg">
            {catalogAvailable
              ? "A fast model is recommended - it must also call tools reliably. GLM-5.2 is slower but a strong tool-caller."
              : "Model browsing isn't available on the hosted Gateway yet; your saved model is shown."}
          </span>
        </div>
      </div>

      <div className="settings-field">
        <label htmlFor="settings-carmode-phrase">End phrase (say this to finish your turn)</label>
        <input
          id="settings-carmode-phrase"
          className="settings-input"
          type="text"
          value={phraseDraft}
          disabled={busy}
          autoCapitalize="none"
          autoCorrect="off"
          placeholder="over and out"
          onChange={(e) => setPhraseDraft(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") void savePhrase();
          }}
        />
        <button type="button" className="settings-btn" disabled={busy} onClick={() => void savePhrase()}>
          Save
        </button>
      </div>

      <PhraseTester phrase={phraseDraft || snap.carModeEndPhrase} />

      {msg !== "" && <div className="settings-msg">{msg}</div>}
    </section>
  );
}

// The live phrase tester: the same detection Car Mode uses (rolling transcription through the Gateway ~once
// a second, fire when the transcript ends with the phrase), so the owner can confirm a phrase is heard
// reliably before he relies on it on the road. Uses the shared cue audio so the "heard it" beep sounds.
function PhraseTester({ phrase }: { phrase: string }) {
  const [listening, setListening] = useState(false);
  const [status, setStatus] = useState("Tap Test, speak, and finish with your phrase.");
  const [result, setResult] = useState<{ latencyMs: number; command: string } | null>(null);
  const [log, setLog] = useState<{ atMs: number; transcript: string; matched: boolean; transcribeMs: number }[]>([]);

  const recorderRef = useRef<MicRecorder | null>(null);
  const pollRef = useRef<number | null>(null);
  const levelRef = useRef<number | null>(null);
  const busyRef = useRef(false);
  const listeningRef = useRef(false);
  const startAtRef = useRef(0);
  const lastLoudRef = useRef(0);
  const phraseRef = useRef(phrase);
  phraseRef.current = phrase;

  const stopTest = useCallback(async () => {
    listeningRef.current = false;
    setListening(false);
    if (pollRef.current !== null) {
      clearInterval(pollRef.current);
      pollRef.current = null;
    }
    if (levelRef.current !== null) {
      clearInterval(levelRef.current);
      levelRef.current = null;
    }
    releaseCueAudio();
    const rec = recorderRef.current;
    recorderRef.current = null;
    if (rec && rec.isRecording) {
      try {
        await rec.stop();
      } catch {
        /* tearing down */
      }
    }
  }, []);

  const poll = useCallback(async () => {
    if (busyRef.current || !listeningRef.current) return;
    const rec = recorderRef.current;
    if (rec === null) return;
    busyRef.current = true;
    try {
      const clip = rec.snapshot();
      if (clip.size < 2000) return;
      const t0 = performance.now();
      const { wav } = await blobToWav16kMono(clip);
      const heard = (await transcribeCarModeAudio(wav)).trim();
      const transcribeMs = Math.round(performance.now() - t0);
      if (!listeningRef.current) return;
      const det = detectPhraseAtEnd(heard, phraseRef.current);
      setLog((p) =>
        [
          { atMs: Math.round(performance.now() - startAtRef.current), transcript: heard, matched: det.ended, transcribeMs },
          ...p,
        ].slice(0, 12),
      );
      if (det.ended) {
        playReadyCue();
        const latencyMs = Math.max(0, Math.round(performance.now() - lastLoudRef.current));
        setResult({ latencyMs, command: det.command });
        setStatus(`Heard "${phraseRef.current}" - ${latencyMs} ms after you stopped speaking.`);
        void stopTest();
      }
    } catch (e) {
      setStatus("Transcription error: " + errText(e));
    } finally {
      busyRef.current = false;
    }
  }, [stopTest]);

  const startTest = useCallback(async () => {
    if (listeningRef.current) return;
    if (!phraseRef.current.trim()) {
      setStatus("Set an end phrase first.");
      return;
    }
    setResult(null);
    setLog([]);
    setStatus("Opening the microphone...");
    const rec = new MicRecorder();
    try {
      await rec.start();
    } catch (e) {
      setStatus("Could not open the microphone: " + errText(e));
      return;
    }
    recorderRef.current = rec;
    listeningRef.current = true;
    setListening(true);
    primeCueAudio();
    const now = performance.now();
    startAtRef.current = now;
    lastLoudRef.current = now;
    setStatus(`Listening. Speak, then say "${phraseRef.current}".`);
    levelRef.current = window.setInterval(() => {
      const l = recorderRef.current?.level() ?? 0;
      if (l > 0.06) lastLoudRef.current = performance.now();
    }, 100);
    pollRef.current = window.setInterval(() => void poll(), 800);
  }, [poll]);

  useEffect(() => () => void stopTest(), [stopTest]);

  return (
    <div className="carmode-tester">
      <span className="carmode-tester-title">Test your phrase</span>
      <span className="carmode-tester-sub">
        Speak a command, then finish with your phrase. It uses the exact detection Car Mode uses and catches
        the phrase in about a second.
      </span>
      <button
        type="button"
        className={"settings-btn primary carmode-test-btn" + (listening ? " listening" : "")}
        onClick={() => (listening ? void stopTest() : void startTest())}
      >
        {listening ? "Listening - say your phrase, then Stop" : "Test the phrase"}
      </button>
      <div className="carmode-tester-status">{status}</div>
      {result && (
        <div className="carmode-result">
          <span className="carmode-result-badge">Heard it</span>
          Fired {result.latencyMs} ms after you stopped speaking. Command heard: &quot;
          {result.command || "(none)"}&quot;
        </div>
      )}
      {log.length > 0 && (
        <div className="carmode-log">
          {log.map((e, i) => (
            <div key={i} className={"carmode-log-row" + (e.matched ? " match" : "")}>
              {(e.atMs / 1000).toFixed(1)}s - {e.transcribeMs}ms - {e.matched ? "MATCH" : "listening"} - &quot;
              {e.transcript}&quot;
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
