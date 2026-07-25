import { useCallback, useEffect, useRef, useState, type ReactNode } from "react";
import { Link } from "react-router-dom";
import {
  type AiModel,
  type AiProviderSnapshot,
  getAiModels,
  getAiProvider,
  setCarModeModel,
  setWingmanFastModel,
  setTtsModel,
  setTtsVoice,
  setWingmanModel,
  setSpokenLanguage,
  getSpokenLanguages,
  type SpokenLanguageOption,
  testChat,
  ttsSample,
} from "@devthrottle/client-core/api/ai";

// The sample sentence, per language. Auditioning a Danish voice by making it read an English
// sentence is not an audition - you cannot hear the accent you are actually buying. Falls back to
// English for any language we have not written a line for yet.
const SAMPLES: Record<string, string> = {
  en: "Hi, I'm your DevThrottle wingman. This is how I'll sound.",
  da: "Hej, jeg er din DevThrottle wingman. Sadan kommer jeg til at lyde.",
  de: "Hallo, ich bin dein DevThrottle Wingman. So werde ich klingen.",
  fr: "Bonjour, je suis votre wingman DevThrottle. Voici ma voix.",
  es: "Hola, soy tu wingman de DevThrottle. Asi es como voy a sonar.",
  pt: "Ola, eu sou o seu wingman do DevThrottle. E assim que eu vou soar.",
  it: "Ciao, sono il tuo wingman DevThrottle. Ecco come suonero.",
  nl: "Hallo, ik ben je DevThrottle wingman. Zo ga ik klinken.",
  sv: "Hej, jag ar din DevThrottle wingman. Sa har kommer jag att lata.",
  no: "Hei, jeg er din DevThrottle wingman. Slik kommer jeg til a hores ut.",
  tr: "Merhaba, ben DevThrottle yardimcinizim. Sesim boyle olacak.",
};
const SAMPLE_TEXT = SAMPLES.en;

// The mobile AI settings screen. Same controls as the desktop AI tab, stacked for touch: hosted
// DevThrottle AI, the wingman model (with a live Test), the speech model, and the voice (with Play
// sample so you can hear it on the phone). Pure client of the Gateway AI endpoints.
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
  const [languages, setLanguages] = useState<SpokenLanguageOption[]>([]);
  const [language, setLanguage] = useState("en");
  const audioRef = useRef<HTMLAudioElement | null>(null);

  const loadModels = useCallback(async () => {
    setChatModels(await getAiModels("chat"));
    setSpeechModels(await getAiModels("speech"));
    const spoken = await getSpokenLanguages();
    setLanguages(spoken.languages);
    setLanguage(spoken.current);
  }, []);

  const load = useCallback(async () => {
    try {
      setError(null);
      setSnap(await getAiProvider());
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

  // An expressive model with no preset voices is not a model with a missing voice list - there is
  // nothing to choose. Showing an empty picker reads as broken, so the control is hidden instead.
  const modelHasVoices = !currentSpeech || currentSpeech.voices.length > 0;
  const voiceOptions = currentSpeech && currentSpeech.voices.length ? currentSpeech.voices : snap.voices;

  const chooseLanguage = async (code: string) => {
    setBusy(true);
    setMsg("Saving...");
    setSampleMsg("");
    try {
      // The Gateway decides which speech model can say this and switches it; we only report what
      // it did. The browser cannot make that call on hosted - the model catalog is refused there.
      const res = await setSpokenLanguage(code);
      setLanguage(res.language);
      if (res.ttsModel) {
        setSnap({ ...snap, ttsModel: res.ttsModel, ttsVoice: res.ttsVoice ?? "" });
      }
      setMsg(res.switched
        ? "Spoken language set. Speech model switched to " + res.ttsModel + ", which can speak it."
        : "Spoken language set.");
    } catch (e) {
      setMsg(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  };

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

  const chooseCarMode = async (model: string) => {
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
      const blob = await ttsSample(SAMPLES[language] ?? SAMPLE_TEXT, snap.ttsModel, snap.ttsVoice);
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
        DevThrottle hosts transcription, wingman thinking, and spoken voice for this whole fleet.
      </p>

      <div className="setting-block">
        <div className="setting-label">Provider</div>
        <div className="provider-cards">
          <div className="provider-card selected" aria-label="DevThrottle hosted AI">
            <span className="provider-card-title">
              <span className="provider-radio on" aria-hidden="true" />
              DevThrottle
              <span className="provider-badge">Hosted</span>
            </span>
            <span className="provider-card-desc">Hosted models on your DevThrottle account.</span>
          </div>
        </div>
      </div>

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
        <label className="setting-label" htmlFor="ai-carmode-model">Car Mode model</label>
        <select id="ai-carmode-model" className="setting-select" value={snap.carModeModel} disabled={busy} onChange={(e) => void chooseCarMode(e.target.value)}>
          {ensure(snap.carModeModel, chatModels).map((id) => (
            <option key={id} value={id}>{id}</option>
          ))}
        </select>
        <div className="setting-actions">
          <span className="setting-msg">Hands-free fleet control. Fast model recommended; GLM-5.2 is slower but a strong tool-caller.</span>
        </div>
      </div>

      <div className="setting-block">
        <label className="setting-label" htmlFor="ai-spoken-language">Spoken language</label>
        <select id="ai-spoken-language" className="setting-select" value={language} disabled={busy} onChange={(e) => void chooseLanguage(e.target.value)}>
          {languages.map((l) => (
            <option key={l.code} value={l.code}>
              {l.name === l.endonym ? l.name : l.name + " (" + l.endonym + ")"}
            </option>
          ))}
        </select>
        <p className="setting-hint">
          The language DevThrottle speaks back to you in. Dictation understands every language
          automatically, so this does not change how you talk to it. Your agents keep working in
          whatever language they work in.
        </p>
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
        {modelHasVoices ? (
          <>
            <label className="setting-label" htmlFor="ai-voice">Voice</label>
            <select id="ai-voice" className="setting-select" value={snap.ttsVoice} disabled={busy} onChange={(e) => void chooseVoice(e.target.value)}>
              {ensureStrings(snap.ttsVoice, voiceOptions).map((v) => (
                <option key={v} value={v}>{v}</option>
              ))}
            </select>
          </>
        ) : (
          <p className="setting-hint">This speech model has one expressive voice - there is nothing to choose.</p>
        )}
        <div className="setting-actions">
          <button type="button" className="setting-btn" disabled={busy} onClick={() => void playSample()}>Play sample</button>
          <span className="setting-msg">{sampleMsg}</span>
        </div>
      </div>

      <div className="setting-readonly">
        Transcription: <span className="mono">{snap.transcriptionModel}</span>
      </div>

      {/* Discovery: someone who came here because dictation is poor is looking at the transcription
          model, when the cause is far more often the microphone. Point them at the check. */}
      <div className="setting-block">
        <div className="setting-label">Dictation coming out wrong?</div>
        <div className="setting-actions">
          <Link className="setting-btn" to="/mic-test">
            Test microphone
          </Link>
          <Link className="setting-btn" to="/transcription-test">
            Test transcription
          </Link>
        </div>
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
