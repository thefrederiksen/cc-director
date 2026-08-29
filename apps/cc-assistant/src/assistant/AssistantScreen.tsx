import { useCallback, useEffect, useRef, useState } from "react";
import { matchWakeWord, describeWakeWordWeakness } from "../wakeWord/wakeWordMatcher";
import { chirp, createListener, cue, localRecognitionReady, startThinkingTicks, type Listener } from "./speech";
import { DEFAULT_VOICE, VOICES, speakStreamed, stopVoice, unlockVoice, type VoiceName } from "./voice";
import { DebugPanel } from "./DebugPanel";
import { VoiceRing, embedClip, enrolVoice, identifyVoice, loadSpeakerModel, watchSpeakerModel } from "./speakerId";
import { captureUtterance, transcribeClip, type Captured } from "./cloudEars";
import { watchMicLevel, type MicLevel } from "./micLevel";
import { judgeEcho, similarity, ECHO_SIMILARITY } from "./echoGuard";
import { clockFace as clockFaceOf } from "../skills/timerParse";
import { isSilenceCommand, callIt } from "../skills/timerLogic";
import { sayWeather, sayNoLocation, sayPlaceNotFound } from "../skills/weather";
import { useTimers } from "../skills/useTimers";

// The assistant. Say your word, then say the thing.
//
// After the reply it stays awake for a few seconds, so a second question does not need the word
// again. That one behaviour is most of the difference between a conversation and operating a machine.

type State = "off" | "asleep" | "listening" | "thinking" | "speaking";

interface Turn {
  readonly id: number;
  readonly you: string;
  readonly it: string;
  readonly ms: number;
}

// How long it waits for the command after the wake word is said on its own. This is the ONLY time it
// sits listening. After it answers it goes straight back to sleep.
const FOLLOW_UP_MS = 5000;
const WAKE_WORD_KEY = "cc-assistant.wakeWord";
const HOME_KEY = "cc-assistant.home";
const VOICE_KEY = "cc-assistant.voice";
const OWNER_KEY = "cc-assistant.owner";
const MODE_KEY = "cc-assistant.mode";
const EARS_KEY = "cc-assistant.ears";

// How the command is heard. "browser": the platform recogniser's transcript, as before. "cloud":
// Wilson records the command itself and Whisper on Groq writes it down (the path the kitchen box
// will use, since a Pi has no platform recogniser). The wake word is heard by the recogniser either
// way, for now.
type Ears = "browser" | "cloud";
const WORKLET_URL = `${import.meta.env.BASE_URL}pcm-worklet.js`;
/** How much of the last few seconds is the command, for identifying the voice. */
const IDENTIFY_SECONDS = 3.5;

type Mode = "production" | "debug";

// Which screen. ?debug=1 or ?debug=0 wins, then what was chosen last time, then: a PC gets the debug
// screen and a phone gets the circle, because that is where each is looked at.
function initialMode(): Mode {
  try {
    const wanted = new URLSearchParams(window.location.search).get("debug");
    if (wanted === "1") {
      return "debug";
    }
    if (wanted === "0") {
      return "production";
    }
    const saved = window.localStorage.getItem(MODE_KEY);
    if (saved === "debug" || saved === "production") {
      return saved;
    }
    return /Android|iPhone|iPad/i.test(navigator.userAgent) ? "production" : "debug";
  } catch {
    return "debug";
  }
}

export function AssistantScreen() {
  const [wakeWord, setWakeWord] = useState(() => {
    try {
      return window.localStorage.getItem(WAKE_WORD_KEY) ?? "Wilson";
    } catch {
      return "Wilson";
    }
  });
  const [state, setState] = useState<State>("off");
  const [live, setLive] = useState("");
  const [turns, setTurns] = useState<Turn[]>([]);
  const [problem, setProblem] = useState<string | null>(null);
  const [onDevice, setOnDevice] = useState<boolean | null>(null);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [home, setHome] = useState(() => {
    try {
      return window.localStorage.getItem(HOME_KEY) ?? "";
    } catch {
      return "";
    }
  });
  const [voice, setVoice] = useState<VoiceName>(() => {
    try {
      const saved = window.localStorage.getItem(VOICE_KEY);
      return (VOICES as readonly string[]).includes(saved ?? "") ? (saved as VoiceName) : DEFAULT_VOICE;
    } catch {
      return DEFAULT_VOICE;
    }
  });
  const voiceRef = useRef<VoiceName>(voice);
  voiceRef.current = voice;
  // How long the last reply took to make a sound. The number the voice is judged by.
  const [voiceInfo, setVoiceInfo] = useState<{ firstSoundMs: number; seconds: number } | null>(null);
  const [mode, setMode] = useState<Mode>(initialMode);
  // Who this device belongs to: who Wilson assumes is talking until the voice says otherwise.
  const [owner, setOwner] = useState(() => {
    try {
      return window.localStorage.getItem(OWNER_KEY) ?? "";
    } catch {
      return "";
    }
  });
  const ownerRef = useRef(owner);
  ownerRef.current = owner;
  const [ears, setEars] = useState<Ears>(() => {
    try {
      return window.localStorage.getItem(EARS_KEY) === "browser" ? "browser" : "cloud";
    } catch {
      return "cloud";
    }
  });
  const earsRef = useRef<Ears>(ears);
  earsRef.current = ears;
  // What the cloud ears last did: clip length, how it ended, Whisper's time and words.
  const [earsInfo, setEarsInfo] = useState("");
  const captureRef = useRef<{ stop(): void } | null>(null);
  const [speakerStatus, setSpeakerStatus] = useState("not loaded");
  const [identified, setIdentified] = useState("nobody yet");
  const identifiedRef = useRef<{ name: string | null; confidence: number } | null>(null);
  const ringRef = useRef<VoiceRing | null>(null);
  // Bumped after every turn so the debug panel re-reads the log.
  const [logVersion, setLogVersion] = useState(0);
  // The evidence that it is awake. Without these, a silent room and a dead microphone look identical.
  const [level, setLevel] = useState(0);
  const [micInfo, setMicInfo] = useState<{ label: string; echoCancellation: boolean } | null>(null);
  const [sessionRunning, setSessionRunning] = useState(false);
  const [resultCount, setResultCount] = useState(0);
  const [lastHeardAt, setLastHeardAt] = useState<number | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [engine, setEngine] = useState<string>("");
  const [suppressed, setSuppressed] = useState<string | null>(null);
  const [, setTick] = useState(0);

  const micRef = useRef<MicLevel | null>(null);
  const listenerRef = useRef<Listener | null>(null);
  const stateRef = useRef<State>("off");
  const wakeWordRef = useRef(wakeWord);
  const followUpUntilRef = useRef(0);
  const turnIdRef = useRef(0);
  const historyRef = useRef<Array<{ role: "user" | "assistant"; content: string }>>([]);
  const busyRef = useRef(false);
  // What it has said out loud, and when it stopped. Without these it answers its own voice, which on
  // 28 August turned one question into an endless conversation with itself.
  const speakingRef = useRef(false);
  const lastSpokeEndedAtRef = useRef<number | null>(null);
  const recentlySpokenRef = useRef<string[]>([]);

  wakeWordRef.current = wakeWord;
  const setStateBoth = useCallback((next: State) => {
    stateRef.current = next;
    setState(next);
  }, []);

  useEffect(() => {
    try {
      window.localStorage.setItem(HOME_KEY, home);
    } catch {
      // Not remembering it is a nuisance, not a failure.
    }
  }, [home]);

  useEffect(() => {
    try {
      window.localStorage.setItem(WAKE_WORD_KEY, wakeWord);
    } catch {
      // Not being able to remember the word is a nuisance, not a failure.
    }
  }, [wakeWord]);

  useEffect(() => {
    try {
      window.localStorage.setItem(VOICE_KEY, voice);
    } catch {
      // Same.
    }
  }, [voice]);

  useEffect(() => {
    try {
      window.localStorage.setItem(OWNER_KEY, owner);
      window.localStorage.setItem(MODE_KEY, mode);
      window.localStorage.setItem(EARS_KEY, ears);
    } catch {
      // Same.
    }
  }, [owner, mode, ears]);

  useEffect(() => watchSpeakerModel((s) => setSpeakerStatus(s.detail)), []);

  /** The name a turn is attributed to: the voice if it was recognised, else the device's owner. */
  const speakerName = useCallback((): string | null => {
    const heard = identifiedRef.current;
    if (heard && heard.name !== null) {
      return heard.name;
    }
    return ownerRef.current.trim().length > 0 ? ownerRef.current.trim() : null;
  }, []);

  /** The page's half of the turn log: what was actually said, and how fast. Fire and forget. */
  const reportSpoken = useCallback((turnId: string | null, said: string, by: "model" | "device", spoken: { firstSoundMs: number; seconds: number } | null, extra: Record<string, unknown> = {}) => {
    const heard = identifiedRef.current;
    void fetch(`${import.meta.env.BASE_URL}api/turn`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        kind: "spoken",
        id: turnId,
        said,
        by,
        firstSoundMs: spoken ? spoken.firstSoundMs : null,
        speechSeconds: spoken ? Number(spoken.seconds.toFixed(2)) : null,
        identified: heard ? heard.name ?? "unknown" : "not attempted",
        confidence: heard ? Number(heard.confidence.toFixed(3)) : null,
        ...extra,
      }),
    })
      .then(() => setLogVersion((v) => v + 1))
      .catch(() => undefined);
  }, []);

  // Every spoken reply goes through here. The voice streams from the server and starts within a
  // quarter of a second; a short rising note marks the instant it does. When it cannot be reached
  // at all, the failure is shown on screen and sounded, because a silent assistant and a broken one
  // look the same.
  const speak = useCallback(async (text: string, onStart?: () => void): Promise<{ firstSoundMs: number; seconds: number } | null> => {
    try {
      const spoken = await speakStreamed(text, voiceRef.current, () => {
        cue("speak");
        onStart?.();
      });
      setVoiceInfo(spoken);
      return spoken;
    } catch (error) {
      cue("fail");
      setProblem(`The voice failed: ${error instanceof Error ? error.message : String(error)}`);
      return null;
    }
  }, []);

  useEffect(() => {
    void localRecognitionReady("en-US").then(setOnDevice);
  }, []);

  // The one place that ever puts it back to sleep. Every path that wakes it only has to push the
  // deadline out; this notices when the deadline has passed. Nothing else schedules this transition.
  useEffect(() => {
    const timer = window.setInterval(() => {
      setTick((t) => t + 1);
      if (stateRef.current === "listening" && Date.now() >= followUpUntilRef.current) {
        chirp("sleep");
        setStateBoth("asleep");
        setLive("");
      }
    }, 500);
    return () => window.clearInterval(timer);
  }, [setStateBoth]);

  const say = useCallback(
    async (sentence: string) => {
      recentlySpokenRef.current = [...recentlySpokenRef.current, sentence].slice(-4);
      speakingRef.current = true;
      setStateBoth("speaking");
      const spoken = await speak(sentence);
      speakingRef.current = false;
      lastSpokeEndedAtRef.current = Date.now();
      followUpUntilRef.current = 0;
      setStateBoth("asleep");
      setLive("");
      return spoken;
    },
    [setStateBoth, speak],
  );

  const timers = useTimers((finished) => {
    const sentence = `Your ${callIt(finished)} is up.`;
    turnIdRef.current += 1;
    setTurns((previous) =>
      [{ id: turnIdRef.current, you: "(timer)", it: sentence, ms: 0 }, ...previous].slice(0, 30),
    );
    void say(sentence);
  });
  const timersRef = useRef(timers);
  timersRef.current = timers;

  // THERE IS NO LOCAL SHORTCUT FOR TIMERS ANY MORE.
  //
  // There was one, to save the four hundred milliseconds of asking the model. It matched any sentence
  // containing a duration and started an unnamed timer, stepping aside only for the exact words
  // "called", "named" or "for the". So "set a timer called barbecue for three minutes" worked, and the
  // same sentence with the word "called" misheard did not - it silently became an unnamed timer.
  //
  // The model handles every one of these correctly, including asking how long when no duration was
  // given. A shortcut that is sometimes wrong is worse than a delay that is always right.

  const homeRef = useRef(home);
  homeRef.current = home;

  /**
   * Fetch the weather and say it.
   *
   * A named place wins over the home town, so asking about London while standing in the kitchen gives
   * London. With neither, it says it does not know rather than guessing at a city.
   */
  const answerWeather = useCallback(async (place: string | null): Promise<string> => {
    const wanted = place ?? homeRef.current;
    if (wanted.trim().length === 0) {
      return sayNoLocation();
    }
    try {
      // `named` tells the service whether a place was actually said. Without one, it may know
      // better than the saved home town where this person is right now.
      const response = await fetch(`${import.meta.env.BASE_URL}api/weather`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ place: wanted, named: place !== null, home: homeRef.current, speaker: speakerName() }),
      });
      const body = (await response.json()) as {
        reading?: Parameters<typeof sayWeather>[0];
        notFound?: boolean;
        noLocation?: boolean;
        error?: string;
      };
      if (body.notFound) {
        return sayPlaceNotFound(wanted);
      }
      if (body.noLocation) {
        return sayNoLocation();
      }
      if (!response.ok || !body.reading) {
        return body.error ?? "I could not get the weather.";
      }
      return sayWeather(body.reading);
    } catch {
      return "I could not reach the weather service.";
    }
  }, [speakerName]);

  /** Carry out one tool the model asked for, and return the sentence describing what happened. */
  const runAction = useCallback((action: { name: string; args: Record<string, unknown> }): string => {
    const t = timersRef.current;
    const name = typeof action.args.name === "string" ? action.args.name : null;
    switch (action.name) {
      case "start_timer": {
        const seconds = Number(action.args.seconds);
        if (!Number.isFinite(seconds) || seconds <= 0 || seconds > 24 * 3600) {
          return "That is not a length of time I can set.";
        }
        // Nobody asks for a one second timer out loud. A very short one means the length was misheard
        // or invented, and starting it silently is how you get a timer you never asked for.
        if (seconds < 5) {
          return "I did not catch how long. How long should it be?";
        }
        return t.start(Math.round(seconds), name);
      }
      case "stop_timer":
        return t.stopNamed(name);
      case "stop_all_timers":
        return t.stopAll();
      case "list_timers":
        return t.list();
      case "get_weather":
        // Handled by the caller, which can wait for the network.
        return "";
      default:
        return "";
    }
  }, []);

  const ask = useCallback(async (question: string) => {
    if (busyRef.current || question.trim().length === 0) {
      return;
    }
    busyRef.current = true;
    setStateBoth("thinking");
    const startedAt = Date.now();
    // Audible thinking, from now until the reply is in hand. A wait that can be heard is a wait;
    // a silent one is a fault.
    const stopTicks = startThinkingTicks();
    let turnId: string | null = null;

    try {
      const response = await fetch(`${import.meta.env.BASE_URL}api/talk`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ text: question, history: historyRef.current, timers: timersRef.current.snapshot(), speaker: speakerName() }),
      });
      const body = (await response.json()) as {
        reply?: string;
        actions?: Array<{ name: string; args: Record<string, unknown> }>;
        error?: string;
        turnId?: string;
      };
      turnId = body.turnId ?? null;
      if (!response.ok) {
        throw new Error(body.error ?? `The assistant failed (${response.status}).`);
      }

      // The device did the work, so the device says what happened. The model is never asked to
      // narrate its own tool calls.
      if (body.actions && body.actions.length > 0) {
        const said: string[] = [];
        for (const action of body.actions) {
          if (action.name === "get_weather") {
            const place = typeof action.args.place === "string" ? action.args.place : null;
            said.push(await answerWeather(place));
            continue;
          }
          const line = runAction(action);
          if (line.length > 0) {
            said.push(line);
          }
        }
        const sentence = said.length > 0 ? said.join(" ") : "I could not do that.";
        turnIdRef.current += 1;
        setTurns((previous) =>
          [{ id: turnIdRef.current, you: question, it: sentence, ms: Date.now() - startedAt }, ...previous].slice(0, 30),
        );
        busyRef.current = false;
        stopTicks();
        const spoken = await say(sentence);
        reportSpoken(turnId, sentence, "device", spoken);
        return;
      }

      if (!body.reply) {
        throw new Error(body.error ?? "The assistant had nothing to say.");
      }

      historyRef.current = [
        ...historyRef.current,
        { role: "user" as const, content: question },
        { role: "assistant" as const, content: body.reply },
      ].slice(-8);

      turnIdRef.current += 1;
      setTurns((previous) =>
        [{ id: turnIdRef.current, you: question, it: body.reply!, ms: Date.now() - startedAt }, ...previous].slice(0, 30),
      );

      busyRef.current = false;
      stopTicks();
      recentlySpokenRef.current = [...recentlySpokenRef.current, body.reply].slice(-4);
      speakingRef.current = true;
      const spoken = await speak(body.reply, () => setStateBoth("speaking"));
      speakingRef.current = false;
      lastSpokeEndedAtRef.current = Date.now();
      reportSpoken(turnId, body.reply, "model", spoken);
      // STRAIGHT BACK TO SLEEP. It does not keep listening after it has answered. Say the wake word
      // again for the next thing.
      followUpUntilRef.current = 0;
      chirp("sleep");
      setStateBoth("asleep");
      setLive("");
    } catch (error) {
      busyRef.current = false;
      stopTicks();
      cue("fail");
      const message = error instanceof Error ? error.message : String(error);
      setProblem(message);
      reportSpoken(turnId, "", "device", null, { error: message });
      speakingRef.current = true;
      await speak("Something went wrong.");
      speakingRef.current = false;
      lastSpokeEndedAtRef.current = Date.now();
      setStateBoth("asleep");
    }
  }, [answerWeather, reportSpoken, runAction, setStateBoth, speak, speakerName]);

  /**
   * Who just spoke, from the last few seconds of audio, before the command goes to the brain.
   * Quick when the model is loaded (tens of milliseconds), and skipped honestly when it is not:
   * "unknown" costs a little personalisation, a wrong name costs trust.
   */
  const identifyThenAsk = useCallback(
    async (command: string, audio: Float32Array | null = null) => {
      identifiedRef.current = null;
      const ring = ringRef.current;
      const clip = audio ?? (ring !== null && ring.running ? ring.recent(IDENTIFY_SECONDS) : null);
      if (clip !== null) {
        try {
          const result = await identifyVoice(await embedClip(clip));
          identifiedRef.current = { name: result.name, confidence: result.confidence };
          setIdentified(
            result.name !== null
              ? `${result.name} (${result.confidence.toFixed(2)})`
              : `unknown: ${result.reason ?? "no match"}`,
          );
        } catch (error) {
          setIdentified(`not identified: ${error instanceof Error ? error.message : String(error)}`);
        }
      } else {
        setIdentified("not identified: no audio captured");
      }
      await ask(command);
    },
    [ask],
  );

  /**
   * Cloud ears: record what follows the wake word until the person goes quiet, have Whisper write
   * it down, take the wake word off the front, and ask. `afterWake` means the clip may hold only the
   * wake word; then Wilson waits for one more utterance before giving up.
   */
  const hearCommand = useCallback(
    async (afterWake: boolean) => {
      const ring = ringRef.current;
      if (ring === null || !ring.running || captureRef.current !== null) {
        return;
      }
      const capture = captureUtterance(ring);
      captureRef.current = capture;
      let captured: Captured;
      try {
        captured = await capture.done;
      } finally {
        captureRef.current = null;
      }
      if (stateRef.current !== "listening") {
        // Stopped, or it fell asleep while we recorded. Nothing to do with the clip.
        return;
      }
      if (!captured.heardSpeech || captured.endedBy === "stopped") {
        setEarsInfo(`cloud: nothing heard (${captured.endedBy})`);
        chirp("sleep");
        setStateBoth("asleep");
        setLive("");
        return;
      }
      setStateBoth("thinking");
      let heard;
      try {
        heard = await transcribeClip(captured.samples, [wakeWordRef.current, ownerRef.current]);
      } catch (error) {
        cue("fail");
        setProblem(`Hearing failed: ${error instanceof Error ? error.message : String(error)}`);
        setStateBoth("asleep");
        setLive("");
        return;
      }
      setEarsInfo(`cloud: ${captured.seconds.toFixed(1)} s clip, ended by ${captured.endedBy}, Whisper ${heard.elapsedMs} ms: "${heard.text}"`);
      setLive(heard.text);
      setLastHeardAt(Date.now());
      const match = matchWakeWord(heard.text, wakeWordRef.current);
      const command = (match !== null ? match.command : heard.text).trim();
      if (command.length === 0) {
        if (afterWake) {
          // Just the name. Wait for the actual request, once.
          setStateBoth("listening");
          followUpUntilRef.current = Date.now() + FOLLOW_UP_MS;
          void hearCommand(false);
          return;
        }
        chirp("sleep");
        setStateBoth("asleep");
        setLive("");
        return;
      }
      // Whisper's own words, and the very clip it heard them in, for identifying the voice.
      const guard = judgeEcho(command, {
        speaking: speakingRef.current,
        lastSpokeEndedAt: lastSpokeEndedAtRef.current,
        recentlySpoken: recentlySpokenRef.current,
        now: Date.now(),
      });
      if (guard.isEcho) {
        setSuppressed(`Ignored "${command}" - ${guard.reason}`);
        setStateBoth("asleep");
        return;
      }
      setSuppressed(null);
      await identifyThenAsk(command, captured.samples);
    },
    [identifyThenAsk, setStateBoth],
  );

  const onHeard = useCallback(
    (text: string, isFinal: boolean) => {
      setLive(text);
      setResultCount((c) => c + 1);
      setLastHeardAt(Date.now());

      // Shouting the wake word over a beeping alarm is absurd, so while something is ringing a bare
      // "stop" or "shut up" silences it. Instant, local, ahead of everything else.
      if (timersRef.current.anyRinging && isSilenceCommand(text)) {
        if (timersRef.current.silence()) {
          setSuppressed(null);
          setLive("");
          return;
        }
      }
      if (!isFinal) {
        // Wake on an interim result so the sound lands while the sentence is still being said.
        if (stateRef.current === "asleep" && matchWakeWord(text, wakeWordRef.current) !== null) {
          chirp("wake");
          setStateBoth("listening");
          followUpUntilRef.current = Date.now() + FOLLOW_UP_MS;
          // With cloud ears, the command is recorded from here and written down by Whisper; the
          // recogniser's own transcript of it is not used.
          if (earsRef.current === "cloud" && ringRef.current !== null && ringRef.current.running) {
            void hearCommand(true);
          }
        }
        // Interrupting while it talks: saying the word again cuts it off.
        if (stateRef.current === "speaking" && matchWakeWord(text, wakeWordRef.current) !== null) {
          const own = recentlySpokenRef.current.some((said) => similarity(text, said) >= ECHO_SIMILARITY);
          if (own) {
            return;
          }
          speakingRef.current = false;
          lastSpokeEndedAtRef.current = Date.now();
          stopVoice();
          chirp("wake");
          setStateBoth("listening");
          followUpUntilRef.current = Date.now() + FOLLOW_UP_MS;
        }
        return;
      }

      // Cloud ears: final transcripts from the recogniser are not commands. Everything after the wake
      // is heard by Wilson's own microphone and Whisper (hearCommand). Only the wake word above and
      // the alarm silencer further up use the recogniser's words.
      if (earsRef.current === "cloud" && ringRef.current !== null && ringRef.current.running) {
        return;
      }

      const guard = () =>
        judgeEcho(text, {
          speaking: speakingRef.current,
          lastSpokeEndedAt: lastSpokeEndedAtRef.current,
          recentlySpoken: recentlySpokenRef.current,
          now: Date.now(),
        });

      const match = matchWakeWord(text, wakeWordRef.current);
      if (match !== null) {
        if (match.command.length > 0) {
          const decision = guard();
          if (decision.isEcho) {
            setSuppressed(`Ignored "${text}" - ${decision.reason}`);
            return;
          }
          void identifyThenAsk(match.command);
        } else {
          setStateBoth("listening");
          followUpUntilRef.current = Date.now() + FOLLOW_UP_MS;
        }
        return;
      }

      // The only thing that reaches the brain without the wake word is the sentence that follows the
      // wake word being said on its own. Nothing else, ever - that door is what its own voice walked
      // through, and it is now shut.
      if (stateRef.current === "listening" && Date.now() < followUpUntilRef.current) {
        const decision = guard();
        if (decision.isEcho) {
          setSuppressed(`Ignored "${text}" - ${decision.reason}`);
          return;
        }
        setSuppressed(null);
        void identifyThenAsk(text);
      }
    },
    [hearCommand, identifyThenAsk, setStateBoth],
  );

  const start = useCallback(async () => {
    setProblem(null);
    setNotice(null);
    setResultCount(0);
    setLastHeardAt(null);

    // From the press itself, while the browser still counts this as a user gesture: audio may only
    // start from one, and the voice's AudioContext has to be made and resumed here or it stays mute.
    await unlockVoice();

    // Open the microphone ourselves first. This is what makes "is it listening" answerable: the meter
    // moves or it does not. It also surfaces a refused permission here, plainly, instead of leaving
    // the recogniser to fail quietly somewhere out of sight.
    try {
      const mic = await watchMicLevel(setLevel);
      micRef.current = mic;
      setMicInfo({ label: mic.deviceLabel, echoCancellation: mic.echoCancellation });
    } catch (error) {
      setProblem(
        `The microphone could not be opened: ${error instanceof Error ? error.message : String(error)}`,
      );
      return;
    }

    // The browser's own recogniser. It produced nothing under automation, which sent me down a long
    // wrong turn on 28 August; in an ordinary browser it works. Only ask for on-device processing
    // when the device actually has the model, because asking when it does not yields a recogniser
    // that reports nothing at all and looks exactly like a dead microphone.
    const canRunLocally = await localRecognitionReady("en-US");
    setOnDevice(canRunLocally);
    setEngine(canRunLocally ? "on-device recognition" : "the browser's recogniser");

    try {
      const listener = createListener("en-US", canRunLocally, {
        onHeard: (heard) => onHeard(heard.text, heard.isFinal),
        onNotice: setNotice,
        onSession: setSessionRunning,
        onFatal: (message) => {
          setProblem(message);
          setStateBoth("off");
          listenerRef.current = null;
        },
      });
      listenerRef.current = listener;
      listener.start();
      setStateBoth("asleep");

      // The voice ring runs beside the recogniser so there is audio to identify a speaker from. It
      // is not allowed to stop the assistant: a ring that will not open is reported, and Wilson
      // still listens and answers, just without knowing who.
      try {
        const ring = new VoiceRing();
        await ring.start(WORKLET_URL);
        ringRef.current = ring;
        void loadSpeakerModel();
      } catch (error) {
        setNotice(`No voice identification: ${error instanceof Error ? error.message : String(error)}`);
      }
    } catch (error) {
      setProblem(error instanceof Error ? error.message : String(error));
      micRef.current?.stop();
      micRef.current = null;
    }
  }, [onHeard, setStateBoth]);

  const stop = useCallback(() => {
    listenerRef.current?.stop();
    listenerRef.current = null;
    micRef.current?.stop();
    micRef.current = null;
    captureRef.current?.stop();
    void ringRef.current?.stop();
    ringRef.current = null;
    stopVoice();
    setStateBoth("off");
    setLive("");
    setLevel(0);
    setSessionRunning(false);
  }, [setStateBoth]);

  useEffect(() => () => {
    listenerRef.current?.stop();
    micRef.current?.stop();
    void ringRef.current?.stop();
  }, []);

  /**
   * Enrol a voice: wait while the person reads the line, take the seconds that just went by, embed
   * them, and store the result for that name. Needs the assistant started, so the ring is open.
   */
  const enrol = useCallback(async (name: string, line: string): Promise<string> => {
    const ring = ringRef.current;
    if (ring === null || !ring.running) {
      throw new Error("press Start first, so the microphone is open");
    }
    await loadSpeakerModel();
    await new Promise((resolve) => window.setTimeout(resolve, 4500));
    const clip = ring.recent(4);
    if (clip === null) {
      throw new Error("nothing but silence was heard; read the line out loud after pressing");
    }
    const samples = await enrolVoice(name, await embedClip(clip), line);
    return `Enrolled for ${name}: ${samples} sample${samples === 1 ? "" : "s"} now.`;
  }, []);

  const weakness = describeWakeWordWeakness(wakeWord);

  return (
    <div className="assistant" data-state={problem !== null && state === "asleep" ? "error" : state} data-mode={mode}>
      {/* In the kitchen the circle is the whole interface: tap it to start, tap again to stop. */}
      <div
        className="eyeWrap"
        role={mode === "production" ? "button" : undefined}
        tabIndex={mode === "production" ? 0 : undefined}
        aria-label={mode === "production" ? (state === "off" ? "Start Wilson" : "Stop Wilson") : undefined}
        onClick={mode === "production" ? () => (state === "off" ? void start() : stop()) : undefined}
        onKeyDown={mode === "production" ? (e) => (e.key === "Enter" || e.key === " " ? (state === "off" ? void start() : stop()) : undefined) : undefined}
      >
        <div className="eye" />
      </div>

      <p className="stateLine">
        {state === "off" ? (mode === "production" ? "Tap to start" : "Not listening") : null}
        {state === "asleep" ? `Say "${wakeWord}"` : null}
        {state === "listening" ? "Listening" : null}
        {state === "thinking" ? "Thinking" : null}
        {state === "speaking" ? "Talking" : null}
      </p>

      {mode === "production" ? (
        <p className="heardLine">{state === "listening" || state === "thinking" ? live : ""}</p>
      ) : (
        <p className="liveLine">{state === "off" ? "" : live}</p>
      )}

      {mode === "production" ? (
        <button className="cornerButton" onClick={() => setMode("debug")}>
          debug
        </button>
      ) : null}

      {state !== "off" && mode === "debug" ? (
        <div className="mic">
          <div className="meter" aria-label="Microphone level">
            <div className="meterFill" style={{ width: `${Math.min(100, Math.round(level * 220))}%` }} />
          </div>
          <p className="micLine">
            {micInfo === null ? "" : micInfo.label}
            {" · "}
            {sessionRunning ? `listening with ${engine}` : "starting the speech model"}
            {" · "}
            {resultCount} results
            {lastHeardAt === null ? " · nothing heard yet" : ` · last heard ${Math.round((Date.now() - lastHeardAt) / 1000)}s ago`}
            {voiceInfo !== null ? ` · voice ${voice}, first sound ${voiceInfo.firstSoundMs} ms` : ` · voice ${voice}`}
            {` · ears ${ears}`}
          </p>
          {earsInfo.length > 0 ? <p className="micNotice">{earsInfo}</p> : null}
          {notice !== null ? <p className="micNotice">{notice}</p> : null}
          {suppressed !== null ? <p className="micNotice">{suppressed}</p> : null}
        </div>
      ) : null}

      {mode === "debug" ? (
        <div className="assistantButtons">
          {state === "off" ? (
            <button className="big go" onClick={() => void start()}>Start</button>
          ) : (
            <button className="big stop" onClick={stop}>Stop</button>
          )}
          <button onClick={() => setSettingsOpen((o) => !o)}>{settingsOpen ? "Hide" : "Settings"}</button>
          <button onClick={() => setMode("production")}>Kitchen screen</button>
        </div>
      ) : null}

      {problem !== null ? <p className="verdict bad">{problem}</p> : null}

      {settingsOpen && mode === "debug" ? (
        <div className="settings">
          <label>
            Wake word
            <input value={wakeWord} onChange={(e) => setWakeWord(e.target.value)} spellCheck={false} />
          </label>
          {weakness !== null ? <p className="advice">{weakness}</p> : null}
          <label>
            Home town, for the weather
            <input
              value={home}
              onChange={(e) => setHome(e.target.value)}
              placeholder="Toronto"
              spellCheck={false}
            />
          </label>
          <label>
            Voice
            <select value={voice} onChange={(e) => setVoice(e.target.value as VoiceName)}>
              {VOICES.map((v) => (
                <option key={v} value={v}>
                  {v}
                </option>
              ))}
            </select>
          </label>
          <label>
            Ears
            <select value={ears} onChange={(e) => setEars(e.target.value as Ears)}>
              <option value="cloud">cloud: Wilson records, Whisper on Groq writes it down (the kitchen box path)</option>
              <option value="browser">browser: the platform recogniser's transcript</option>
            </select>
          </label>
          {earsInfo.length > 0 ? <p className="status">{earsInfo}</p> : null}
          <p className="status">
            Wilson speaks with Orpheus on Groq, voice {voice}, streamed as it is made.
            {voiceInfo !== null ? ` Last reply: first sound after ${voiceInfo.firstSoundMs} ms, ${voiceInfo.seconds.toFixed(1)} s of speech.` : null}
          </p>
          <p className="status">
            Listening runs on this device with {engine || "the speech model"}, and the audio never leaves it.
            {onDevice === false ? " The browser's own recogniser is not usable here." : null}
          </p>
        </div>
      ) : null}

      {timers.timers.length > 0 ? (
        <ul className="timers">
          {timers.timers.map((t) => (
            <li key={t.id} className={t.ringing ? "ringing" : undefined}>
              <span className="face">{t.ringing ? "0:00" : t.face}</span>
              <span className="of">
                {t.name !== null ? <b className="tname">{t.name}</b> : null}
                {t.name !== null ? " " : null}
                of {clockFaceOf(t.totalSeconds)}
              </span>
              {t.ringing ? <span className="up">TIME IS UP</span> : null}
            </li>
          ))}
          <li className="timerWarning">
            Timers only run while this app is on screen. Leave the page and they stop.
          </li>
        </ul>
      ) : null}

      {turns.length > 0 && mode === "debug" ? (
        <ul className="turns">
          {turns.map((t) => (
            <li key={t.id}>
              <span className="you">{t.you}</span>
              <span className="it">{t.it}</span>
              <span className="ms">{t.ms} ms</span>
            </li>
          ))}
        </ul>
      ) : null}

      {mode === "debug" ? (
        <DebugPanel
          owner={owner}
          onOwnerChange={setOwner}
          enrol={enrol}
          speakerStatus={speakerStatus}
          lastIdentified={identified}
          version={logVersion}
        />
      ) : null}
    </div>
  );
}
