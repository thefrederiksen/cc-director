import { useCallback, useEffect, useRef, useState } from "react";
import { MicRecorder } from "../dictation/recorder";
import { blobToWav16kMono } from "../dictation/wav";
import { playReadyCue, playYourTurnCue } from "../dictation/readyCue";
import { decideControlAction, detectEndPhrase } from "./controlPhrases";
import { ControlWordListener, isControlRecognitionSupported } from "./speechRecognition";
import { speakCarModeText, transcribeCarModeAudio, type CarModeAction } from "./carModeApi";
import { gatewayErrorMessage } from "../api/client";

// The Car Mode turn-taking machine (new build B, mission Phase 1). This shared hook owns the whole
// walkie-talkie loop so the page is a thin view (decision 6): capture -> transcribe -> brain -> speak,
// bounded by the two control triggers and the two audible cues. There is NO silence timer anywhere -
// the owner pauses to think for as long as he likes and it stays silent until he says "over and out".
//
// Two states, plus a brief Thinking state while the brain works (mission "New build B"):
//   Listening - the owner is talking. MicRecorder buffers the whole utterance for accurate Gateway
//               transcription, while ControlWordListener watches only for "over and out".
//   Thinking  - the buffered audio is being transcribed and answered.
//   Speaking  - the reply plays through <audio>, while ControlWordListener watches only for
//               "stop"/"wait"/"shut up" so the owner can cut it off instantly.
//
// The audible handshake (mission, first-class): the "my turn" water-drop (playReadyCue) fires the
// instant "over and out" is heard - the assistant is taking the turn; the "your turn" double-blip
// (playYourTurnCue) fires whenever the microphone becomes live for the owner again (start, after a
// reply, or after an interrupt). Two clearly distinct tones so he always knows whose turn it is
// without looking.

/** The reply the injected brain produces for one turn: what to say, and (Phase 2+) what it did. */
export interface CarModeReply {
  spoken: string;
  actions?: CarModeAction[];
  pendingConfirmation?: boolean;
}

/** One transcript/answer exchange, kept for the on-screen scrollback (eyes-on debugging over
 *  chrome://inspect; the interface is sound, so the list is a nicety, not the channel). */
export interface CarModeExchange {
  command: string;
  spoken: string;
  actions: CarModeAction[];
}

export type CarModePhase = "idle" | "listening" | "thinking" | "speaking";

/** Everything the presentational Car Mode page needs. The page owns only JSX + the route; all state,
 *  the state machine, capture, transcription, playback, and the cues live behind these members. */
export interface CarModeView {
  phase: CarModePhase;
  /** True once the owner has tapped into voice mode; false before start() and after stop(). */
  started: boolean;
  /** The most recent transcribed command shown on screen (decision: show the text as it is heard). */
  transcript: string;
  /** The assistant's most recent spoken reply text (shown while it speaks). */
  reply: string;
  /** What the assistant did on the latest turn (Phase 2+); empty for a pure question. */
  actions: CarModeAction[];
  /** True while the assistant is holding a destructive action for a spoken confirmation (Phase 3). */
  pendingConfirmation: boolean;
  /** A loud, specific failure line; never a silent stall (decision 8). Null when healthy. */
  error: string | null;
  /** True when this browser cannot run Car Mode's control-word recognizer (not Chromium, decision 7). */
  unsupported: boolean;
  /** Recent exchanges, newest last, for the on-screen scrollback. */
  history: CarModeExchange[];
  start: () => Promise<void>;
  stop: () => void;
}

/** Options: the brain responder is INJECTED so Phase 1 passes a canned acknowledgement and Phase 2
 *  passes the real POST /carmode/turn call, over the identical turn-taking machine. */
export interface UseCarModeOptions {
  respond: (command: string, signal: AbortSignal) => Promise<CarModeReply>;
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
  const unsupported = !isControlRecognitionSupported();

  // Long-lived collaborators, held in refs so the effect wiring never re-creates them mid-session.
  const recorderRef = useRef<MicRecorder | null>(null);
  const listenerRef = useRef<ControlWordListener | null>(null);
  const audioRef = useRef<HTMLAudioElement | null>(null);
  const clipUrlRef = useRef<string | null>(null);
  const abortRef = useRef<AbortController | null>(null);

  // The phase, read synchronously inside the recognizer callback (which is not a React render). A ref
  // mirror avoids a stale closure so the control-word handler always branches on the CURRENT phase.
  const phaseRef = useRef<CarModePhase>("idle");
  const setPhaseBoth = useCallback((p: CarModePhase) => {
    phaseRef.current = p;
    setPhase(p);
  }, []);

  const revokeClip = useCallback(() => {
    if (clipUrlRef.current !== null) {
      URL.revokeObjectURL(clipUrlRef.current);
      clipUrlRef.current = null;
    }
  }, []);

  // Announce a failure LOUDLY (decision 8). The failure is put on screen AND spoken through the
  // browser's local speech synthesis - deliberately NOT the Gateway voice, because the most common
  // failure (an offline / out-of-credit Gateway) is exactly when POST /wingman/tts is also down, and a
  // spoken failure must still be heard eyes-free. This is used ONLY to announce failures, never to speak
  // the assistant's normal replies (which always use the one good Gateway voice) - so it does not
  // violate the single-voice rule; it is the honest, always-available way to say "this failed".
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

  // Enter Listening: the microphone becomes live for the owner. Play the "your turn" cue, clear the
  // recognizer buffer so a just-used control phrase cannot re-trigger, and open a fresh capture segment.
  const enterListening = useCallback(async () => {
    setPhaseBoth("listening");
    setError(null);
    playYourTurnCue();
    listenerRef.current?.reset();
    try {
      const rec = recorderRef.current;
      if (rec !== null && !rec.isRecording) await rec.start();
    } catch (err) {
      announceError(err instanceof Error ? err.message : "Could not open the microphone.");
    }
  }, [announceError, setPhaseBoth]);

  // Synthesize `text` through the one good Gateway voice and play it, entering Speaking. When it ends
  // (or is interrupted), the microphone returns to the owner. A synthesis failure is announced loudly.
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
        await audio.play();
      } catch (err) {
        if (signal.aborted) return;
        announceError(gatewayErrorMessage(err));
        await enterListening();
      }
    },
    [announceError, enterListening, revokeClip, setPhaseBoth],
  );

  // The owner ended his turn with "over and out": take the turn. Capture stops, the buffered audio is
  // transcribed by the Gateway, the injected brain answers, and the reply is spoken. Every failure is
  // announced loudly and returns the microphone to the owner.
  const takeTurn = useCallback(async () => {
    setPhaseBoth("thinking");
    playReadyCue(); // "my turn" - his sign-off was heard, the assistant is taking the turn
    listenerRef.current?.reset();

    const controller = new AbortController();
    abortRef.current = controller;

    try {
      const rec = recorderRef.current;
      if (rec === null) throw new Error("The microphone was not started.");
      const captured = await rec.stop();
      const { wav } = await blobToWav16kMono(captured);

      const rawTranscript = await transcribeCarModeAudio(wav, controller.signal);
      // Strip a trailing "over and out" the Gateway transcript may also carry, so the brain sees only
      // the command (decision 1). If the phrase is not found verbatim, the whole transcript is the
      // command - the browser recognizer already confirmed the sign-off, so we never re-gate on it here.
      const parsed = detectEndPhrase(rawTranscript);
      const command = (parsed.ended ? parsed.command : rawTranscript).trim();
      setTranscript(command);
      console.log(`[CarMode] command: "${command}"`);

      if (command.length === 0) {
        // He signed off without a command (or nothing transcribed): say so and hand the turn back,
        // rather than sending an empty prompt to the brain.
        await speakAndPlay("I didn't catch a request. Go ahead when you're ready.", controller.signal);
        return;
      }

      const answer = await respond(command, controller.signal);
      const spoken = answer.spoken.trim();
      setReply(spoken);
      setActions(answer.actions ?? []);
      setPendingConfirmation(Boolean(answer.pendingConfirmation));
      setHistory((prev) => [...prev, { command, spoken, actions: answer.actions ?? [] }]);
      if (spoken.length === 0) {
        await enterListening();
        return;
      }
      await speakAndPlay(spoken, controller.signal);
    } catch (err) {
      if (controller.signal.aborted) return; // stop() aborted this turn on purpose
      // A loud, SPECIFIC spoken failure (decision 8): an unreachable Gateway, an out-of-credits 402, or
      // a model error each collapses to the shared, friendly, retry-implying line rather than a raw
      // "Failed to fetch" - and it is spoken, never a silent stall.
      announceError(gatewayErrorMessage(err));
      await enterListening();
    }
  }, [respond, announceError, enterListening, speakAndPlay, setPhaseBoth]);

  // The single control-word handler. It runs OUTSIDE React render (a recognizer callback), so it reads
  // the live phase from phaseRef and branches: in Listening it looks for the end phrase; in Speaking it
  // looks for an interrupt word. Thinking ignores control words (nothing is playing to interrupt, and
  // the turn is already committed).
  const onControlTranscript = useCallback(
    (text: string) => {
      const current = phaseRef.current;
      if (current === "thinking" || current === "idle") return;
      const action = decideControlAction(current, text);
      if (action === "end") {
        console.log("[CarMode] end phrase heard -> taking the turn");
        void takeTurn();
      } else if (action === "interrupt") {
        console.log("[CarMode] interrupt heard -> silencing and returning the turn");
        const audio = audioRef.current;
        try {
          audio?.pause();
        } catch {
          /* nothing playing */
        }
        void enterListening();
      }
    },
    [takeTurn, enterListening],
  );

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
    const listener = new ControlWordListener();
    listenerRef.current = listener;
    try {
      listener.start(onControlTranscript, (message) => announceError(message));
    } catch (err) {
      announceError(err instanceof Error ? err.message : "Could not start the recognizer.");
      setStarted(false);
      return;
    }
    await enterListening();
  }, [started, unsupported, onControlTranscript, announceError, enterListening]);

  const stop = useCallback(() => {
    console.log("[CarMode] stop");
    abortRef.current?.abort();
    listenerRef.current?.stop();
    listenerRef.current = null;
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
    setPhaseBoth("idle");
  }, [revokeClip, setPhaseBoth]);

  // Tear everything down if the page unmounts mid-session (navigating away from Car Mode).
  useEffect(() => {
    return () => {
      abortRef.current?.abort();
      listenerRef.current?.stop();
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
    start,
    stop,
  };
}
