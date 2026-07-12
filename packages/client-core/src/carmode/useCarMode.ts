import { useCallback, useEffect, useRef, useState } from "react";
import { MicRecorder } from "../dictation/recorder";
import { blobToWav16kMono } from "../dictation/wav";
import { playReadyCue, playYourTurnCue } from "../dictation/readyCue";
import { detectEndPhrase, detectInterrupt } from "./controlPhrases";
import {
  postCarModeTelemetry,
  speakCarModeText,
  transcribeCarModeAudio,
  type CarModeAction,
  type CarModeServerTiming,
} from "./carModeApi";
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

/** The reply the injected brain produces for one turn: what to say, and (Phase 2+) what it did. `turnId`
 *  and `timing` are the server side of the performance-round telemetry: the browser merges them with its
 *  own client stamps into one record it posts back. They are optional so the Phase 1 stand-in responder
 *  (no server) still satisfies this shape. */
export interface CarModeReply {
  spoken: string;
  actions?: CarModeAction[];
  pendingConfirmation?: boolean;
  turnId?: string;
  timing?: CarModeServerTiming | null;
}

/** One turn's client + server timing, gathered across the turn-taking machine and posted once first audio
 *  plays. All times are milliseconds; only counts and lengths are kept, never any command or reply text. */
interface TurnMetrics {
  turnId: string;
  pauseDetectedAt: number; // performance.now() when the ending pause was detected / "over and out" tapped
  pauseToTranscribeMs: number; // that pause to the command transcript in hand (transcode + network + server)
  transcodeMs: number; // the client-side WebM/Opus -> 16k mono WAV transcode alone (phone CPU)
  commandChars: number;
  replyChars: number;
  brainMs: number; // POST /carmode/turn round trip as the browser saw it
  replyReadyAt: number; // performance.now() when the spoken reply text was in hand
  server: CarModeServerTiming | null;
  actionsCount: number;
  pendingConfirmation: boolean;
  ttsMs: number; // first-chunk text-to-speech round trip
  firstAudioMs: number; // reply-in-hand to first audio playing
  totalTurnMs: number; // pause detected to first audio playing (what the owner feels)
  posted: boolean;
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

/**
 * Split a reply into the first sentence and the remainder, so the browser can start synthesizing and
 * PLAYING the first sentence while the rest is still being synthesized - the owner hears an answer sooner
 * (performance round: begin playback on the first sentence, not the whole reply). Returns exactly two parts
 * so at most one extra text-to-speech call is ever made; the remainder is an empty string when the reply is
 * a single sentence (then it is one synthesis, unchanged). A very short lead fragment (for example "Okay.")
 * is NOT split off on its own - it would just add a round trip for a word - so the whole reply stays as one.
 */
export function splitFirstSentence(text: string): [string, string] {
  const trimmed = text.trim();
  const match = trimmed.match(/^(.+?[.!?]+)\s+(\S.*)$/s);
  if (match && match[1].trim().length >= 12 && match[2].trim().length > 0) {
    return [match[1].trim(), match[2].trim()];
  }
  return [trimmed, ""];
}

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

  // Performance-round telemetry: the metrics for the turn in flight, filled across transcribe -> brain ->
  // speak and posted once first audio plays. Null between turns.
  const turnMetricsRef = useRef<TurnMetrics | null>(null);
  // The chunked-playback "stop now" resolver: set while a clip is playing so an interrupt (voice "stop" or
  // the Stop button) or End Car Mode can end the current clip's play promise and unblock the play loop.
  const playbackStopRef = useRef<(() => void) | null>(null);

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

  // Post one turn's merged timing record ONCE (guards double-post). Only a real brain turn (with a server
  // turnId and server timing) is posted; the canned "I didn't catch that" replies carry no server turn.
  // Best-effort and fire-and-forget - it never blocks or disrupts the spoken reply.
  const postTurnTelemetry = useCallback((m: TurnMetrics | null) => {
    if (m === null || m.posted || m.turnId.length === 0 || m.server === null) return;
    m.posted = true;
    void postCarModeTelemetry({
      turnId: m.turnId,
      pauseToTranscribeMs: m.pauseToTranscribeMs,
      transcodeMs: m.transcodeMs,
      brainMs: m.brainMs,
      ttsMs: m.ttsMs,
      firstAudioMs: m.firstAudioMs,
      totalTurnMs: m.totalTurnMs,
      serverTotalMs: m.server.totalMs,
      modelCallCount: m.server.modelCallCount,
      modelMsTotal: m.server.modelMsTotal,
      modelMs: m.server.modelMs,
      fleetReadCount: m.server.fleetReadCount,
      fleetReadMsTotal: m.server.fleetReadMsTotal,
      rounds: m.server.rounds,
      commandChars: m.commandChars,
      replyChars: m.replyChars,
      actionsCount: m.actionsCount,
      pendingConfirmation: m.pendingConfirmation,
    });
  }, []);

  // Play one audio clip and resolve when it FINISHES ("ended") or is stopped early ("stopped" - an
  // interrupt or End Car Mode). Used to play the reply's sentence chunks in order: the loop awaits each
  // clip, so the next sentence starts the instant the previous one ends. A play() rejection (autoplay
  // block) resolves "stopped" so the loop never hangs.
  const playBlob = useCallback((url: string): Promise<"ended" | "stopped"> => {
    return new Promise((resolve) => {
      const audio = audioRef.current;
      if (audio === null) {
        resolve("stopped");
        return;
      }
      let done = false;
      const finish = (how: "ended" | "stopped") => {
        if (done) return;
        done = true;
        audio.onended = null;
        playbackStopRef.current = null;
        resolve(how);
      };
      audio.onended = () => finish("ended");
      playbackStopRef.current = () => finish("stopped");
      audio.src = url;
      void audio.play().catch(() => finish("stopped"));
    });
  }, []);

  // Stop whatever clip is playing right now (pause the element and resolve its play promise), so the
  // chunked-playback loop unwinds cleanly. Shared by the touch Stop button, the voice interrupt watch, and
  // End Car Mode.
  const haltPlayback = useCallback(() => {
    const audio = audioRef.current;
    try {
      audio?.pause();
    } catch {
      /* nothing playing */
    }
    playbackStopRef.current?.();
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
        const metrics = turnMetricsRef.current;
        // Begin on the FIRST sentence: synthesize and play it while the remainder is still synthesizing, so
        // the owner hears an answer sooner (performance round). A single-sentence reply stays one synthesis.
        const [first, rest] = splitFirstSentence(text);

        const ttsStart = performance.now();
        const firstBlob = await speakCarModeText(first, signal);
        if (signal.aborted) return;
        if (metrics !== null) metrics.ttsMs = performance.now() - ttsStart;

        // Prefetch the remainder in parallel so it is ready by the time the first sentence finishes.
        const restPromise = rest.length > 0 ? speakCarModeText(rest, signal) : null;

        revokeClip();
        const url0 = URL.createObjectURL(firstBlob);
        clipUrlRef.current = url0;
        const audio = audioRef.current;
        if (audio === null) throw new Error("The audio player was not ready.");

        setPhaseBoth("speaking");
        setCaptureState("watching for stop");

        // Begin playing the first sentence; record when the owner first hears audio, then post the merged
        // timing record for the turn (fire-and-forget - it never delays playback).
        const firstPlayed = playBlob(url0);
        if (metrics !== null && metrics.replyReadyAt > 0) {
          const nowMs = performance.now();
          metrics.firstAudioMs = nowMs - metrics.replyReadyAt;
          metrics.totalTurnMs = metrics.pauseDetectedAt > 0 ? nowMs - metrics.pauseDetectedAt : 0;
          postTurnTelemetry(metrics);
        }

        // Barge-in: a fresh capture segment plus the rolling-window transcription watch for "stop".
        await restartCapture();
        stopSpeakingPoll();
        speakingPollRef.current = window.setInterval(() => onSpeakingTickRef.current(), SPEAKING_POLL_MS);

        const how0 = await firstPlayed;
        if (how0 === "stopped" || signal.aborted) return; // interrupted / ended session mid-reply

        // Play the remainder (if any) right after the first sentence, back to back.
        if (restPromise !== null) {
          let restBlob: Blob;
          try {
            restBlob = await restPromise;
          } catch (err) {
            if (signal.aborted) return;
            throw err;
          }
          if (signal.aborted) return;
          revokeClip();
          const url1 = URL.createObjectURL(restBlob);
          clipUrlRef.current = url1;
          const how1 = await playBlob(url1);
          if (how1 === "stopped" || signal.aborted) return;
        }

        // The reply finished on its own: hand the microphone back to the owner.
        await enterListening();
      } catch (err) {
        if (signal.aborted) return;
        announceError(gatewayErrorMessage(err));
        await enterListening();
      }
    },
    [announceError, enterListening, playBlob, postTurnTelemetry, restartCapture, revokeClip, setPhaseBoth, stopSpeakingPoll],
  );

  // Take the turn: the command (already stripped of "over and out") is answered by the brain and the reply
  // is spoken. Guards against double-entry so a touch tap and a pause probe cannot both fire one turn.
  const takeTurn = useCallback(
    async (command: string) => {
      if (phaseRef.current !== "listening") return;
      setPhaseBoth("thinking");
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
          // A forced turn with nothing heard: a canned nudge, no server turn, so no telemetry record.
          turnMetricsRef.current = null;
          await speakAndPlay("I didn't catch a request. Go ahead when you're ready.", controller.signal);
          return;
        }
        const brainStart = performance.now();
        const answer = await respond(trimmed, controller.signal);
        const spoken = answer.spoken.trim();
        // Fill the turn metrics the transcribe step started (same object via the ref), for telemetry.
        const metrics = turnMetricsRef.current;
        if (metrics !== null) {
          metrics.brainMs = performance.now() - brainStart;
          metrics.replyReadyAt = performance.now();
          metrics.turnId = answer.turnId ?? "";
          metrics.server = answer.timing ?? null;
          metrics.replyChars = spoken.length;
          metrics.actionsCount = (answer.actions ?? []).length;
          metrics.pendingConfirmation = Boolean(answer.pendingConfirmation);
        }
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
      // The pause was detected (or "over and out" tapped) now; time the command transcription from here so
      // the telemetry record shows how long the owner waits between finishing and the brain starting.
      const pauseDetectedAt = performance.now();
      try {
        const clip = rec.snapshot();
        if (clip.size < MIN_CLIP_BYTES) {
          if (force) void takeTurn("");
          return;
        }
        if (!force) setCaptureState("pause - transcribing");
        // Measure the client-side transcode (phone CPU) separately from the transcribe round trip (network
        // + server), so a real phone turn shows where the pause-to-transcript time actually goes.
        const transcodeStart = performance.now();
        const { wav } = await blobToWav16kMono(clip);
        const transcodeMs = performance.now() - transcodeStart;
        const transcript = (await transcribeCarModeAudio(wav)).trim();
        const pauseToTranscribeMs = performance.now() - pauseDetectedAt;
        setLastHeard(transcript);
        setCaptureError(null);
        const parsed = detectEndPhrase(transcript);
        setCaptureState(`heard: "${transcript}"`);

        // Confirm the turn: fire the "my turn" cue the INSTANT the transcript confirms (before the brain
        // responds), so the owner gets an immediate audible acknowledgement, then seed the turn metrics the
        // brain + speak steps fill, then take the turn.
        const confirmAndTake = (command: string) => {
          playReadyCue();
          turnMetricsRef.current = {
            turnId: "",
            pauseDetectedAt,
            pauseToTranscribeMs,
            transcodeMs,
            commandChars: command.trim().length,
            replyChars: 0,
            brainMs: 0,
            replyReadyAt: 0,
            server: null,
            actionsCount: 0,
            pendingConfirmation: false,
            ttsMs: 0,
            firstAudioMs: 0,
            totalTurnMs: 0,
            posted: false,
          };
          void takeTurn(command);
        };

        if (parsed.ended) {
          console.log("[CarMode] end phrase heard -> taking the turn");
          confirmAndTake(parsed.command);
        } else if (force) {
          console.log('[CarMode] "over and out" tapped -> taking the turn with the heard command');
          confirmAndTake(transcript);
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
        haltPlayback();
        setCaptureState("interrupt");
        await enterListening();
      }
    } catch (err) {
      setCaptureError(gatewayErrorMessage(err));
    } finally {
      busyRef.current = false;
    }
  }, [enterListening, haltPlayback]);

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
    haltPlayback();
    setCaptureState("interrupt");
    void enterListening();
  }, [enterListening, haltPlayback]);

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
    // The reply's sentence chunks are played by playBlob, which sets onended per clip; the speak loop hands
    // the microphone back after the LAST chunk, so no global onended handler is needed here.
    audioRef.current = new Audio();

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
    // Unblock any in-flight chunked playback so the speak loop unwinds instead of awaiting an ended event
    // that will never fire.
    playbackStopRef.current?.();
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
      playbackStopRef.current?.();
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
