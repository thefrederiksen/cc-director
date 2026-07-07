import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { Link } from "react-router-dom";
import {
  type AiModel,
  type AiProviderSnapshot,
  getAiModels,
  getAiProvider,
  setAiProvider,
  setWingmanFastModel,
  setTtsModel,
  setTtsVoice,
  setWingmanModel,
  testChat,
  ttsSample,
} from "@devthrottle/client-core/api/ai";

const SAMPLE_TEXT = "Hi, I'm your DevThrottle wingman. This is how I'll sound.";

// The mobile AI settings screen (issue: mobile settings). Same controls as the desktop AI tab, stacked
// for touch: the wingman model (with a live Test), the speech model, and the voice (with Play sample so
// you can hear it on the phone). All AI is DevThrottle-hosted - the bring-your-own OpenAI provider choice
// was removed. Pure client of the Gateway AI endpoints.
export function AiSettings() {
  const [snap, setSnap] = useState<AiProviderSnapshot | null>(null);
  const [chatModels, setChatModels] = useState<AiModel[]>([]);
  const [speechModels, setSpeechModels] = useState<AiModel[]>([]);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState("");
  const [testMsg, setTestMsg] = useState("");
  const [fastTestMsg, setFastTestMsg] = useState("");
  const [sampleMsg, setSampleMsg] = useState("");
  const audioRef = useRef<HTMLAudioElement | null>(null);

  const loadModels = useCallback(async () => {
    setChatModels(await getAiModels("chat"));
    setSpeechModels(await getAiModels("speech"));
  }, []);

  const load = useCallback(async () => {
    try {
      setError(null);
      let s = await getAiProvider();
      // Bring-your-own OpenAI was removed - all AI is DevThrottle-hosted. Migrate any machine still on
      // the OpenAI provider back to DevThrottle so its models, voice, and transcription match the only
      // provider the app now offers.
      if (s.provider !== "devthrottle") s = await setAiProvider("devthrottle");
      setSnap(s);
      await loadModels();
    } catch (e) {
      setError(errText(e));
    }
  }, [loadModels]);

  useEffect(() => {
    void load();
  }, [load]);

  if (error !== null) {
    return (
      <Frame>
        <div className="banner banner-error" role="alert">
          Could not load AI settings: {error}
        </div>
      </Frame>
    );
  }
  if (snap === null) {
    return (
      <Frame>
        <p className="status-line">Loading...</p>
      </Frame>
    );
  }

  const currentSpeech = speechModels.find((m) => m.id === snap.ttsModel);
  const voiceOptions = currentSpeech && currentSpeech.voices.length ? currentSpeech.voices : snap.voices;

  const chooseWingman = async (model: string) => {
    setBusy(true);
    setMsg("Saving...");
    setTestMsg("");
    try {
      await setWingmanModel(model);
      setSnap({ ...snap, wingmanModel: model });
      setMsg("Thinking model set. Test it to confirm.");
    } catch (e) {
      setMsg(errText(e));
    } finally {
      setBusy(false);
    }
  };

  const runTest = async () => {
    setBusy(true);
    setTestMsg("Testing " + snap.wingmanModel + "...");
    const r = await testChat(snap.wingmanModel);
    setTestMsg(r.ok ? `OK - replied "${r.reply}" in ${r.seconds}s.` : "Failed: " + r.error);
    setBusy(false);
  };

  const chooseFastWingman = async (model: string) => {
    setBusy(true);
    setMsg("Saving...");
    setFastTestMsg("");
    try {
      await setWingmanFastModel(model);
      setSnap({ ...snap, wingmanFastModel: model });
      setMsg("Fast model set. Test it to confirm.");
    } catch (e) {
      setMsg(errText(e));
    } finally {
      setBusy(false);
    }
  };

  const runFastTest = async () => {
    setBusy(true);
    setFastTestMsg("Testing " + snap.wingmanFastModel + "...");
    const r = await testChat(snap.wingmanFastModel);
    setFastTestMsg(r.ok ? `OK - replied "${r.reply}" in ${r.seconds}s.` : "Failed: " + r.error);
    setBusy(false);
  };

  const chooseSpeech = async (model: string) => {
    setBusy(true);
    setMsg("Saving...");
    try {
      await setTtsModel(model);
      const sm = speechModels.find((m) => m.id === model);
      const voices = sm && sm.voices.length ? sm.voices : snap.voices;
      let voice = snap.ttsVoice;
      if (voices.indexOf(voice) < 0) {
        voice = sm && sm.defaultVoice && voices.indexOf(sm.defaultVoice) >= 0 ? sm.defaultVoice : voices[0] ?? voice;
        if (voice) await setTtsVoice(voice);
      }
      setSnap({ ...snap, ttsModel: model, ttsVoice: voice });
      setMsg("Speech model set.");
    } catch (e) {
      setMsg(errText(e));
    } finally {
      setBusy(false);
    }
  };

  const chooseVoice = async (voice: string) => {
    setBusy(true);
    setMsg("Saving...");
    try {
      await setTtsVoice(voice);
      setSnap({ ...snap, ttsVoice: voice });
      setMsg("Voice set to " + voice + ".");
    } catch (e) {
      setMsg(errText(e));
    } finally {
      setBusy(false);
    }
  };

  const playSample = async () => {
    setBusy(true);
    setSampleMsg("Synthesizing...");
    try {
      const blob = await ttsSample(SAMPLE_TEXT, snap.ttsModel, snap.ttsVoice);
      if (audioRef.current === null) audioRef.current = new Audio();
      audioRef.current.src = URL.createObjectURL(blob);
      audioRef.current.onended = () => setSampleMsg("");
      setSampleMsg("Playing " + snap.ttsVoice + "...");
      await audioRef.current.play();
    } catch (e) {
      setSampleMsg(errText(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Frame>
      <p className="settings-intro">
        DevThrottle runs all AI - transcription, the wingman, and the spoken voice - on hosted models
        billed to your DevThrottle account. Choose which models to use below.
      </p>

      <div className="setting-block">
        <label className="setting-label" htmlFor="ai-model">Thinking model</label>
        <select id="ai-model" className="setting-select" value={snap.wingmanModel} disabled={busy} onChange={(e) => void chooseWingman(e.target.value)}>
          {ensure(snap.wingmanModel, chatModels).map((id) => (
            <option key={id} value={id}>{id}</option>
          ))}
        </select>
        <div className="setting-actions">
          <button type="button" className="setting-btn" disabled={busy} onClick={() => void runTest()}>Test</button>
          <span className="setting-msg">{testMsg || "Talk-to-the-wingman and product questions."}</span>
        </div>
      </div>

      <div className="setting-block">
        <label className="setting-label" htmlFor="ai-fast-model">Fast model</label>
        <select id="ai-fast-model" className="setting-select" value={snap.wingmanFastModel} disabled={busy} onChange={(e) => void chooseFastWingman(e.target.value)}>
          {ensure(snap.wingmanFastModel, chatModels).map((id) => (
            <option key={id} value={id}>{id}</option>
          ))}
        </select>
        <div className="setting-actions">
          <button type="button" className="setting-btn" disabled={busy} onClick={() => void runFastTest()}>Test</button>
          <span className="setting-msg">{fastTestMsg || "Spoken turn summaries and menus."}</span>
        </div>
      </div>

      <div className="setting-block">
        <label className="setting-label" htmlFor="ai-ttsmodel">Speech model</label>
        <select id="ai-ttsmodel" className="setting-select" value={snap.ttsModel} disabled={busy} onChange={(e) => void chooseSpeech(e.target.value)}>
          {ensure(snap.ttsModel, speechModels).map((id) => (
            <option key={id} value={id}>{id}</option>
          ))}
        </select>
      </div>

      <div className="setting-block">
        <label className="setting-label" htmlFor="ai-voice">Voice</label>
        <select id="ai-voice" className="setting-select" value={snap.ttsVoice} disabled={busy} onChange={(e) => void chooseVoice(e.target.value)}>
          {ensureStrings(snap.ttsVoice, voiceOptions).map((v) => (
            <option key={v} value={v}>{v}</option>
          ))}
        </select>
        <div className="setting-actions">
          <button type="button" className="setting-btn" disabled={busy} onClick={() => void playSample()}>Play sample</button>
          <span className="setting-msg">{sampleMsg}</span>
        </div>
      </div>

      <div className="setting-readonly">
        Transcription: <span className="mono">{snap.transcriptionModel}</span>
      </div>

      {msg !== "" && <div className="setting-msg setting-msg-foot">{msg}</div>}
    </Frame>
  );
}

function Frame({ children }: { children: ReactNode }) {
  return (
    <div className="screen">
      <header className="app-bar">
        <Link className="back-link" to="/">
          Back
        </Link>
        <h1>AI settings</h1>
      </header>
      {children}
    </div>
  );
}

// Build the option id list, guaranteeing the currently-saved id is present + first-choice even when the
// catalog failed to load or does not list it (so the <select> value always matches an option).
function ensure(current: string, models: AiModel[]): string[] {
  const ids = models.map((m) => m.id);
  if (current && ids.indexOf(current) < 0) ids.unshift(current);
  return ids;
}

function ensureStrings(current: string, values: string[]): string[] {
  const out = values.slice();
  if (current && out.indexOf(current) < 0) out.unshift(current);
  return out;
}

function errText(e: unknown): string {
  return e instanceof Error ? e.message : String(e);
}
