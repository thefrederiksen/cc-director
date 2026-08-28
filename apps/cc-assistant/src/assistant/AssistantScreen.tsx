import { useCallback, useEffect, useRef, useState } from "react";
import { matchWakeWord, describeWakeWordWeakness } from "../wakeWord/wakeWordMatcher";
import { chirp, createListener, localRecognitionReady, speak, stopSpeaking, type Listener } from "./speech";
import { watchMicLevel, type MicLevel } from "./micLevel";
import { judgeEcho, similarity, ECHO_SIMILARITY } from "./echoGuard";
import { clockFace as clockFaceOf } from "../skills/timerParse";
import { isSilenceCommand, callIt } from "../skills/timerLogic";
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
      window.localStorage.setItem(WAKE_WORD_KEY, wakeWord);
    } catch {
      // Not being able to remember the word is a nuisance, not a failure.
    }
  }, [wakeWord]);

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
      await speak(sentence);
      speakingRef.current = false;
      lastSpokeEndedAtRef.current = Date.now();
      followUpUntilRef.current = 0;
      setStateBoth("asleep");
      setLive("");
    },
    [setStateBoth],
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

    try {
      const response = await fetch(`${import.meta.env.BASE_URL}api/talk`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ text: question, history: historyRef.current, timers: timersRef.current.snapshot() }),
      });
      const body = (await response.json()) as {
        reply?: string;
        actions?: Array<{ name: string; args: Record<string, unknown> }>;
        error?: string;
      };
      if (!response.ok) {
        throw new Error(body.error ?? `The assistant failed (${response.status}).`);
      }

      // The device did the work, so the device says what happened. The model is never asked to
      // narrate its own tool calls.
      if (body.actions && body.actions.length > 0) {
        const said = body.actions.map((action) => runAction(action)).filter((line) => line.length > 0);
        const sentence = said.length > 0 ? said.join(" ") : "I could not do that.";
        turnIdRef.current += 1;
        setTurns((previous) =>
          [{ id: turnIdRef.current, you: question, it: sentence, ms: Date.now() - startedAt }, ...previous].slice(0, 30),
        );
        busyRef.current = false;
        await say(sentence);
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
      recentlySpokenRef.current = [...recentlySpokenRef.current, body.reply].slice(-4);
      speakingRef.current = true;
      await speak(body.reply, () => setStateBoth("speaking"));
      speakingRef.current = false;
      lastSpokeEndedAtRef.current = Date.now();
      // STRAIGHT BACK TO SLEEP. It does not keep listening after it has answered. Say the wake word
      // again for the next thing.
      followUpUntilRef.current = 0;
      chirp("sleep");
      setStateBoth("asleep");
      setLive("");
    } catch (error) {
      busyRef.current = false;
      const message = error instanceof Error ? error.message : String(error);
      setProblem(message);
      speakingRef.current = true;
      await speak("Something went wrong.");
      speakingRef.current = false;
      lastSpokeEndedAtRef.current = Date.now();
      setStateBoth("asleep");
    }
  }, [runAction, setStateBoth]);

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
        }
        // Interrupting while it talks: saying the word again cuts it off.
        if (stateRef.current === "speaking" && matchWakeWord(text, wakeWordRef.current) !== null) {
          const own = recentlySpokenRef.current.some((said) => similarity(text, said) >= ECHO_SIMILARITY);
          if (own) {
            return;
          }
          speakingRef.current = false;
          lastSpokeEndedAtRef.current = Date.now();
          stopSpeaking();
          chirp("wake");
          setStateBoth("listening");
          followUpUntilRef.current = Date.now() + FOLLOW_UP_MS;
        }
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
          void ask(match.command);
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
        void ask(text);
      }
    },
    [ask, setStateBoth],
  );

  const start = useCallback(async () => {
    setProblem(null);
    setNotice(null);
    setResultCount(0);
    setLastHeardAt(null);

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
      // A first utterance primes the voice on some browsers, which otherwise swallow the first reply.
      void speak(" ");
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
    stopSpeaking();
    setStateBoth("off");
    setLive("");
    setLevel(0);
    setSessionRunning(false);
  }, [setStateBoth]);

  useEffect(() => () => {
    listenerRef.current?.stop();
    micRef.current?.stop();
  }, []);

  const weakness = describeWakeWordWeakness(wakeWord);

  return (
    <div className="assistant" data-state={state}>
      <div className="eyeWrap">
        <div className="eye" />
      </div>

      <p className="stateLine">
        {state === "off" ? "Not listening" : null}
        {state === "asleep" ? `Say "${wakeWord}"` : null}
        {state === "listening" ? "Listening" : null}
        {state === "thinking" ? "Thinking" : null}
        {state === "speaking" ? "Talking" : null}
      </p>

      <p className="liveLine">{state === "off" ? "" : live}</p>

      {state !== "off" ? (
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
          </p>
          {notice !== null ? <p className="micNotice">{notice}</p> : null}
          {suppressed !== null ? <p className="micNotice">{suppressed}</p> : null}
        </div>
      ) : null}

      <div className="assistantButtons">
        {state === "off" ? (
          <button className="big go" onClick={() => void start()}>Start</button>
        ) : (
          <button className="big stop" onClick={stop}>Stop</button>
        )}
        <button onClick={() => setSettingsOpen((o) => !o)}>{settingsOpen ? "Hide" : "Settings"}</button>
      </div>

      {problem !== null ? <p className="verdict bad">{problem}</p> : null}

      {settingsOpen ? (
        <div className="settings">
          <label>
            Wake word
            <input value={wakeWord} onChange={(e) => setWakeWord(e.target.value)} spellCheck={false} />
          </label>
          {weakness !== null ? <p className="advice">{weakness}</p> : null}
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

      {turns.length > 0 ? (
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
    </div>
  );
}
