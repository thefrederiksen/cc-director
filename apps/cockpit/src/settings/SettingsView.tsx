import { useCallback, useEffect, useRef, useState } from "react";
import { Link, useSearchParams } from "react-router-dom";
import { visibleTabs, tabFromParam, type TabId } from "./settingsTabs";
import {
  getGatewaySettings,
  setSnoozePresets,
  setTimeZone,
  type GatewaySettings,
  type SnoozePresets,
} from "@devthrottle/client-core/settings/settingsClient";
import {
  formatSnoozeLength,
  snoozeDraftFrom,
  snoozeMinutesFrom,
  type SnoozeUnit,
} from "@devthrottle/client-core/settings/snoozeFormat";
import {
  type AiModel,
  type AiProviderSnapshot,
  getAiModels,
  getAiProvider,
  setCarModeModel,
  setCarModeEndPhrase,
  setWingmanFastModel,
  setTtsModel,
  setTtsVoice,
  setWingmanModel,
  testChat,
  ttsSample,
} from "@devthrottle/client-core/api/ai";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import { MicRecorder } from "@devthrottle/client-core/dictation/recorder";
import { blobToWav16kMono } from "@devthrottle/client-core/dictation/wav";
import { transcribeCarModeAudio } from "@devthrottle/client-core/carmode/carModeApi";
import { playReadyCue, primeCueAudio, releaseCueAudio } from "@devthrottle/client-core/dictation/readyCue";
import { detectPhraseAtEnd } from "@devthrottle/client-core/carmode/controlPhrases";
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
// routes to.
//
// Issue #2022 - self-host IS the hosted Gateway with one tenant, so this page is IDENTICAL on both surfaces:
// per-account, three tabs, no surface branching. The machine settings left the web page (diagnostics to the
// About page, autostart to the installer + the `cc-devthrottle autostart` command, addressing dropped, brain
// removed), so there is no "This machine" tab on either surface. The page is organised by WHAT a setting is
// about:
//
//   Notifications - how a session that needs you reaches you: snooze length, display time zone, browser
//                   notifications
//   AI            - DevThrottle-hosted models, transcription, wingman, and voice
//   Car Mode      - the phone's hands-free fleet control
//
// Every card carries a scope pill ("your account" / "this browser" / "included") so the reach of a change is
// readable without reading the hint paragraph.
//
// A pure client of existing Gateway endpoints, same-origin (root-relative URLs, never a Director
// address). Responsive (CodingStyle.md): each tab renders immediately with a loading line and loads
// asynchronously; on a failure it shows an explicit error banner, never a fabricated value.

const SAMPLE_TEXT = "Hi, I'm your DevThrottle wingman. This is how I'll sound.";

export function SettingsView() {
  const [params] = useSearchParams();
  // The tab set is the same on both surfaces (issue #2022), so the initial tab is resolved straight from
  // ?tab= with no Gateway round-trip first - the page renders its tabs immediately, and each card loads its
  // own data and shows its own error banner.
  const [tab, setTab] = useState<TabId>(() => tabFromParam(params.get("tab")));

  const tabs = visibleTabs();
  return (
    <div className="page settings">
      <div className="page-head">
        <h1>Settings</h1>
      </div>
      <p className="settings-lede">Your DevThrottle account settings.</p>

      <p className="settings-relocated">
        Looking for something else? Your <Link to="/account">DevThrottle account</Link> and{" "}
        <Link to="/about">Gateway diagnostics</Link> each have their own page.
      </p>

      <div className="settings-tabs" role="tablist" aria-label="Settings sections">
        {tabs.map((t) => (
          <button
            key={t.id}
            type="button"
            role="tab"
            aria-selected={tab === t.id}
            className={tab === t.id ? "settings-tab active" : "settings-tab"}
            onClick={() => setTab(t.id)}
          >
            {t.label}
          </button>
        ))}
      </div>

      {tab === "notifications" ? (
        <NotificationsTab />
      ) : tab === "ai" ? (
        <AiTab />
      ) : (
        <CarModeTab />
      )}
    </div>
  );
}

// The scope pill for a per-account setting (issue #2022): always "your account" now that self-host is the
// hosted Gateway with one tenant. Held as a constant so every per-account card reads the same word.
const ACCOUNT_SCOPE: Scope = "your account";

// The scope of a setting: how far a change to it reaches. Rendered as the pill in every card heading.
type Scope = "this browser" | "your account" | "included";

function CardHead({ title, scope }: { title: string; scope: Scope }) {
  return (
    <h2 className="settings-h2">
      {title} <span className="settings-pill">{scope}</span>
    </h2>
  );
}

// ---- The Gateway settings document -----------------------------------------------------------------
//
// The Notifications tab's snooze and time-zone cards each read this one document, so the load/error/busy/
// message plumbing lives here once. Each consumer mounts its own copy, which means one fetch per card - and
// tabs unmount when you switch, so nothing lingers.
//
// runSave wraps a mutation in the immediate-feedback contract every card owes the user (CodingStyle.md):
// the controls disable and the message reads "Saving..." before the call goes out, the caller's returned
// sentence replaces it on success, and a failure reports the Gateway's own error rather than a fabricated
// value. The try/catch is here because this IS the event-handler entry point.
//
// It also refuses to start a second save while one is in flight. Every caller applies its result over the
// settings snapshot its render captured, so two overlapping saves would let the slower one write back a
// value the faster one had already superseded. Disabling the controls is not enough on its own - that
// stops a click, not a keyboard repeat or a queued change event - so the invariant is enforced here, once,
// for every caller. The guard reads a ref because this callback is created once and would otherwise close
// over the first render's `busy`.
function useGatewaySettings() {
  const [settings, setSettings] = useState<GatewaySettings | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [msg, setMsg] = useState("");
  const busyRef = useRef(false);

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

  const runSave = useCallback(async (apply: () => Promise<string>) => {
    if (busyRef.current) return;
    busyRef.current = true;
    setBusy(true);
    setMsg("Saving...");
    try {
      setMsg(await apply());
    } catch (e) {
      setMsg(errText(e));
    } finally {
      busyRef.current = false;
      setBusy(false);
    }
  }, []);

  return { settings, setSettings, error, busy, msg, setMsg, runSave };
}

// A read-only label/value row, used by the AI tab's Transcription line.
function Row({ label, value }: { label: string; value: string }) {
  return (
    <div className="settings-row">
      <div className="settings-row-label">{label}</div>
      <div className="settings-row-value">{value}</div>
    </div>
  );
}

// ---- "Notifications" tab: how a session that needs you reaches you --------------------------------
//
// Snooze, the display time zone, and browser notifications are all per-account preferences about how the
// fleet reaches and reads to you, so they sit together. The time zone moved here from the retired "This
// machine" tab (issue #2022): it is a per-tenant display preference - which local hours the dashboards read
// in - not a fact about the host, so it belongs with the account settings, not on the About page.

function NotificationsTab() {
  return (
    <>
      <SnoozeCard />
      <TimeZoneCard />
      <NotificationsCard />
    </>
  );
}

// The display time zone (issue #2022 - relocated here from the retired "This machine" tab). A per-account
// preference, so it owns its own load of the settings document (one fetch), the same pattern SnoozeCard uses.
function TimeZoneCard() {
  const { settings, setSettings, error, busy, msg, runSave } = useGatewaySettings();

  if (error !== null) {
    return <div className="settings-error">Could not load the time zone setting: {error}</div>;
  }
  if (settings === null) {
    return <p className="settings-loading">Loading...</p>;
  }

  const chooseTimeZone = (tz: string) => {
    if (busy || tz === settings.timeZone) return;
    void runSave(async () => {
      const applied = await setTimeZone(tz);
      setSettings({ ...settings, timeZone: applied });
      return `Time zone set to ${applied}. Your Throttle charts now read this zone.`;
    });
  };

  return (
    <section className="settings-card">
      <CardHead title="Display time zone" scope={ACCOUNT_SCOPE} />
      <p className="settings-hint">
        The zone your private dashboards read local clock hours in - the Your Throttle hourly charts. It
        starts on this Gateway machine&apos;s own zone ({settings.timeZoneMachineDefault}); change it if you
        want the charts shown in a different zone. Read at render time, so it applies on the next refresh.
      </p>
      <div className="settings-field">
        <label htmlFor="settings-timezone">Zone</label>
        <select
          id="settings-timezone"
          className="settings-select"
          value={settings.timeZone}
          disabled={busy}
          onChange={(e) => chooseTimeZone(e.target.value)}
        >
          {timeZoneOptions(settings.timeZone).map((tz) => (
            <option key={tz} value={tz}>
              {tz}
            </option>
          ))}
        </select>
        {settings.timeZone !== settings.timeZoneMachineDefault && (
          <button
            type="button"
            className="settings-btn"
            disabled={busy}
            onClick={() => chooseTimeZone(settings.timeZoneMachineDefault)}
          >
            Use gateway zone
          </button>
        )}
      </div>
      {msg !== "" && <div className="settings-msg">{msg}</div>}
    </section>
  );
}

// The snooze lengths and which of them is the default (Snooze Length mission). A Gateway setting, so it
// owns its own load of the settings document (one fetch for this tab).
//
// The list and its default are ONE form: the radio that marks the default is a row in the same list you
// edit, so there is no separate "default" control that could name a length the list does not offer. Every
// change writes both through setSnoozePresets in a single call, which is what holds that invariant.
//
// Exported ONLY so the browser proof harness
// (packages/client-core/browser-tests/snooze-presets-proof) can mount this exact component against a fake
// Gateway. Nothing else should import it - the card is reached through SettingsView's tabs.
export function SnoozeCard() {
  const { settings, setSettings, error, busy, msg, runSave } = useGatewaySettings();
  // The row being added or edited: its index (null when adding) and the drafted number + unit. Null when
  // the editor is closed.
  const [editing, setEditing] = useState<{ index: number | null; count: string; unit: SnoozeUnit } | null>(null);

  if (error !== null) {
    return <div className="settings-error">Could not load the snooze setting: {error}</div>;
  }
  if (settings === null) {
    return <p className="settings-loading">Loading...</p>;
  }

  const presets = settings.snoozePresets;
  const max = settings.snoozeMaxPresets;
  const isFull = presets.length >= max;

  // Every edit goes through here: it writes the whole list plus the default in one call, so a
  // half-applied change can never leave a default that is not on the menu.
  const save = (next: number[], nextDefault: number, describe: (applied: SnoozePresets) => string) => {
    if (busy) return;
    void runSave(async () => {
      const applied = await setSnoozePresets(next, nextDefault);
      setSettings({
        ...settings,
        snoozePresets: applied.presets,
        snoozeDefaultMinutes: applied.defaultMinutes,
        snoozeMaxPresets: applied.maxPresets,
      });
      setEditing(null);
      return describe(applied);
    });
  };

  const makeDefault = (minutes: number) =>
    save(presets, minutes, (a) => `One click on Snooze now holds a session for ${formatSnoozeLength(a.defaultMinutes)}.`);

  const removeLength = (minutes: number) => {
    const next = presets.filter((m) => m !== minutes);
    // Deleting the default moves it to the shortest remaining length rather than refusing the delete:
    // the list can never be empty (the last row's Remove is disabled) so there is always one to move to.
    const nextDefault = minutes === settings.snoozeDefaultMinutes ? Math.min(...next) : settings.snoozeDefaultMinutes;
    save(next, nextDefault, () =>
      minutes === settings.snoozeDefaultMinutes
        ? `Removed ${formatSnoozeLength(minutes)}. The default is now ${formatSnoozeLength(nextDefault)}.`
        : `Removed ${formatSnoozeLength(minutes)}.`,
    );
  };

  const commitEdit = () => {
    if (editing === null) return;
    const minutes = snoozeMinutesFrom(editing.count, editing.unit);
    if (minutes === null) return;
    const previous = editing.index === null ? null : presets[editing.index];
    const next = previous === null ? [...presets, minutes] : presets.map((m) => (m === previous ? minutes : m));
    // Editing the row that holds the default keeps the default on that row.
    const nextDefault = previous !== null && previous === settings.snoozeDefaultMinutes ? minutes : settings.snoozeDefaultMinutes;
    save(next, nextDefault, () => `Saved ${formatSnoozeLength(minutes)}.`);
  };

  return (
    <section className="settings-card">
      <CardHead title="Snooze" scope={ACCOUNT_SCOPE} />
      <p className="settings-hint">
        How long a snoozed session stays parked before it returns to &quot;needs you&quot; on its own - a
        dead-man&apos;s switch, so a session you snooze can never be lost. These are the lengths every Snooze
        menu offers, the same on every device. Read at snooze time, so a change applies to the next snooze.
      </p>

      <ul className="snooze-list">
        {presets.map((minutes, index) => (
          <li key={minutes} className="snooze-row">
            <label className="snooze-row-pick">
              <input
                type="radio"
                name="snooze-default"
                checked={minutes === settings.snoozeDefaultMinutes}
                disabled={busy}
                onChange={() => makeDefault(minutes)}
              />
              <span className="snooze-row-name">{formatSnoozeLength(minutes)}</span>
            </label>
            <button
              type="button"
              className="settings-btn"
              disabled={busy}
              onClick={() => setEditing({ index, ...snoozeDraftFrom(minutes) })}
            >
              Edit
            </button>
            <button
              type="button"
              className="settings-btn"
              // The menu can never be empty, so the last remaining length cannot be removed.
              disabled={busy || presets.length === 1}
              title={presets.length === 1 ? "You need at least one snooze length." : undefined}
              onClick={() => removeLength(minutes)}
            >
              Remove
            </button>
          </li>
        ))}
      </ul>

      <p className="settings-hint">
        The dot is the default: what one click on Snooze, the phone, and voice all use.{" "}
        {presets.length} of {max} lengths used.
      </p>

      {editing !== null ? (
        <div className="settings-field">
          <label htmlFor="settings-snooze-count">Length</label>
          <input
            id="settings-snooze-count"
            className="settings-input"
            type="number"
            min={1}
            step={1}
            value={editing.count}
            disabled={busy}
            onChange={(e) => setEditing({ ...editing, count: e.target.value })}
            onKeyDown={(e) => {
              if (e.key === "Enter") commitEdit();
              if (e.key === "Escape") setEditing(null);
            }}
          />
          <select
            className="settings-input"
            aria-label="Unit"
            value={editing.unit}
            disabled={busy}
            onChange={(e) => setEditing({ ...editing, unit: e.target.value as SnoozeUnit })}
          >
            <option value="minutes">minutes</option>
            <option value="hours">hours</option>
            <option value="days">days</option>
          </select>
          <button
            type="button"
            className="settings-btn"
            disabled={busy || snoozeMinutesFrom(editing.count, editing.unit) === null}
            onClick={() => commitEdit()}
          >
            Save
          </button>
          <button type="button" className="settings-btn" disabled={busy} onClick={() => setEditing(null)}>
            Cancel
          </button>
        </div>
      ) : (
        <button
          type="button"
          className="settings-btn"
          disabled={busy || isFull}
          title={isFull ? `A snooze menu holds at most ${max} lengths. Remove one first.` : undefined}
          onClick={() => setEditing({ index: null, count: "30", unit: "minutes" })}
        >
          Add a length
        </button>
      )}

      {msg !== "" && <div className="settings-msg">{msg}</div>}
    </section>
  );
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
        // enablePush names every outcome and carries the sentence for it, so the reason a switch-on
        // did not take is always shown. It used to collapse three different outcomes into "this
        // browser blocked notifications" - which is a false statement when the prompt was never
        // shown, and said nothing at all about permission being granted while REGISTERING the
        // device failed.
        const result = await enablePush();
        setPermission(notificationPermission());
        setEnabled(result.state === "granted");
        setMsg(
          result.state === "granted"
            ? "On. This browser will be notified when a session needs you, even in the background."
            : result.message,
        );
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
      <CardHead title="Browser notifications" scope="this browser" />
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
      const s = await getAiProvider();
      setSnap(s);
      // The live catalog + Test spend the shared provider credential and stay denied on the hosted Gateway
      // (issue #2022); the Gateway says so via catalogAvailable. Skip the catalog fetch there so the tab
      // renders clean with the account's saved selections instead of painting a load error.
      if (s.catalogAvailable !== false) await loadModels();
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

  // Gateway-owned (issue #2022): whether the live catalog + Test are available. False on hosted, where model
  // browsing and testing are disabled with a concise explanation rather than offered as controls that fail.
  const catalogAvailable = snap.catalogAvailable !== false;
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
    <section className="settings-card">
      <CardHead title="AI" scope={ACCOUNT_SCOPE} />
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

      {!catalogAvailable && (
        <p className="settings-hint settings-hint-inline">
          Model browsing and testing aren&apos;t available on the hosted Gateway yet. Your saved models are
          shown below; per-account model selection arrives with account-scoped billing.
        </p>
      )}

      <div className="settings-field">
        <label htmlFor="settings-ai-model">Thinking model</label>
        <select
          id="settings-ai-model"
          className="settings-select"
          value={snap.wingmanModel}
          disabled={busy || !catalogAvailable}
          onChange={(e) => void chooseWingman(e.target.value)}
        >
          {ensureIds(snap.wingmanModel, chatModels).map((id) => (
            <option key={id} value={id}>
              {id}
            </option>
          ))}
        </select>
        <div className="settings-actions">
          <button type="button" className="settings-btn" disabled={busy || !catalogAvailable} onClick={() => void runTest()}>
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
          disabled={busy || !catalogAvailable}
          onChange={(e) => void chooseFastWingman(e.target.value)}
        >
          {ensureIds(snap.wingmanFastModel, chatModels).map((id) => (
            <option key={id} value={id}>
              {id}
            </option>
          ))}
        </select>
        <div className="settings-actions">
          <button type="button" className="settings-btn" disabled={busy || !catalogAvailable} onClick={() => void runFastTest()}>
            Test
          </button>
          <span className="settings-inline-msg">
            {fastTestMsg || "Used for spoken turn summaries, menus, and choice mapping."}
          </span>
        </div>
      </div>

      <div className="settings-field">
        <label htmlFor="settings-ai-ttsmodel">Speech model</label>
        <select
          id="settings-ai-ttsmodel"
          className="settings-select"
          value={snap.ttsModel}
          disabled={busy || !catalogAvailable}
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

// ---- "Car Mode" tab: the phone's hands-free fleet control in one place (owner's ask) - its model, its
// sign-off phrase, and a live phrase tester. The phrase is a Gateway setting (setCarModeEndPhrase) so what
// is set here reaches the phone where Car Mode runs.
function CarModeTab() {
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
        [{ atMs: Math.round(performance.now() - startAtRef.current), transcript: heard, matched: det.ended, transcribeMs }, ...p].slice(0, 12),
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
          Fired {result.latencyMs} ms after you stopped speaking. Command heard: &quot;{result.command || "(none)"}&quot;
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

// The IANA time-zone options for the picker: the browser's full supported list (Intl.supportedValuesOf),
// with the current value guaranteed present even if the browser cannot enumerate zones.
function timeZoneOptions(current: string): string[] {
  let list: string[] = [];
  try {
    const supported = (Intl as unknown as { supportedValuesOf?: (key: string) => string[] }).supportedValuesOf;
    if (typeof supported === "function") list = supported("timeZone");
  } catch {
    /* Intl.supportedValuesOf unavailable - fall through to just the current value */
  }
  if (current && list.indexOf(current) < 0) list = [current, ...list];
  return list.length > 0 ? list : [current || "UTC"];
}
