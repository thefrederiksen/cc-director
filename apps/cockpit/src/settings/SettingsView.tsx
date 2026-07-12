import { useCallback, useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import {
  getGatewaySettings,
  setAddressingMode,
  setAutostart,
  setSnoozeDefaultMinutes,
  setTrainingCapture,
  type AddressingMode,
  type GatewaySettings,
} from "@devthrottle/client-core/settings/settingsClient";
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
  testChat,
  ttsSample,
} from "@devthrottle/client-core/api/ai";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import {
  disablePush,
  enablePush,
  isPushSubscribed,
  notificationPermission,
  pushSupported,
} from "@devthrottle/client-core/push/register";

// The Cockpit Settings page (issue #1025, epic #967) - the React port of the retired Blazor
// wwwroot/pages/settings.html. The left-rail "Settings" item used to be a dead full-load anchor to
// /settings (nothing served it, so it fell through to the SPA "Not found"); this is the real page it now
// routes to. It ports the two tabs the issue names: "This machine" (the Gateway connection: process
// diagnostics, network addressing, startup, training capture) and "AI" (DevThrottle-hosted models,
// transcription, wingman, and voice).
//
// A pure client of existing Gateway endpoints, same-origin (root-relative URLs, never a Director
// address). Responsive (CodingStyle.md): each tab renders immediately with a loading line and loads
// asynchronously; on a failure it shows an explicit error banner, never a fabricated value.

const SAMPLE_TEXT = "Hi, I'm your DevThrottle wingman. This is how I'll sound.";

type TabId = "machine" | "ai";

export function SettingsView() {
  const [tab, setTab] = useState<TabId>("machine");
  return (
    <div className="page settings">
      <div className="page-head">
        <h1>Settings</h1>
      </div>
      <p className="settings-lede">Gateway and fleet configuration for this machine.</p>

      <p className="settings-relocated">
        Looking for something else? Your <Link to="/account">DevThrottle account</Link>,{" "}
        <Link to="/telemetry">usage telemetry</Link>, and <Link to="/about">Gateway diagnostics</Link>{" "}
        each have their own page.
      </p>

      <div className="settings-tabs" role="tablist" aria-label="Settings sections">
        <button
          type="button"
          role="tab"
          aria-selected={tab === "machine"}
          className={tab === "machine" ? "settings-tab active" : "settings-tab"}
          onClick={() => setTab("machine")}
        >
          This machine
        </button>
        <button
          type="button"
          role="tab"
          aria-selected={tab === "ai"}
          className={tab === "ai" ? "settings-tab active" : "settings-tab"}
          onClick={() => setTab("ai")}
        >
          AI
        </button>
      </div>

      {tab === "machine" ? <ThisMachineTab /> : <AiTab />}
    </div>
  );
}

// ---- "This machine" tab: Gateway connection + startup ---------------------------------------------

function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="settings-row">
      <div className="settings-row-label">{label}</div>
      <div className="settings-row-value">{value}</div>
    </div>
  );
}

function formatUptime(totalSeconds: number): string {
  if (totalSeconds <= 0) return "just started";
  const days = Math.floor(totalSeconds / 86400);
  const hours = Math.floor((totalSeconds % 86400) / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const parts: string[] = [];
  if (days > 0) parts.push(`${days}d`);
  if (hours > 0) parts.push(`${hours}h`);
  parts.push(`${minutes}m`);
  return parts.join(" ");
}

function ThisMachineTab() {
  const [settings, setSettings] = useState<GatewaySettings | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState("");
  // Snooze Length mission: a draft of the default-snooze-length input, synced from the loaded settings.
  const [snoozeDraft, setSnoozeDraft] = useState("");

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      setError(null);
      setSettings(await getGatewaySettings(signal));
    } catch (e) {
      if (signal?.aborted) return;
      setError(errText(e));
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  // Keep the snooze input in sync with the loaded/saved value.
  useEffect(() => {
    if (settings !== null) setSnoozeDraft(String(settings.snoozeDefaultMinutes));
  }, [settings?.snoozeDefaultMinutes]);

  const chooseAddressing = async (mode: AddressingMode) => {
    if (settings === null || busy || mode === settings.addressingMode) return;
    setBusy(true);
    setMsg("Saving...");
    try {
      const applied = await setAddressingMode(mode);
      setSettings({ ...settings, addressingMode: applied });
      setMsg(
        applied === "lan"
          ? "LAN mode saved. Applies to this machine's Directors on their next restart."
          : "Tailscale mode saved. Applies to this machine's Directors on their next restart.",
      );
    } catch (e) {
      setMsg(errText(e));
    } finally {
      setBusy(false);
    }
  };

  const toggleAutostart = async (enabled: boolean) => {
    if (settings === null || busy) return;
    setBusy(true);
    setMsg("Saving...");
    try {
      const state = await setAutostart(enabled);
      setSettings({ ...settings, autostart: state });
      setMsg(state.enabled ? "The Gateway will start when you log in." : "Autostart turned off.");
    } catch (e) {
      setMsg(errText(e));
    } finally {
      setBusy(false);
    }
  };

  const toggleTraining = async (enabled: boolean) => {
    if (settings === null || busy) return;
    setBusy(true);
    setMsg("Saving...");
    try {
      const applied = await setTrainingCapture(enabled);
      setSettings({ ...settings, wingmanTrainingCapture: applied });
      setMsg(applied ? "Capturing wingman training data." : "Training capture turned off.");
    } catch (e) {
      setMsg(errText(e));
    } finally {
      setBusy(false);
    }
  };

  const saveSnooze = async () => {
    if (settings === null || busy) return;
    const minutes = Number(snoozeDraft);
    if (!Number.isInteger(minutes) || minutes < 1 || minutes > 7 * 24 * 60) {
      setMsg("Snooze length must be a whole number of minutes from 1 to 10080.");
      return;
    }
    setBusy(true);
    setMsg("Saving...");
    try {
      const applied = await setSnoozeDefaultMinutes(minutes);
      setSettings({ ...settings, snoozeDefaultMinutes: applied });
      setMsg(`Snooze length set to ${applied} minute${applied === 1 ? "" : "s"}. Applies to the next snooze, on every device.`);
    } catch (e) {
      setMsg(errText(e));
    } finally {
      setBusy(false);
    }
  };

  if (error !== null) {
    return <div className="settings-error">Could not load settings from the Gateway: {error}</div>;
  }
  if (settings === null) {
    return <p className="settings-loading">Loading...</p>;
  }

  return (
    <>
      <section className="settings-card">
        <h2 className="settings-h2">Gateway</h2>
        <p className="settings-hint">The Gateway process serving this page and supervising the fleet.</p>
        <Row label="State" value={settings.state} />
        <Row label="Port" value={String(settings.port)} />
        <Row label="Mode" value={settings.mode} />
        <Row label="Cockpit" value={cockpitLabel(settings)} />
        <Row label="Uptime" value={formatUptime(settings.uptimeSeconds)} />
        <Row label="Version" value={settings.version} />
        <Row label="Directors" value={String(settings.directors)} />
      </section>

      <section className="settings-card">
        <h2 className="settings-h2">Network addressing</h2>
        <p className="settings-hint">
          How machines in the fleet address one another. Tailscale (default): each Director is reached
          over its Tailscale front door. LAN: each Director is reached on its real LAN IP - use only on a
          trusted network (LAN mode also turns on Director authentication). This is a per-machine setting
          read at startup; it applies to this host&apos;s Directors on their next restart.
        </p>
        <div className="settings-field">
          <label htmlFor="settings-addrmode">Mode</label>
          <select
            id="settings-addrmode"
            className="settings-select"
            value={settings.addressingMode}
            disabled={busy}
            onChange={(e) => void chooseAddressing(e.target.value === "lan" ? "lan" : "tailscale")}
          >
            <option value="tailscale">Tailscale (front door)</option>
            <option value="lan">LAN (direct IP)</option>
          </select>
        </div>
      </section>

      <section className="settings-card">
        <h2 className="settings-h2">Startup</h2>
        <p className="settings-hint">
          Registers a per-user Run entry so the Gateway starts when you log in. The fleet only works
          while you are logged in, so this is per-user, never a machine service.
        </p>
        <label className="settings-check">
          <input
            type="checkbox"
            checked={settings.autostart.enabled === true}
            disabled={busy || !settings.autostart.supported}
            onChange={(e) => void toggleAutostart(e.target.checked)}
          />
          Start the Gateway when I log in
        </label>
        {!settings.autostart.supported && (
          <p className="settings-hint settings-hint-inline">Not supported on this host (no tray).</p>
        )}
      </section>

      <section className="settings-card">
        <h2 className="settings-h2">Snooze</h2>
        <p className="settings-hint">
          How long a snoozed session stays parked before it returns to &quot;needs you&quot; on its own -
          a dead-man&apos;s switch, so a session you snooze can never be lost. One length for every snooze,
          the same on every device. Read at snooze time, so a change applies to the next snooze.
        </p>
        <div className="settings-field">
          <label htmlFor="settings-snooze">Default snooze length (minutes)</label>
          <input
            id="settings-snooze"
            className="settings-input"
            type="number"
            min={1}
            max={10080}
            step={1}
            value={snoozeDraft}
            disabled={busy}
            onChange={(e) => setSnoozeDraft(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") void saveSnooze();
            }}
          />
          <button type="button" className="settings-btn" disabled={busy} onClick={() => void saveSnooze()}>
            Save
          </button>
        </div>
      </section>

      <NotificationsCard />

      <section className="settings-card">
        <h2 className="settings-h2">
          Training data <span className="settings-pill">improve the wingman</span>
        </h2>
        <p className="settings-hint">
          When on, every wingman voice summary saves the session terminal plus the wingman&apos;s spoken
          response to the Gateway machine - a labeled dataset for testing and improving the wingman.
          Takes effect immediately, no restart.
        </p>
        <label className="settings-check">
          <input
            type="checkbox"
            checked={settings.wingmanTrainingCapture}
            disabled={busy}
            onChange={(e) => void toggleTraining(e.target.checked)}
          />
          Capture wingman training data
        </label>
      </section>

      {msg !== "" && <div className="settings-msg">{msg}</div>}
    </>
  );
}

function cockpitLabel(settings: GatewaySettings): string {
  const state = settings.cockpit.up ? "up" : "down";
  return `port ${settings.cockpit.port} (${state})`;
}

// ---- Browser notifications (issue #1257) ----------------------------------------------------------
//
// Per-browser toggle that subscribes THIS browser to the Gateway's existing "needs you" web push (the
// same pipe the phone uses, #905) so a backgrounded Cockpit tab raises a desktop notification when a
// session turns red. The subscribe/unsubscribe flow is the shared client-core push module; this card is
// only its on/off switch plus the browser permission prompt behind it. Self-contained state (support,
// permission, subscribed) so it never depends on the Gateway settings load - notifications are a
// property of this browser, not of the machine's Gateway config. Turning it off unsubscribes this
// browser only; a phone or another browser stays subscribed.

function NotificationsCard() {
  const supported = pushSupported();
  const [permission, setPermission] = useState<NotificationPermission | "unsupported">(
    notificationPermission(),
  );
  const [enabled, setEnabled] = useState<boolean | null>(null);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState("");

  useEffect(() => {
    if (!supported) {
      setEnabled(false);
      return;
    }
    let cancelled = false;
    void isPushSubscribed().then((on) => {
      if (!cancelled) setEnabled(on);
    });
    return () => {
      cancelled = true;
    };
  }, [supported]);

  const onToggle = async (checked: boolean) => {
    if (busy) return;
    setBusy(true);
    setMsg("Saving...");
    try {
      if (checked) {
        const result = await enablePush();
        setPermission(notificationPermission());
        if (result === "granted") {
          setEnabled(true);
          setMsg("On. This browser will be notified when a session needs you, even in the background.");
        } else if (result === "denied") {
          setEnabled(false);
          setMsg("This browser blocked notifications. Allow them in the browser's site settings, then try again.");
        } else {
          setEnabled(false);
          setMsg("This browser does not support notifications.");
        }
      } else {
        await disablePush();
        setEnabled(false);
        setMsg("Off. Notifications stopped for this browser only.");
      }
    } catch (e) {
      setMsg(errText(e));
    } finally {
      setBusy(false);
    }
  };

  return (
    <section className="settings-card">
      <h2 className="settings-h2">
        Notifications <span className="settings-pill">this browser</span>
      </h2>
      <p className="settings-hint">
        Get a desktop notification when a session needs you, even when this tab is in the background or
        the browser is minimized. Clicking the notification brings the Cockpit forward and opens the
        session that is waiting. This is per-browser: turning it off here stops notifications for this
        browser only, and your phone keeps its own alerts.
      </p>
      <label className="settings-check">
        <input
          type="checkbox"
          checked={enabled === true}
          disabled={!supported || busy || enabled === null || permission === "denied"}
          onChange={(e) => void onToggle(e.target.checked)}
        />
        Notify me on this browser
      </label>
      {!supported && (
        <p className="settings-hint settings-hint-inline">
          This browser does not support web notifications.
        </p>
      )}
      {supported && permission === "denied" && (
        <p className="settings-hint settings-hint-inline">
          Notifications are blocked for this site in the browser. Allow them in the browser&apos;s site
          settings to turn this on.
        </p>
      )}
      {msg !== "" && <div className="settings-msg">{msg}</div>}
    </section>
  );
}

// ---- "AI" tab: DevThrottle-hosted models + voice --------------------------------------------------

function AiTab() {
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
    return <div className="settings-error">Could not load AI settings: {error}</div>;
  }
  if (snap === null) {
    return <p className="settings-loading">Loading...</p>;
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
    <section className="settings-card">
      <h2 className="settings-h2">AI</h2>
      <p className="settings-hint">
        DevThrottle hosts AI for this fleet: transcription, the wingman, and spoken voice all run on
        your DevThrottle account.
      </p>

      <div className="settings-provider-cards">
        <div className="settings-provider-card selected" aria-label="DevThrottle hosted AI">
          <span className="settings-provider-title">
            <span className="settings-provider-radio on" aria-hidden="true" />
            DevThrottle
            <span className="settings-provider-badge">Hosted</span>
          </span>
          <span className="settings-provider-desc">
            Hosted models on your DevThrottle account. Billed to your account credits.
          </span>
        </div>
      </div>

      <div className="settings-field">
        <label htmlFor="settings-ai-model">Thinking model</label>
        <select
          id="settings-ai-model"
          className="settings-select"
          value={snap.wingmanModel}
          disabled={busy}
          onChange={(e) => void chooseWingman(e.target.value)}
        >
          {ensureIds(snap.wingmanModel, chatModels).map((id) => (
            <option key={id} value={id}>
              {id}
            </option>
          ))}
        </select>
        <div className="settings-actions">
          <button type="button" className="settings-btn" disabled={busy} onClick={() => void runTest()}>
            Test
          </button>
          <span className="settings-inline-msg">
            {testMsg || "Used for talk-to-the-wingman and product questions."}
          </span>
        </div>
      </div>

      <div className="settings-field">
        <label htmlFor="settings-ai-fast-model">Fast model</label>
        <select
          id="settings-ai-fast-model"
          className="settings-select"
          value={snap.wingmanFastModel}
          disabled={busy}
          onChange={(e) => void chooseFastWingman(e.target.value)}
        >
          {ensureIds(snap.wingmanFastModel, chatModels).map((id) => (
            <option key={id} value={id}>
              {id}
            </option>
          ))}
        </select>
        <div className="settings-actions">
          <button type="button" className="settings-btn" disabled={busy} onClick={() => void runFastTest()}>
            Test
          </button>
          <span className="settings-inline-msg">
            {fastTestMsg || "Used for spoken turn summaries, menus, and choice mapping."}
          </span>
        </div>
      </div>

      <div className="settings-field">
        <label htmlFor="settings-ai-carmode-model">Car Mode model</label>
        <select
          id="settings-ai-carmode-model"
          className="settings-select"
          value={snap.carModeModel}
          disabled={busy}
          onChange={(e) => void chooseCarMode(e.target.value)}
        >
          {ensureIds(snap.carModeModel, chatModels).map((id) => (
            <option key={id} value={id}>
              {id}
            </option>
          ))}
        </select>
        <div className="settings-actions">
          <span className="settings-inline-msg">
            Hands-free fleet control from the phone. A fast model is recommended; GLM-5.2 is slower but a strong tool-caller.
          </span>
        </div>
      </div>

      <div className="settings-field">
        <label htmlFor="settings-ai-ttsmodel">Speech model</label>
        <select
          id="settings-ai-ttsmodel"
          className="settings-select"
          value={snap.ttsModel}
          disabled={busy}
          onChange={(e) => void chooseSpeech(e.target.value)}
        >
          {ensureIds(snap.ttsModel, speechModels).map((id) => (
            <option key={id} value={id}>
              {id}
            </option>
          ))}
        </select>
      </div>

      <div className="settings-field">
        <label htmlFor="settings-ai-voice">Voice</label>
        <select
          id="settings-ai-voice"
          className="settings-select"
          value={snap.ttsVoice}
          disabled={busy}
          onChange={(e) => void chooseVoice(e.target.value)}
        >
          {ensureStrings(snap.ttsVoice, voiceOptions).map((v) => (
            <option key={v} value={v}>
              {v}
            </option>
          ))}
        </select>
        <div className="settings-actions">
          <button type="button" className="settings-btn" disabled={busy} onClick={() => void playSample()}>
            Play sample
          </button>
          <span className="settings-inline-msg">{sampleMsg}</span>
        </div>
      </div>

      <Row label="Transcription" value={snap.transcriptionModel} />

      <p className="settings-hint settings-hint-inline">
        Hosted AI runs on your DevThrottle account. <Link to="/account">Manage account</Link>.
      </p>

      {msg !== "" && <div className="settings-msg">{msg}</div>}
    </section>
  );
}

// Build the option id list, guaranteeing the currently-saved id is present + first even when the catalog
// failed to load or does not list it (so the <select> value always matches an option).
function ensureIds(current: string, models: AiModel[]): string[] {
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
  return gatewayErrorMessage(e);
}
