import { useCallback, useEffect, useRef, useState } from "react";
import { MicRecorder } from "../dictation/recorder";
import { blobToWav16kMono } from "../dictation/wav";
import { playReadyCue, playYourTurnCue } from "../dictation/readyCue";
import { detectEndPhrase, detectInterrupt } from "./controlPhrases";
import { speakCarModeText, transcribeCarModeAudio, type CarModeAction } from "./carModeApi";
import { gatewayErrorMessage } from "../api/client";

// The Car Mode turn-taking machine (new build B). This shared hook owns the whole walkie-talkie loop so
// the page is a thin view (decision 6). There is NO silence timer that ends a turn - the owner pauses to
// think for as long as he likes, and a pause only ends the turn when what he said ends with "over and out".
//
// SINGLE MIC STREAM, control detection folded into the Gateway transcription (the architecture, 2026-07-11).
// The browser's built-in speech recognizer was dropped: a harness proved Chrome's fake-audio capture does
// not even feed webkitSpeechRecognition, and it is flaky and untestable - the likely reason the phone
// failed. Instead there is ONE microphone consumer, MicRecorder, and the control words are found by the
// SAME proven Gateway transcription (Whisper) we already use for the command. One getUserMedia stream, its
// AnalyserNode driving BOTH the real level meter AND a cheap local silence detector, and no second consumer
// so nothing contends for the microphone.
//
// Two states, plus a brief Thinking state while the brain works:
//   Listening - the owner is talking. MicRecorder accumulates his audio; the level meter shows it is being
//               picked up. When he PAUSES (level under a threshold for ~0.7s AFTER speech), the accumulated
//               audio is transcribed by the Gateway; the turn ends ONLY if that transcript ends with "over
//               and out" (command = transcript minus the phrase). A mid-thought pause whose transcript does
//               NOT end with the phrase does nothing - he keeps his turn. No silence-based turn-end.
//   Thinking  - the command is answered by the brain.
//   Speaking  - the reply plays through <audio>, while a short rolling-window Gateway transcription watches
//               for "stop"/"wait"/"shut up" so the owner can cut it off. echoCancellation keeps the
//               assistant's own voice out of that window; the touch Stop button is the instant path.
//
// The audible handshake (mission, first-class): the "my turn" water-drop (playReadyCue) fires the instant a
// turn is taken; the "your turn" double-blip (playYourTurnCue) fires whenever the microphone is the owner's
// again. Two clearly distinct tones so he always knows whose turn it is without looking.

/** The reply the injected brain produces for one turn: what to say, and (Phase 2+) what it did. */
export interface CarModeReply {
  spoken: string;
  actions?: CarModeAction[];
  pendingConfirmation?: boolean;
}

/** One command/answer exchange, kept for the on-screen scrollback. */
export interface CarModeExchange {
  command: string;
  spoken: string;
  actions: CarModeAction[];
}

export type CarModePhase = "idle" | "listening" | "thinking" | "speaking";

/** Everything the presentational Car Mode page needs. The page owns only JSX + the route; all state, the
 *  state machine, capture, transcription, playback, and the cues live behind these members. */
export interface CarModeView {
  phase: CarModePhase;
  /** True once the owner has tapped into voice mode; false before start() and after stop(). */
  started: boolean;
  /** The most recent command shown on screen (the transcript with the end phrase stripped). */
  transcript: string;
  /** The assistant's most recent spoken reply text (shown while it speaks). */
  reply: string;
  /** What the assistant did on the latest turn (Phase 2+); empty for a pure question. */
  actions: CarModeAction[];
  /** True while the assistant is holding a destructive action for a spoken confirmation (Phase 3). */
  pendingConfirmation: boolean;
  /** A loud, specific failure line; never a silent stall (decision 8). Null when healthy. */
  error: string | null;
  /** True when this browser cannot capture audio for Car Mode (no getUserMedia / MediaRecorder). */
  unsupported: boolean;
  /** Recent exchanges, newest last, for the on-screen scrollback. */
  history: CarModeExchange[];
  /** The last transcript the Gateway returned for a pause / rolling window (Car Mode diagnostic: lets the
   *  owner SEE what the phone heard). Empty until the first transcription of the current turn. */
  lastHeard: string;
  /** A short capture/transcription state for the debug readout ("listening", "pause - transcribing",
   *  'heard: "..."', "thinking", "watching for stop", "interrupt"). */
  captureState: string;
  /** The last transcription error line, or null when none. Distinct from `error` (the loud user-facing
   *  failure); this is the raw diagnostic detail from a background probe. */
  captureError: string | null;
  /** The current microphone input level in 0..1, sampled live from the capture stream's AnalyserNode, for
   *  the on-screen level meter. Read on an animation frame; returns 0 when not capturing. A polled getter
   *  (not React state) so the meter animates without re-rendering the whole page. */
  getMicLevel: () => number;
  /** End the current turn by TOUCH: transcribe what has been captured so far and take the turn immediately
   *  (the instant path, not waiting for a pause). A no-op outside Listening. */
  endTurn: () => void;
  /** Interrupt the assistant by TOUCH: silence the reply instantly and hand the microphone back. A no-op
   *  unless the assistant is Speaking. */
  interrupt: () => void;
  start: () => Promise<void>;
  stop: () => void;
}

/** Options: the brain responder is INJECTED so Phase 1 passes a canned acknowledgement and Phase 2 passes
 *  the real POST /carmode/turn call, over the identical turn-taking machine. */
export interface UseCarModeOptions {
  respond: (command: string, signal: AbortSignal) => Promise<CarModeReply>;
}

// Level below this (0..1 from the AnalyserNode) counts as quiet; at or above it counts as speech.
const SILENCE_THRESHOLD = 0.06;
// Quiet for this long AFTER speech has been heard counts as a pause worth transcribing.
const PAUSE_MS = 700;
// How often the rolling window is transcribed while the assistant is speaking, to catch an interrupt word.
const SPEAKING_POLL_MS = 1500;
// A snapshot smaller than this is just the container header with no real audio - skip transcribing it.
const MIN_CLIP_BYTES = 2000;

/** Whether this browser can capture audio for Car Mode. Car Mode is Chromium-first (decision 7); elsewhere
 *  the page tells the owner plainly instead of silently degrading (no fallback, decision 8). */
function isCaptureSupported(): boolean {
  if (typeof navigator === "undefined" || typeof window === "undefined") return false;
  const md = navigator.mediaDevices as MediaDevices | undefined;
  return Boolean(md && md.getUserMedia) && typeof MediaRecorder !== "undefined";
}

export function useCarMode(options: UseCarModeOptions): CarModeView {
  const respond = options.respond;

  const [phase, setPhase] = useState<CarModePhase>("idle");
  const [started, setStarted] = useState(false);
  const [transcript, setTranscript] = useState("");
  const [reply, setReply] = useState("");
  const [actions, setActions] = useState<CarModeAction[]>([]);
  const [pendingConfirmation, setPendingConfirmation] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [history, setHistory] = useState<CarModeExchange[]>([]);
  // Diagnostic surface: the last Gateway transcript, the capture state, and the last transcription error.
  const [lastHeard, setLastHeard] = useState("");
  const [captureState, setCaptureState] = useState("not started");
  const [captureError, setCaptureError] = useState<string | null>(null);
  const unsupported = !isCaptureSupported();

  // Long-lived collaborators, held in refs so the effect wiring never re-creates them mid-session.
  const recorderRef = useRef<MicRecorder | null>(null);
  const audioRef = useRef<HTMLAudioElement | null>(null);
  const clipUrlRef = useRef<string | null>(null);
  const abortRef = useRef<AbortController | null>(null);

  // Silence-detector bookkeeping, read/written from the animation-frame loop (not a React render).
  const rafRef = useRef<number>(0);
  const heardSpeechRef = useRef(false); // has the owner spoken since the last (re)start of listening?
  const belowSinceRef = useRef(0); // performance.now() the level first dropped below the threshold, or 0
  const busyRef = useRef(false); // a background transcription is in flight (do not start another)
  const speakingPollRef = useRef<number | null>(null); // setInterval id for the speaking rolling window

  // The phase, read synchronously inside the loop/timer callbacks (not a React render). A ref mirror
  // avoids a stale closure so the machine always branches on the CURRENT phase.
  const phaseRef = useRef<CarModePhase>("idle");
  const setPhaseBoth = useCallback((p: CarModePhase) => {
    phaseRef.current = p;
    setPhase(p);
  }, []);

  // "Latest" refs for the two functions the long-lived loop/interval invoke, so those callbacks always run
  // the current implementation and never a stale closure, regardless of React re-renders.
  const onPauseRef = useRef<() => void>(() => {});
  const onSpeakingTickRef = useRef<() => void>(() => {});

  const revokeClip = useCallback(() => {
    if (clipUrlRef.current !== null) {
      URL.revokeObjectURL(clipUrlRef.current);
      clipUrlRef.current = null;
    }
  }, []);

  const stopSpeakingPoll = useCallback(() => {
    if (speakingPollRef.current !== null) {
      clearInterval(speakingPollRef.current);
      speakingPollRef.current = null;
    }
  }, []);

  // Announce a failure LOUDLY (decision 8). The failure is put on screen AND spoken through the browser's
  // local speech synthesis - deliberately NOT the Gateway voice, because the most common failure (an
  // offline / out-of-credit Gateway) is exactly when POST /wingman/tts is also down, and a spoken failure
  // must still be heard eyes-free. Used ONLY to announce failures, never the assistant's normal replies.
  const announceError = useCallback((message: string) => {
    setError(message);
    console.log(`[CarMode] FAILURE: ${message}`);
    try {
      const synth = (window as unknown as { speechSynthesis?: SpeechSynthesis }).speechSynthesis;
      if (synth) {
        synth.cancel();
        synth.speak(new SpeechSynthesisUtterance(message));
      }
    } catch {
      // Local synthesis is a courtesy on top of the on-screen error; never let it throw into the loop.
    }
  }, []);

  // Stop the current capture segment (discarding its clip) and start a fresh one on the same recorder, so
  // the next segment's accumulated buffer starts clean with a container header. Used at every phase
  // boundary. Reused rather than a new getUserMedia each time keeps it to one microphone consumer.
  const restartCapture = useCallback(async () => {
    const rec = recorderRef.current;
    if (rec === null) return;
    try {
      if (rec.isRecording) await rec.stop();
      await rec.start();
    } catch (err) {
      announceError(err instanceof Error ? err.message : "Could not open the microphone.");
    }
  }, [announceError]);

  // Enter Listening: the microphone is the owner's again. Play the "your turn" cue, reset the silence
  // detector, clear the on-screen transcript for the fresh turn, and open a clean capture segment.
  const enterListening = useCallback(async () => {
    stopSpeakingPoll();
    setPhaseBoth("listening");
    setError(null);
    playYourTurnCue();
    heardSpeechRef.current = false;
    belowSinceRef.current = 0;
    busyRef.current = false;
    setLastHeard("");
    setCaptureError(null);
    setCaptureState("listening");
    await restartCapture();
  }, [restartCapture, setPhaseBoth, stopSpeakingPoll]);

  // Synthesize `text` through the one good Gateway voice and play it, entering Speaking. Once playing, open
  // a fresh capture segment and start the rolling-window interrupt watch (barge-in). A synthesis failure is
  // announced loudly and the microphone returns to the owner.
  const speakAndPlay = useCallback(
    async (text: string, signal: AbortSignal) => {
      try {
        const clip = await speakCarModeText(text, signal);
        if (signal.aborted) return;
        revokeClip();
        const url = URL.createObjectURL(clip);
        clipUrlRef.current = url;
        const audio = audioRef.current;
        if (audio === null) throw new Error("The audio player was not ready.");
        audio.src = url;
        setPhaseBoth("speaking");
        setCaptureState("watching for stop");
        await audio.play();
        // Barge-in: a fresh capture segment plus the rolling-window transcription watch for "stop".
        await restartCapture();
        stopSpeakingPoll();
        speakingPollRef.current = window.setInterval(() => onSpeakingTickRef.current(), SPEAKING_POLL_MS);
      } catch (err) {
        if (signal.aborted) return;
        announceError(gatewayErrorMessage(err));
        await enterListening();
      }
    },
    [announceError, enterListening, restartCapture, revokeClip, setPhaseBoth, stopSpeakingPoll],
  );

  // Take the turn: the command (already stripped of "over and out") is answered by the brain and the reply
  // is spoken. Guards against double-entry so a touch tap and a pause probe cannot both fire one turn.
  const takeTurn = useCallback(
    async (command: string) => {
      if (phaseRef.current !== "listening") return;
      setPhaseBoth("thinking");
      playReadyCue(); // "my turn" - the turn is taken
      stopSpeakingPoll();
      const rec = recorderRef.current;
      try {
        if (rec !== null && rec.isRecording) await rec.stop();
      } catch {
        // the segment is being torn down; nothing more to do here
      }

      const controller = new AbortController();
      abortRef.current = controller;

      const trimmed = command.trim();
      setTranscript(trimmed);
      setCaptureState("thinking");
      console.log(`[CarMode] command: "${trimmed}"`);

      try {
        if (trimmed.length === 0) {
          await speakAndPlay("I didn't catch a request. Go ahead when you're ready.", controller.signal);
          return;
        }
        const answer = await respond(trimmed, controller.signal);
        const spoken = answer.spoken.trim();
        setReply(spoken);
        setActions(answer.actions ?? []);
        setPendingConfirmation(Boolean(answer.pendingConfirmation));
        setHistory((prev) => [...prev, { command: trimmed, spoken, actions: answer.actions ?? [] }]);
        if (spoken.length === 0) {
          await enterListening();
          return;
        }
        await speakAndPlay(spoken, controller.signal);
      } catch (err) {
        if (controller.signal.aborted) return; // stop()/interrupt aborted this turn on purpose
        announceError(gatewayErrorMessage(err));
        await enterListening();
      }
    },
    [respond, announceError, enterListening, speakAndPlay, setPhaseBoth, stopSpeakingPoll],
  );

  // Transcribe the audio captured so far and decide the turn. `force` is the touch "over and out": it ends
  // the turn with whatever was said (phrase stripped if present). A background pause probe (force=false)
  // ends the turn ONLY if the transcript ends with the phrase; otherwise the owner keeps his turn. A probe
  // failure is recorded but NOT spoken (it is a background check, not a user action); a forced failure is
  // announced loudly (the owner explicitly acted).
  const transcribeAndDecide = useCallback(
    async (force: boolean) => {
      const rec = recorderRef.current;
      if (rec === null) return;
      if (!force) {
        if (busyRef.current) return;
        busyRef.current = true;
      }
      try {
        const clip = rec.snapshot();
        if (clip.size < MIN_CLIP_BYTES) {
          if (force) void takeTurn("");
          return;
        }
        if (!force) setCaptureState("pause - transcribing");
        const { wav } = await blobToWav16kMono(clip);
        const transcript = (await transcribeCarModeAudio(wav)).trim();
        setLastHeard(transcript);
        setCaptureError(null);
        const parsed = detectEndPhrase(transcript);
        setCaptureState(`heard: "${transcript}"`);
        if (parsed.ended) {
          console.log("[CarMode] end phrase heard -> taking the turn");
          void takeTurn(parsed.command);
        } else if (force) {
          console.log('[CarMode] "over and out" tapped -> taking the turn with the heard command');
          void takeTurn(transcript);
        } else {
          // A mid-thought pause: keep the turn. Wait for fresh speech before probing again.
          heardSpeechRef.current = false;
          belowSinceRef.current = 0;
          setCaptureState("listening");
        }
      } catch (err) {
        const msg = gatewayErrorMessage(err);
        setCaptureError(msg);
        if (force) announceError(msg);
        else {
          heardSpeechRef.current = false;
          belowSinceRef.current = 0;
          setCaptureState("listening");
        }
      } finally {
        if (!force) busyRef.current = false;
      }
    },
    [announceError, takeTurn],
  );

  // The rolling-window interrupt watch, run on a timer while the assistant is speaking. Transcribe the
  // recent audio and, if it carries an interrupt word, cut the assistant off. A probe failure is recorded,
  // not spoken (the touch Stop button is the guaranteed path).
  const watchForInterrupt = useCallback(async () => {
    if (phaseRef.current !== "speaking" || busyRef.current) return;
    const rec = recorderRef.current;
    if (rec === null) return;
    busyRef.current = true;
    try {
      const clip = rec.snapshot();
      if (clip.size < MIN_CLIP_BYTES) return;
      const { wav } = await blobToWav16kMono(clip);
      const transcript = (await transcribeCarModeAudio(wav)).trim();
      setLastHeard(transcript);
      setCaptureError(null);
      if (phaseRef.current === "speaking" && detectInterrupt(transcript)) {
        console.log("[CarMode] interrupt heard -> silencing and returning the turn");
        const audio = audioRef.current;
        try {
          audio?.pause();
        } catch {
          /* nothing playing */
        }
        setCaptureState("interrupt");
        await enterListening();
      }
    } catch (err) {
      setCaptureError(gatewayErrorMessage(err));
    } finally {
      busyRef.current = false;
    }
  }, [enterListening]);

  // Keep the "latest" refs the long-lived loop/interval call pointed at the current implementations.
  onPauseRef.current = () => void transcribeAndDecide(false);
  onSpeakingTickRef.current = () => void watchForInterrupt();

  // Touch controls (the app must be fully usable by touch so testing is never blocked on the spoken
  // phrase). "Over and out" forces an immediate transcribe-and-end; "Stop" cuts the reply off instantly.
  const endTurn = useCallback(() => {
    if (phaseRef.current !== "listening") return;
    void transcribeAndDecide(true);
  }, [transcribeAndDecide]);

  const interrupt = useCallback(() => {
    if (phaseRef.current !== "speaking") return;
    console.log('[CarMode] "stop" tapped -> silencing and returning the turn');
    const audio = audioRef.current;
    try {
      audio?.pause();
    } catch {
      /* nothing playing */
    }
    setCaptureState("interrupt");
    void enterListening();
  }, [enterListening]);

  // The live microphone level for the on-screen meter, polled by the page on an animation frame. Reads the
  // capture stream's AnalyserNode (display only) and returns 0 when the microphone is not capturing.
  const getMicLevel = useCallback(() => recorderRef.current?.level() ?? 0, []);

  const start = useCallback(async () => {
    if (started) return;
    if (unsupported) {
      setError("Car Mode needs Chrome or another Chromium browser for hands-free voice.");
      return;
    }
    console.log("[CarMode] start");
    setStarted(true);
    setError(null);
    recorderRef.current = new MicRecorder();
    audioRef.current = new Audio();
    audioRef.current.onended = () => {
      // The reply finished on its own: hand the microphone back to the owner.
      if (phaseRef.current === "speaking") void enterListening();
    };

    // The single silence-detector loop, live for the whole session; it only acts while Listening. When the
    // level drops below the threshold for PAUSE_MS AFTER speech was heard, it fires a pause probe.
    const tick = () => {
      const rec = recorderRef.current;
      if (rec !== null && phaseRef.current === "listening") {
        const level = rec.level();
        if (level >= SILENCE_THRESHOLD) {
          heardSpeechRef.current = true;
          belowSinceRef.current = 0;
        } else if (heardSpeechRef.current) {
          if (belowSinceRef.current === 0) {
            belowSinceRef.current = performance.now();
          } else if (performance.now() - belowSinceRef.current >= PAUSE_MS && !busyRef.current) {
            belowSinceRef.current = 0;
            onPauseRef.current();
          }
        }
      }
      rafRef.current = window.requestAnimationFrame(tick);
    };
    rafRef.current = window.requestAnimationFrame(tick);

    await enterListening();
  }, [started, unsupported, enterListening]);

  const stop = useCallback(() => {
    console.log("[CarMode] stop");
    abortRef.current?.abort();
    if (rafRef.current !== 0) {
      window.cancelAnimationFrame(rafRef.current);
      rafRef.current = 0;
    }
    stopSpeakingPoll();
    try {
      recorderRef.current?.dispose();
    } catch {
      /* already released */
    }
    recorderRef.current = null;
    const audio = audioRef.current;
    if (audio !== null) {
      audio.onended = null;
      try {
        audio.pause();
      } catch {
        /* nothing playing */
      }
    }
    audioRef.current = null;
    revokeClip();
    setStarted(false);
    setCaptureState("not started");
    setPhaseBoth("idle");
  }, [revokeClip, setPhaseBoth, stopSpeakingPoll]);

  // Tear everything down if the page unmounts mid-session (navigating away from Car Mode).
  useEffect(() => {
    return () => {
      abortRef.current?.abort();
      if (rafRef.current !== 0) window.cancelAnimationFrame(rafRef.current);
      if (speakingPollRef.current !== null) clearInterval(speakingPollRef.current);
      try {
        recorderRef.current?.dispose();
      } catch {
        /* already released */
      }
      const audio = audioRef.current;
      if (audio !== null) {
        audio.onended = null;
        try {
          audio.pause();
        } catch {
          /* nothing playing */
        }
      }
      if (clipUrlRef.current !== null) URL.revokeObjectURL(clipUrlRef.current);
    };
  }, []);

  return {
    phase,
    started,
    transcript,
    reply,
    actions,
    pendingConfirmation,
    error,
    unsupported,
    history,
    lastHeard,
    captureState,
    captureError,
    getMicLevel,
    endTurn,
    interrupt,
    start,
    stop,
  };
}
