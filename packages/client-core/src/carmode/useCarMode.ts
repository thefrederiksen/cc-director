import { useCallback, useEffect, useRef, useState } from "react";
import { logCaptureHealth } from "../dictation/captureHealth";
import { MicRecorder } from "../dictation/recorder";
import { blobToWav16kMono } from "../dictation/wav";
import { playReadyCue, playYourTurnCue, startThinkingCue, primeCueAudio, releaseCueAudio } from "../dictation/readyCue";
import { detectPhraseAtEnd } from "./controlPhrases";
import { playClip, type PlayOutcome, type PlayClipHooks } from "./audioPlayback";
import {
  getCarModeHelp,
  postCarModeTelemetry,
  postCarModeWarmup,
  speakCarModeText,
  transcribeCarModeAudio,
  type CarModeAction,
  type CarModeServerTiming,
} from "./carModeApi";
import {
  deletePendingTurn,
  getPendingTurn,
  listPendingTurns,
  savePendingTurn,
  type PendingCarModeTurn,
} from "./pendingTurnStore";
import {
  CONNECTION_DOWN_MESSAGE,
  classifyHeldTurn,
  HOLDING_MESSAGE,
  nextTurnRetryDelayMs,
  RECOVERY_PREFIX,
} from "./turnRetry";
import { CreditsError, gatewayErrorMessage } from "../api/client";

// The Car Mode turn-taking machine (v3: button-first, mic never touched during playback). This shared hook
// owns the whole walkie-talkie loop so the page is a thin view (decision 6).
//
// WHY v3 IS THE SHAPE IT IS (data-driven): the reply audio was proven to PLAY to its natural end on the
// phone (Completed=TRUE, PlayedTo==ClipDuration) yet the owner "heard nothing". The telemetry pinned the
// cause: re-opening the microphone (getUserMedia) for a rolling voice-"stop" watch WHILE the reply plays
// ducks/reroutes the <audio> output on mobile so it is inaudible. So the hard rule now is: WHILE SPEAKING,
// NOTHING touches the microphone - the capture stream is fully released and only the <audio> element plays.
//
// The two voice control paths differ by whether the assistant is speaking:
//   - Ending a turn is HANDS-FREE via the spoken sign-off phrase (default "over and out", owner-configurable):
//     a rolling transcription watch runs during Listening and takes the turn the moment the transcript ends
//     with the phrase. This replaces the old finicky silence/pause probe by leaning on the reliable
//     transcription (measured 9/9). The touch "Over and out" BUTTON is the instant fallback. There is no
//     self-trigger risk because the assistant is silent while the owner talks.
//   - Interrupting the reply is done by the touch "Stop" BUTTON, the SOLE interrupt. There is deliberately no
//     voice "stop" watch during playback, because re-opening the mic mid-playback ducks the reply on mobile,
//     AND the generic on-device keyword model self-triggers on the assistant's own voice (issue #1411).
//
// The states, plus a brief Thinking state while the brain works:
//   Listening - the owner is talking. MicRecorder (one getUserMedia stream) accumulates his audio and its
//               AnalyserNode drives the on-screen level meter. The turn ends when he taps "Over and out".
//   Thinking  - the microphone is stopped and RELEASED; the command is answered by the brain.
//   Speaking  - the whole reply plays through the reused, gesture-unlocked <audio> element with the
//               microphone still RELEASED (no getUserMedia). Only the touch "Stop" button cuts it off.
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
  ttsMs: number; // text-to-speech round trip for the whole reply (one clip since the split was reverted)
  firstAudioMs: number; // reply-in-hand to first audio playing
  totalTurnMs: number; // pause detected to first audio playing (what the owner feels)
  // ----- Finickiness of "over and out" (the finicky-end-phrase diagnostic) -----
  transcribeAttempts: number; // how many pause/forced transcribe probes ran this turn before the turn was taken
  // ----- Reply-audio lifecycle (the cut-off-reply diagnostic): how the spoken reply's one clip played -----
  chunks: number; // how many audio clips the reply played (1 after the split revert; a guard for a future regression)
  playStartedAt: number; // performance.now() when the reply clip's play() was requested, or 0
  playMs: number; // how long the reply clip was actually audible: play-started to play-ended / cut off
  clipDurationMs: number; // the SYNTHESIZED reply clip's media length (audio.duration): the whole reply the phone got
  playedToMs: number; // how far INTO the clip playback reached at end/cutoff (audio.currentTime): media-time, not wall-clock
  completed: boolean; // true when the reply clip played fully to its natural end; false when cut off (interrupt / End)
  playRejected: boolean; // true when the reply's play() was REJECTED (mobile autoplay block) so it never sounded
  // ----- Mic-during-playback proof (v3: the mic is released while the reply plays, so these read healthy) -----
  micReacquiredDuringPlayback: boolean; // v3: always false (the mic is never re-opened during playback); kept to PROVE it
  speakingPollCount: number; // v3: always 0 (no rolling-"stop" transcription runs during the reply)
  // ----- Real viewport measurements (v5: the button-cut-off diagnostic, read from HIS phone, not desktop) -----
  viewportInnerHeight: number; // window.innerHeight at telemetry time (the layout viewport height)
  visualViewportHeight: number; // window.visualViewport.height (the ACTUALLY visible height, minus toolbars) or 0
  documentClientHeight: number; // document.documentElement.clientHeight (a third viewport read to cross-check)
  footerBottom: number; // the .car-foot element's getBoundingClientRect().bottom (where the buttons end, in px)
  footerVisible: boolean; // footerBottom <= the visible viewport height: TRUE only when the buttons are on-screen
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
  /** A short capture/transcription state for the debug readout ("listening", "transcribing",
   *  'heard: "..."', "thinking", "speaking", "interrupt", "held"). */
  captureState: string;
  // ----- Offline resilience (mission Phase 4a, issue #1427) -------------------------------------------
  /** True while one or more of the owner's spoken requests are SAVED on the device and waiting to send
   *  (a dead zone). Never a loss: the audio is durable and auto-retries when the connection returns. */
  holding: boolean;
  /** How many spoken requests are currently saved-and-waiting on the device. */
  heldCount: number;
  /** The honest, saved-not-lost line to show/say for the current held state, or null when nothing is held.
   *  Distinct from `error` (a loud red failure): holding is a calm "saved, will send" state. */
  holdMessage: string | null;
  /** The oldest held turn that is too old to fire blind (past the ~30-minute staleness cap) and needs the
   *  owner's explicit send/discard, or null. Since Phase 4b's server idempotency makes any held turn safe
   *  to auto-retry, staleness is the only reason a turn waits for the owner. */
  askOwnerTurn: { id: string; transcript: string } | null;
  /** True once the hands-free end-phrase watch has failed to reach the Gateway several times in a row, so
   *  the page can show the connection is down (paired with the spoken CONNECTION_DOWN cue). */
  connectionDown: boolean;
  /** Owner explicitly sends a stale held turn now. Safe via Phase 4b's server idempotency (it acts at most
   *  once whether or not it was sent before). A no-op for an unknown id. */
  sendHeldTurn: (id: string) => void;
  /** Owner explicitly discards a held turn, dropping its saved audio for good. */
  discardHeldTurn: (id: string) => void;
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
  /** Explain OUT LOUD what Car Mode can do and how to talk to it (Help Mode, issue #1441): the big "Help"
   *  button. It speaks the ONE curated help script from the Gateway (model-free, instant). From the idle
   *  screen it starts Car Mode first (to prime the audio inside the tap gesture) and then speaks; while
   *  running it speaks immediately, cutting off any current reply. A no-op while the brain is thinking. */
  help: () => void;
  start: () => Promise<void>;
  stop: () => void;
}

/** Options: the brain responder is INJECTED so Phase 1 passes a canned acknowledgement and Phase 2 passes
 *  the real POST /carmode/turn call, over the identical turn-taking machine. */
export interface UseCarModeOptions {
  /** The injected brain call. `idempotencyKey` (the durable turn record id, Phase 4b) is passed so the
   *  page can forward it to POST /carmode/turn as the Idempotency-Key, making a re-driven turn act at most
   *  once. It is undefined only for the Phase 1 stand-in / when no durable record backs the turn. */
  respond: (command: string, signal: AbortSignal, idempotencyKey?: string) => Promise<CarModeReply>;
  /** The spoken sign-off phrase that ends the owner's turn hands-free (default "over and out"). The page
   *  passes the owner's configured phrase; when the rolling end-phrase watch hears the transcript end with
   *  it, the turn is taken, exactly as tapping the "Over and out" button does. The button remains the
   *  instant fallback. */
  endPhrase?: string;
}

// How often to send a keep-warm ping WHILE Car Mode is open, so the hosted model + text-to-speech stay hot
// for the drive. Comfortably inside a typical provider keep-alive window; only fires while Car Mode is open.
const KEEP_WARM_MS = 3 * 60 * 1000;
// A snapshot smaller than this is just the container header with no real audio - skip transcribing it.
const MIN_CLIP_BYTES = 2000;
// The default hands-free sign-off phrase; the page can override it with the owner's configured phrase.
const DEFAULT_END_PHRASE = "over and out";
// How often, while Listening, the captured audio is re-transcribed to check whether it now ends with the
// sign-off phrase. This replaces the removed silence/endpoint probe: it leans on the reliable transcription
// (measured 9/9 on "over and out") instead of guessing when the owner paused. The touch button is the
// instant path; this is the hands-free path, ~1s felt delay after the phrase (owner-accepted).
const END_PHRASE_POLL_MS = 800;
// How many consecutive failed end-phrase transcribe ticks (each ~800 ms) before Car Mode audibly tells
// the owner the connection is down, so a dead zone never silently swallows his "over and out" forever
// (the silent-stall fix). Four ticks is ~3 seconds - long enough to ride out a single blip, short enough
// that a real dead zone is announced quickly.
const END_PHRASE_FAIL_THRESHOLD = 4;
// The live microphone level (0..1, from the same AnalyserNode the meter reads) above which the owner is
// treated as actively speaking, so the background re-drive defers rather than cutting him off. Quiet and
// suppressed road noise sit well below this; direct speech spikes above it.
const SPEAKING_LEVEL = 0.08;

/** Whether this browser can capture audio for Car Mode. Car Mode is Chromium-first (decision 7); elsewhere
 *  the page tells the owner plainly instead of silently degrading (no fallback, decision 8). */
function isCaptureSupported(): boolean {
  if (typeof navigator === "undefined" || typeof window === "undefined") return false;
  const md = navigator.mediaDevices as MediaDevices | undefined;
  return Boolean(md && md.getUserMedia) && typeof MediaRecorder !== "undefined";
}

/** True when the browser reports it is offline. A cheap pre-check so the retry driver does not burn a
 *  transcribe round trip into a known-dead network; the real classification still comes from gatewayFetch. */
function isOffline(): boolean {
  return typeof navigator !== "undefined" && navigator.onLine === false;
}

// A tiny, silent WAV clip (a valid RIFF/WAVE header with zero audio samples) used ONLY to unlock the
// reply <audio> element inside the Start tap gesture. It is a data URI so it needs no network and no
// bundled asset.
const SILENT_WAV_DATA_URI =
  "data:audio/wav;base64,UklGRiQAAABXQVZFZm10IBAAAAABAAEAgD4AAAB9AAACABAAZGF0YQAAAAA=";

// Unlock an <audio> element for later programmatic playback, called INSIDE the Start tap gesture. Mobile
// Chrome blocks a play() that is not tied to a live user gesture (NotAllowedError), and Car Mode's reply
// plays SECONDS after the tap - after the transcribe -> brain -> speak pipeline - so by then the gesture
// is gone and the reply is silently refused (the mobile cut-off / "heard nothing" bug). Playing a silent
// clip now, within the tap, marks THIS element as user-activated, so every later reply on the SAME element
// is allowed to play with no fresh gesture. Best-effort: a rejection here is logged, never thrown, because
// the unlock is a courtesy on top of the normal play path.
function unlockAudioElement(audio: HTMLAudioElement): void {
  try {
    audio.src = SILENT_WAV_DATA_URI;
    const played = audio.play();
    if (played && typeof played.then === "function") {
      played
        .then(() => console.log("[CarMode] reply audio element unlocked for autoplay"))
        .catch((error: unknown) => {
          const name = error instanceof Error ? error.name : "unknown";
          console.log(`[CarMode] audio unlock play() rejected (will retry on first reply): ${name}`);
        });
    }
  } catch (error) {
    console.log(`[CarMode] audio unlock threw: ${String(error)}`);
  }
}

export function useCarMode(options: UseCarModeOptions): CarModeView {
  const respond = options.respond;
  // The owner's configured sign-off phrase, mirrored in a ref so the rolling watch's timer callback always
  // reads the CURRENT phrase (a render prop would be a stale closure inside the interval).
  const endPhraseRef = useRef(options.endPhrase ?? DEFAULT_END_PHRASE);
  endPhraseRef.current = options.endPhrase ?? DEFAULT_END_PHRASE;

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

  // Offline resilience (Phase 4a): the held-turn state the page shows, plus the connection-down flag the
  // end-phrase watch raises. `heldTurns` mirrors the durable store, refreshed after every store mutation.
  const [heldTurns, setHeldTurns] = useState<PendingCarModeTurn[]>([]);
  const [holdMessage, setHoldMessage] = useState<string | null>(null);
  const [connectionDown, setConnectionDown] = useState(false);

  // Long-lived collaborators, held in refs so the effect wiring never re-creates them mid-session.
  const recorderRef = useRef<MicRecorder | null>(null);
  const audioRef = useRef<HTMLAudioElement | null>(null);
  const clipUrlRef = useRef<string | null>(null);
  const abortRef = useRef<AbortController | null>(null);

  const keepWarmRef = useRef<number | null>(null); // setInterval id for the keep-warm ping while Car Mode is open

  // Performance-round telemetry: the metrics for the turn in flight, filled across transcribe -> brain ->
  // speak and posted once first audio plays. Null between turns.
  const turnMetricsRef = useRef<TurnMetrics | null>(null);
  // Finickiness diagnostic: how many pause/forced transcribe probes have run in the current turn before it
  // was taken. Reset when the microphone returns to the owner (enterListening) and snapshotted into the
  // turn metrics the instant the turn is confirmed, so one real turn shows how hard "over and out" was to
  // land (a high count means the phrase kept being missed).
  const transcribeAttemptsRef = useRef(0);
  // The playback "stop now" resolver: set while the reply clip is playing so the touch Stop button or End
  // Car Mode can end the play promise and unblock the speak loop.
  const playbackStopRef = useRef<(() => void) | null>(null);
  // The "thinking" ambient cue's stopper (v5): set the instant the owner taps "Over and out" (in the tap
  // gesture, so mobile lets it sound) and called the instant the reply audio starts, so the ~2s of silent
  // work is filled with a gentle working tone. Also cleared when the mic returns to the owner or the
  // session ends, so it can never leak past its turn.
  const thinkingCueStopRef = useRef<(() => void) | null>(null);

  // The hands-free end-phrase watch: a rolling transcription timer during Listening. endPollRef holds its
  // interval id; endPollBusyRef prevents overlapping transcriptions; endTickRef points at the latest tick
  // so the interval (started from enterListening, defined before the tick) always calls the current one.
  const endPollRef = useRef<number | null>(null);
  const endPollBusyRef = useRef(false);
  const endTickRef = useRef<() => void>(() => {});

  // Capture-health (issue #863, #1988 Phase 2): wall-clock the microphone has been open for THIS listening
  // turn, anchored when the mic (re)opens in enterListening. Compared at commit against the decoded audio
  // duration of the committed clip so Car Mode reports the same audio-loss deficit every other surface does
  // - previously it had no measurement at all. 0 before the first listening turn opens.
  const utteranceStartRef = useRef(0);

  // Offline resilience (Phase 4a) refs. currentRecordIdRef is the durable id of the turn in flight, so a
  // brain success can delete exactly that record. The drive* refs run the background re-drive of held
  // audio: drivingRef single-flights it, driveTimerRef holds the cadence timer, driveAttemptRef is the
  // backoff step, and driveTickRef breaks the scheduleDrive <-> driveHeldTurns callback cycle (the same
  // ref-indirection the end-phrase watch uses). endFailCountRef + connDownAnnouncedRef drive the silent-
  // stall fix: after several failed end-phrase ticks the connection-down cue is spoken once.
  const currentRecordIdRef = useRef<string | null>(null);
  const drivingRef = useRef(false);
  const driveTimerRef = useRef<number | null>(null);
  const driveAttemptRef = useRef(0);
  const driveTickRef = useRef<() => void>(() => {});
  const endFailCountRef = useRef(0);
  const connDownAnnouncedRef = useRef(false);
  // The connectivity listeners installed while Car Mode is open, held so stop()/unmount can remove exactly
  // them: a re-drive of held turns is kicked the instant the network returns or the app is foregrounded.
  const onlineHandlerRef = useRef<(() => void) | null>(null);
  const visibilityHandlerRef = useRef<(() => void) | null>(null);

  // The phase, read synchronously inside the loop/timer callbacks (not a React render). A ref mirror
  // avoids a stale closure so the machine always branches on the CURRENT phase.
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

  // Stop the "thinking" ambient cue if it is playing (v5). Idempotent: safe to call whether or not a cue
  // is running. Called when the reply audio starts, when the mic returns to the owner, and on teardown.
  const stopThinkingCue = useCallback(() => {
    thinkingCueStopRef.current?.();
    thinkingCueStopRef.current = null;
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
      transcribeAttempts: m.transcribeAttempts,
      chunks: m.chunks,
      playMs: m.playMs,
      clipDurationMs: m.clipDurationMs,
      playedToMs: m.playedToMs,
      completed: m.completed,
      playRejected: m.playRejected,
      micReacquiredDuringPlayback: m.micReacquiredDuringPlayback,
      speakingPollCount: m.speakingPollCount,
      viewportInnerHeight: m.viewportInnerHeight,
      visualViewportHeight: m.visualViewportHeight,
      documentClientHeight: m.documentClientHeight,
      footerBottom: m.footerBottom,
      footerVisible: m.footerVisible,
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

  // Play the reply's one audio clip and resolve when it FINISHES ("ended") or is stopped early ("stopped" -
  // an interrupt or End Car Mode). Delegates to the extracted, unit-tested playClip, which assigns the
  // element's src EXACTLY ONCE, so a clip that is still playing can never be clobbered. The stop function
  // playClip hands back is parked in playbackStopRef so the interrupt watch and End Car Mode can end the
  // clip cleanly; playClip clears it (registers a no-op) once the clip is done. The optional lifecycle
  // hooks feed the cut-off-reply telemetry (play-started, play-ended, completed-vs-cutoff).
  const playBlob = useCallback(
    (url: string, hooks?: PlayClipHooks): Promise<PlayOutcome> => {
      const audio = audioRef.current;
      if (audio === null) return Promise.resolve<PlayOutcome>("stopped");
      return playClip(
        audio,
        url,
        (stop) => {
          playbackStopRef.current = stop;
        },
        hooks,
      );
    },
    [],
  );

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

  // Speak a short line through the browser's LOCAL speech synthesis - deliberately NOT the Gateway voice,
  // because the states this is used for (a failure, or an offline/holding state) are exactly when
  // POST /wingman/tts is also unreachable, and the line must still be heard eyes-free. Best-effort: a
  // synthesis hiccup never throws into the turn loop.
  const speakLocal = useCallback((message: string) => {
    try {
      const synth = (window as unknown as { speechSynthesis?: SpeechSynthesis }).speechSynthesis;
      if (synth) {
        synth.cancel();
        synth.speak(new SpeechSynthesisUtterance(message));
      }
    } catch {
      // Local synthesis is a courtesy on top of the on-screen state; never let it throw into the loop.
    }
  }, []);

  // Announce a failure LOUDLY (decision 8): on screen AND spoken locally. Used ONLY for true failures,
  // never the assistant's normal replies and never the calm "saved, will send" holding state (which uses
  // holdMessage + speakLocal directly, not the red error surface).
  const announceError = useCallback((message: string) => {
    setError(message);
    console.log(`[CarMode] FAILURE: ${message}`);
    speakLocal(message);
  }, [speakLocal]);

  // Reload the held-turn list from the durable store into React state, so the page reflects exactly what
  // is saved and waiting after every store mutation. Best-effort: an unreadable store shows nothing held.
  const refreshHeldTurns = useCallback(async () => {
    try {
      setHeldTurns(await listPendingTurns());
    } catch {
      setHeldTurns([]);
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

  // Stop the hands-free end-phrase watch (idempotent). Called at every exit from Listening.
  const stopEndPhraseWatch = useCallback(() => {
    if (endPollRef.current !== null) {
      clearInterval(endPollRef.current);
      endPollRef.current = null;
    }
    endPollBusyRef.current = false;
  }, []);

  // Start the hands-free end-phrase watch: a rolling timer that (via endTickRef, set once the tick below is
  // defined) re-transcribes the captured audio and takes the turn when it ends with the sign-off phrase.
  const startEndPhraseWatch = useCallback(() => {
    stopEndPhraseWatch();
    endPollRef.current = window.setInterval(() => endTickRef.current(), END_PHRASE_POLL_MS);
  }, [stopEndPhraseWatch]);

  // Enter Listening: the microphone is the owner's again. Play the "your turn" cue, clear the on-screen
  // transcript for the fresh turn, and open a clean capture segment (a fresh getUserMedia stream).
  const enterListening = useCallback(async () => {
    // The mic is the owner's again: make sure the "thinking" ambient cue is not still running (v5).
    stopThinkingCue();
    setPhaseBoth("listening");
    setError(null);
    playYourTurnCue();
    // Fresh turn: reset the finickiness probe counter so it counts only THIS turn's attempts.
    transcribeAttemptsRef.current = 0;
    setLastHeard("");
    setCaptureError(null);
    setCaptureState("listening");
    await restartCapture();
    // Anchor the capture-health wall-clock for this listening turn at the moment the mic reopened.
    utteranceStartRef.current = performance.now();
    // Hands-free: watch for the spoken sign-off phrase for the whole of this Listening turn. The touch
    // "Over and out" button remains the instant path; this is what makes it work without a tap.
    startEndPhraseWatch();
  }, [restartCapture, setPhaseBoth, stopThinkingCue, startEndPhraseWatch]);

  // Synthesize `text` through the one good Gateway voice and play the WHOLE reply with the microphone fully
  // RELEASED (v3): nothing touches getUserMedia while the reply plays, because re-opening the mic mid-
  // playback ducks/reroutes the audio on mobile so the owner hears nothing. The reply is cut off ONLY by the
  // touch Stop button. When it finishes on its own the microphone returns to the owner. A synthesis failure
  // is announced loudly and the microphone returns to the owner.
  const speakAndPlay = useCallback(
    async (text: string, signal: AbortSignal) => {
      try {
        const metrics = turnMetricsRef.current;

        // Synthesize the WHOLE reply as ONE clip and play it once. The perf-round first-sentence split was
        // REVERTED here: it synthesized the first sentence and the remainder separately and played them on
        // the SAME reused <audio> element, and on the phone the second clip clobbered the first while it was
        // still playing, so the owner heard only the tail of the reply (the cut-off-reply bug). Correctness
        // first: hearing the WHOLE reply is non-negotiable; the ~1 second streaming shave is not worth
        // cutting off the answer. (Streaming can return LATER, but only behind separate audio elements per
        // chunk plus the on-device audio-event test - never one reused element.) The other performance wins
        // of that round - keep-warm and the fleet-read suppression - are untouched, so Car Mode stays fast.
        const ttsStart = performance.now();
        const clip = await speakCarModeText(text, signal);
        if (signal.aborted) return;
        if (metrics !== null) metrics.ttsMs = performance.now() - ttsStart;

        revokeClip();
        const url = URL.createObjectURL(clip);
        clipUrlRef.current = url;
        const audio = audioRef.current;
        if (audio === null) throw new Error("The audio player was not ready.");

        setPhaseBoth("speaking");
        setCaptureState("speaking");

        // Play the reply. The lifecycle hooks record the cut-off-reply diagnostic: when playback actually
        // starts, how long it stayed audible, and whether it played to its natural end or was cut off. The
        // microphone stays RELEASED throughout (it was stopped in takeTurn before Thinking) - nothing
        // re-opens it here, which is the whole point of v3.
        const played = playBlob(url, {
          onPlayStarted: () => {
            // The reply is now sounding: stop the gentle "thinking" ambient cue so it does not play under
            // the reply (v5 - the ambient fills only the silent working gap, never overlaps the answer).
            stopThinkingCue();
            if (metrics === null) return;
            const nowMs = performance.now();
            metrics.chunks = 1;
            metrics.playStartedAt = nowMs;
            if (metrics.replyReadyAt > 0) {
              metrics.firstAudioMs = nowMs - metrics.replyReadyAt;
              metrics.totalTurnMs = metrics.pauseDetectedAt > 0 ? nowMs - metrics.pauseDetectedAt : 0;
            }
          },
          onPlayEnded: (outcome) => {
            if (metrics === null) return;
            metrics.completed = outcome === "ended";
            metrics.playMs = metrics.playStartedAt > 0 ? performance.now() - metrics.playStartedAt : 0;
            // The cut-off-reply distinction the mission asks for: the SYNTHESIZED clip length (audio.duration,
            // the whole reply the phone received) versus how far playback actually reached (audio.currentTime).
            // A short clipDuration means a truncated SYNTHESIS; a playedTo far below clipDuration means the
            // PLAYBACK was cut. Both are media-time (seconds -> ms), independent of the wall-clock playMs.
            const audioEl = audioRef.current;
            if (audioEl !== null) {
              metrics.clipDurationMs = Number.isFinite(audioEl.duration) ? audioEl.duration * 1000 : 0;
              metrics.playedToMs = Number.isFinite(audioEl.currentTime) ? audioEl.currentTime * 1000 : 0;
            }
            // No rolling "stop" watch runs in v3, so the microphone was never re-opened during playback:
            // these two mic-contention diagnostics read their healthy values (false / 0). They are kept so
            // the /carmode/telemetry dashboard can PROVE the mic stayed released this turn.
            metrics.micReacquiredDuringPlayback = false;
            metrics.speakingPollCount = 0;
            // v5: capture the REAL viewport numbers from THIS phone so the button-cut-off bug is proven from
            // his device, not guessed from desktop. innerHeight (layout viewport), visualViewport.height (the
            // ACTUALLY visible height minus browser toolbars), and documentElement.clientHeight are three
            // independent reads; footerBottom is where the buttons actually end, and footerVisible is the
            // bottom line: is that below or at the visible viewport bottom (buttons on-screen) or past it
            // (cut off). Measured now, while the active footer with its buttons is on screen.
            const vv = typeof window !== "undefined" ? window.visualViewport : null;
            const innerH = typeof window !== "undefined" ? window.innerHeight : 0;
            const clientH =
              typeof document !== "undefined" ? document.documentElement.clientHeight : 0;
            const visualH = vv !== null ? vv.height : 0;
            const footEl =
              typeof document !== "undefined" ? document.querySelector(".car-foot") : null;
            const footBottom = footEl !== null ? footEl.getBoundingClientRect().bottom : 0;
            const viewportH = visualH > 0 ? visualH : innerH;
            metrics.viewportInnerHeight = innerH;
            metrics.visualViewportHeight = visualH;
            metrics.documentClientHeight = clientH;
            metrics.footerBottom = footBottom;
            // +1px tolerance for sub-pixel rounding. Only true when the footer's bottom edge is within view.
            metrics.footerVisible = footBottom > 0 && viewportH > 0 ? footBottom <= viewportH + 1 : false;
            // Post the merged timing record now that the clip's whole lifecycle is known - including a
            // cut-off - so a truncated reply is VISIBLE at /carmode/telemetry (fire-and-forget).
            postTurnTelemetry(metrics);
          },
          onPlayRejected: () => {
            // The reply's play() was refused (mobile autoplay block): record it so the telemetry shows the
            // reply never sounded. onPlayEnded still fires (as "stopped") and posts the record.
            if (metrics !== null) metrics.playRejected = true;
          },
        });

        // v3: the microphone is NOT re-opened here. The reply plays with getUserMedia released; the touch
        // Stop button is the sole interrupt. This is the fix for the "played but heard nothing" bug.
        const how = await played;
        if (how === "stopped" || signal.aborted) return; // interrupted / ended session mid-reply

        // The reply finished on its own: hand the microphone back to the owner.
        await enterListening();
      } catch (err) {
        if (signal.aborted) return;
        announceError(gatewayErrorMessage(err));
        await enterListening();
      }
    },
    [announceError, enterListening, playBlob, postTurnTelemetry, revokeClip, setPhaseBoth, stopThinkingCue],
  );

  // ----- Offline resilience: holding, the audible states, and the background re-drive driver (Phase 4a) -

  // Enter the HOLDING state after a transcribe failure (the brain call never started, so the durable
  // record is brainSent=false and safe to auto-retry). This is NOT a red failure: the owner's speech is
  // SAVED and will send when the connection returns. Say + show the calm held line, refresh the held list,
  // hand the microphone back (the durable audio is safe, so restarting the live capture is fine now), and
  // kick the retry driver so the cadence / online wait begins.
  const enterHolding = useCallback(async () => {
    stopThinkingCue();
    setCaptureState("held");
    setHoldMessage(HOLDING_MESSAGE);
    speakLocal(HOLDING_MESSAGE);
    await refreshHeldTurns();
    await enterListening();
    driveTickRef.current();
  }, [stopThinkingCue, speakLocal, refreshHeldTurns, enterListening]);

  // Re-drive ONE held command-audio record through the whole pipeline (transcribe -> brain -> speak),
  // exactly like a live turn but announced with the "Back online" prefix so the owner knows this is the
  // delayed answer to a request he made earlier (Architect Q3). The durable record is deleted ONLY after
  // the brain call returns a definitive success (the turn is owned server-side); a failure keeps the audio
  // and holds, staying auto-retriable. The brain call carries the record id as the Idempotency-Key (Phase
  // 4b), so even if a prior attempt already reached the brain, this re-drive acts at most once.
  const driveHeldTurn = useCallback(
    async (rec: PendingCarModeTurn) => {
      const recorder = recorderRef.current;
      if (recorder === null) return;
      // The mic must be the owner's and idle to take a recovered turn. Re-check here (not just in the
      // caller) because a live "over and out" can fire during the driver's async gap - the phase would
      // already be "thinking", and two turns must never run on one recorder/audio element.
      if (phaseRef.current !== "listening") return;
      // Reflect the recovered turn taking the turn, synchronously (responsive-first), same as a live end.
      playReadyCue();
      setPhaseBoth("thinking");
      setCaptureState("thinking");
      setHoldMessage(null);
      stopThinkingCue();
      thinkingCueStopRef.current = startThinkingCue();
      const controller = new AbortController();
      abortRef.current = controller;
      try {
        // Stop + release the live microphone before Thinking/Speaking (v3 rule), so the recovered reply
        // plays with getUserMedia released, just like a live reply.
        try {
          if (recorder.isRecording) await recorder.stop();
        } catch {
          // the segment is being torn down; nothing more to do
        }

        // Transcribe unless a prior attempt already cached the command text on the record.
        let command = rec.transcript ?? null;
        if (command === null) {
          const { wav } = await blobToWav16kMono(rec.audio);
          const transcript = (await transcribeCarModeAudio(wav, controller.signal)).trim();
          const parsed = detectPhraseAtEnd(transcript, rec.endPhrase);
          command = (parsed.ended ? parsed.command : transcript).trim();
          try {
            await savePendingTurn({ ...rec, transcript: command });
          } catch {
            // durable update failed; continue from the in-memory command this run
          }
        }
        if (controller.signal.aborted) return;

        if (command.length === 0) {
          // The saved audio transcribed to nothing (noise): drop it rather than nagging the owner later.
          await deletePendingTurn(rec.id);
          await refreshHeldTurns();
          await enterListening();
          return;
        }

        setTranscript(command);
        setLastHeard(command);
        // Record that the brain call is being sent (kept for diagnostics; no longer gates the retry now
        // that Phase 4b's server idempotency makes an already-sent turn safe to auto-retry).
        try {
          await savePendingTurn({ ...rec, transcript: command, brainSent: true });
        } catch {
          // durable update failed; harmless - the brain call below still carries the Idempotency-Key
        }
        turnMetricsRef.current = null; // re-drives are recovery, not part of the live performance telemetry

        // Pass the record id as the Idempotency-Key so a turn that already reached the brain acts at most once.
        const answer = await respond(command, controller.signal, rec.id);
        if (controller.signal.aborted) return;
        // The brain owns the turn: delete the durable record so it can never be re-driven / double-acted.
        await deletePendingTurn(rec.id);
        if (currentRecordIdRef.current === rec.id) currentRecordIdRef.current = null;
        driveAttemptRef.current = 0; // a success resets the backoff so the next held turn retries hard
        await refreshHeldTurns();

        const spoken = answer.spoken.trim();
        setReply(spoken);
        setActions(answer.actions ?? []);
        setPendingConfirmation(Boolean(answer.pendingConfirmation));
        setHistory((prev) => [...prev, { command: command as string, spoken, actions: answer.actions ?? [] }]);
        if (spoken.length === 0) {
          await enterListening();
          return;
        }
        // Speak the recovered answer with the "Back online" prefix so it is clearly the delayed reply.
        await speakAndPlay(RECOVERY_PREFIX + spoken, controller.signal);
      } catch (err) {
        if (controller.signal.aborted) return;
        stopThinkingCue();
        console.log(`[CarMode] re-drive of a held turn failed: ${err instanceof Error ? err.message : String(err)}`);
        // Keep the audio; the turn stays held and auto-retriable (server idempotency makes the retry safe).
        // A money refusal (402) is shown as the shared credits notice and NOT re-kicked immediately (a fast
        // retry cannot conjure credits - the next foreground / online re-drives it); any other failure holds
        // and kicks the retry cadence.
        setHoldMessage(HOLDING_MESSAGE);
        await refreshHeldTurns();
        await enterListening();
        if (err instanceof CreditsError) {
          announceError(err.message);
        } else {
          driveTickRef.current();
        }
      }
    },
    [refreshHeldTurns, enterListening, respond, speakAndPlay, setPhaseBoth, stopThinkingCue, announceError],
  );

  // Schedule the next automatic drive attempt on the retry cadence (hard for the first hour since the
  // oldest auto-eligible turn was captured, then throttled - never stops). No-op when nothing is
  // auto-eligible. Idempotent: a pending timer is not doubled.
  const scheduleDrive = useCallback(() => {
    if (driveTimerRef.current !== null) return;
    void (async () => {
      let all: PendingCarModeTurn[];
      try {
        all = await listPendingTurns();
      } catch {
        return;
      }
      const now = Date.now();
      const auto = all
        .filter((r) => classifyHeldTurn(r, now) === "auto")
        .sort((a, b) => a.createdAt - b.createdAt)[0];
      if (auto === undefined) return; // only ask-owner turns remain; they wait for the owner, not a timer
      const delay = nextTurnRetryDelayMs(auto.createdAt, driveAttemptRef.current, now);
      driveTimerRef.current = window.setTimeout(() => {
        driveTimerRef.current = null;
        driveAttemptRef.current += 1;
        driveTickRef.current();
      }, delay);
    })();
  }, []);

  // Drive held turns when connectivity may have returned (the online event, app foreground, Start, and the
  // cadence timer). Serialized: never while a live turn or another drive is in flight, never while the
  // owner is mid-utterance (so a recovered answer cannot cut him off). Fires the oldest AUTO turn; any
  // ask-owner turns are left surfaced in the UI for the owner's explicit choice.
  const driveHeldTurns = useCallback(async () => {
    if (drivingRef.current) return;
    if (phaseRef.current !== "listening") return; // only when the microphone is the owner's and idle
    const recorder = recorderRef.current;
    // If the owner is speaking RIGHT NOW, defer so the recovered answer cannot cut him off. Gate on the
    // live microphone LEVEL, not the captured byte count: the byte count only ever grows (ambient sound
    // and road noise keep accumulating), so it would wrongly read "he is talking" forever in a car and
    // block the auto-retry indefinitely. The level (the same signal the on-screen meter uses) is low
    // during quiet - road noise is suppressed by the echo-cancel/noise-suppress capture - and spikes only
    // on speech, so it is the right "is he talking now" test.
    if (recorder !== null && recorder.level() >= SPEAKING_LEVEL) {
      scheduleDrive();
      return;
    }
    let all: PendingCarModeTurn[];
    try {
      all = await listPendingTurns();
    } catch {
      return;
    }
    await refreshHeldTurns(); // keep the UI (ask-owner surface, held count) in sync every kick
    const now = Date.now();
    const auto = all
      .filter((r) => classifyHeldTurn(r, now) === "auto")
      .sort((a, b) => a.createdAt - b.createdAt)[0];
    if (auto === undefined) return; // nothing safe to auto-fire
    if (isOffline()) {
      scheduleDrive(); // no point transcribing into a dead network; wait for online / the cadence
      return;
    }
    drivingRef.current = true;
    try {
      await driveHeldTurn(auto);
    } finally {
      drivingRef.current = false;
    }
    scheduleDrive(); // continue draining any remaining auto turns on the cadence
  }, [scheduleDrive, refreshHeldTurns, driveHeldTurn]);
  // Point the scheduler's indirection ref at the latest driveHeldTurns (breaks the scheduleDrive cycle).
  driveTickRef.current = () => void driveHeldTurns();

  // Take the turn: the command (already stripped of "over and out") is answered by the brain and the reply
  // is spoken. Guards against double-entry so a touch tap and a pause probe cannot both fire one turn.
  const takeTurn = useCallback(
    async (command: string, recordId: string | null) => {
      // v5: the screen is switched to Thinking SYNCHRONOUSLY in the endTurn tap handler (responsive-first),
      // so by the time this runs the phase is already "thinking" - guard on that (not "listening") so the
      // turn proceeds, while a second rapid tap is still a no-op (endTurn's own listening-guard blocks it).
      if (phaseRef.current !== "thinking") return;
      // Stop AND release the microphone before Thinking + Speaking: no getUserMedia stays open while the
      // brain works and the reply plays (v3 - the mic must not touch the audio session during playback).
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
          // A forced turn with nothing heard: a canned nudge, no server turn, so no telemetry record. Drop
          // any durable record too - saved noise is not worth holding or retrying.
          turnMetricsRef.current = null;
          if (recordId !== null) {
            try {
              await deletePendingTurn(recordId);
            } catch {
              /* store hiccup; a later resume simply re-drops it */
            }
            if (currentRecordIdRef.current === recordId) currentRecordIdRef.current = null;
            await refreshHeldTurns();
          }
          await speakAndPlay("I didn't catch a request. Go ahead when you're ready.", controller.signal);
          return;
        }
        const brainStart = performance.now();
        // Pass the durable record id as the Idempotency-Key (Phase 4b) so a re-driven turn acts at most once.
        const answer = await respond(trimmed, controller.signal, recordId ?? undefined);
        // The brain owns the turn: delete the durable record so it can never be re-driven / double-acted.
        if (recordId !== null) {
          try {
            await deletePendingTurn(recordId);
          } catch {
            /* store hiccup; the turn still succeeded, so a later resume re-drops it harmlessly */
          }
          if (currentRecordIdRef.current === recordId) currentRecordIdRef.current = null;
          setHoldMessage(null);
          await refreshHeldTurns();
        }
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
        // The brain call failed. With a durable record the turn is HELD and auto-retriable: Phase 4b's
        // server idempotency makes the retry safe (it acts at most once), so there is no "ambiguous,
        // discard-only" case any more. A money refusal (402) shows the shared credits notice and is not
        // re-kicked immediately (a fast retry cannot conjure credits - a later foreground / online
        // re-drives it); any other failure holds and the driver retries on the cadence. Without a durable
        // record (no store) fall back to the loud failure.
        if (recordId !== null && err instanceof CreditsError) {
          setHoldMessage(HOLDING_MESSAGE);
          await refreshHeldTurns();
          await enterListening();
          announceError(err.message);
        } else if (recordId !== null) {
          await enterHolding();
        } else {
          announceError(gatewayErrorMessage(err));
          await enterListening();
        }
      }
    },
    [respond, announceError, enterListening, speakAndPlay, setPhaseBoth, enterHolding, refreshHeldTurns],
  );

  // Transcribe the audio captured so far and take the turn. This is the touch "Over and out" path, the
  // primary (and only) end-of-turn path in v3: the owner explicitly ended his turn, so a failure is
  // announced loudly. If he happened to say "over and out" it is stripped; otherwise the whole transcript is
  // the command (voice over-and-out is deferred - the button is the end path).
  const transcribeAndTake = useCallback(async (prefetched?: { transcript: string; transcodeMs: number; pauseDetectedAt: number; clip: Blob; decodedSeconds: number }) => {
    const rec = recorderRef.current;
    if (rec === null) return;
    // Time the command transcription from the tap so the telemetry shows how long the owner waits between
    // finishing and the brain starting. The voice end-phrase path already transcribed to DETECT the phrase,
    // so it passes its transcript + timings and we skip a second round trip.
    const pauseDetectedAt = prefetched ? prefetched.pauseDetectedAt : performance.now();

    // Acquire the raw command audio for BOTH paths (the voice end-phrase path passes the very clip it
    // transcribed). This is what gets persisted so speech is never lost. On the button path the
    // recorder is STOPPED to get the clip - MediaRecorder's stop event fires only after its final
    // buffered chunk was delivered, so the tail (the last words before the tap) is included with no
    // race at all; a bare snapshot() clipped up to 100ms off the end, and even a flushed snapshot can
    // resolve on an already-queued earlier chunk under load. Stopping here is safe: this turn is over
    // and takeTurn releases the microphone before Thinking anyway (its own stop then no-ops).
    const clip = prefetched ? prefetched.clip : rec.isRecording ? await rec.stop() : rec.snapshot();
    if (!prefetched && clip.size < MIN_CLIP_BYTES) {
      void takeTurn("", null); // nothing captured: a canned nudge, no server turn, no durable record
      return;
    }

    // Persist the command audio to the durable store BEFORE any transcribe, so a connection drop mid-turn
    // can no longer lose the owner's speech (req a, #1427). brainSent=false marks it safe to auto-retry -
    // the brain provably has not started yet.
    const record: PendingCarModeTurn = {
      id: crypto.randomUUID(),
      audio: clip,
      endPhrase: endPhraseRef.current,
      createdAt: Date.now(),
      brainSent: false,
    };
    let persisted = false;
    try {
      await savePendingTurn(record);
      persisted = true;
      currentRecordIdRef.current = record.id;
      void refreshHeldTurns();
    } catch {
      // Durable storage genuinely unavailable (rare private-mode tab): continue best-effort on the live
      // path; a transcribe failure then falls back to the loud error, since we cannot hold what we could
      // not save (no fallback that silently drops the speech - it is announced).
    }

    try {
      let transcript: string;
      let transcodeMs: number;
      let decodedSeconds: number;
      if (prefetched) {
        transcript = prefetched.transcript;
        transcodeMs = prefetched.transcodeMs;
        decodedSeconds = prefetched.decodedSeconds;
      } else {
        setCaptureState("transcribing");
        transcribeAttemptsRef.current += 1;
        // Measure the client-side transcode (phone CPU) separately from the transcribe round trip (network +
        // server), so a real phone turn shows where the time actually goes.
        const transcodeStart = performance.now();
        const decoded = await blobToWav16kMono(clip);
        transcodeMs = performance.now() - transcodeStart;
        decodedSeconds = decoded.decodedSeconds;
        transcript = (await transcribeCarModeAudio(decoded.wav)).trim();
      }
      // Capture-health (issue #863, #1988 Phase 2): Car Mode now reports the same audio-loss deficit every
      // other surface does - the listening wall-clock versus the decoded audio duration of the committed
      // clip. Logged once per committed turn (never on the rolling end-phrase ticks, which would spam).
      if (utteranceStartRef.current > 0) {
        logCaptureHealth("carmode", {
          recordedMs: pauseDetectedAt - utteranceStartRef.current,
          decodedSeconds,
          sourceBytes: clip.size,
        });
      }
      const pauseToTranscribeMs = performance.now() - pauseDetectedAt;
      setLastHeard(transcript);
      setCaptureError(null);
      // Strip the owner's configured sign-off phrase (default "over and out") if he ended with it; on the
      // button path, if he did not say it, the whole transcript is the command.
      const parsed = detectPhraseAtEnd(transcript, endPhraseRef.current);
      const command = parsed.ended ? parsed.command : transcript;
      setCaptureState(`heard: "${transcript}"`);

      // Transcription succeeded. Cache the command on the durable record and cross the brain boundary
      // (brainSent=true) BEFORE the brain call: a failure from here on is ambiguous (held for the owner),
      // while everything up to here stayed auto-retriable.
      if (persisted) {
        try {
          await savePendingTurn({ ...record, transcript: command.trim(), brainSent: true });
        } catch {
          // durable update failed; the record stays brainSent=false. Worst case a later resume re-drives
          // it as a fresh first brain call - still safe, and speech is never lost.
        }
      }

      // Seed the turn metrics the brain + speak steps fill, then take the turn. (v4: the "my turn" cue is
      // no longer fired here - it now fires SYNCHRONOUSLY in the endTurn tap handler, before this async
      // transcode+transcribe work, so the owner hears the acknowledgement the instant he taps, not ~2s
      // later. See endTurn below.)
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
        transcribeAttempts: transcribeAttemptsRef.current,
        chunks: 0,
        playStartedAt: 0,
        playMs: 0,
        clipDurationMs: 0,
        playedToMs: 0,
        completed: false,
        playRejected: false,
        micReacquiredDuringPlayback: false,
        speakingPollCount: 0,
        viewportInnerHeight: 0,
        visualViewportHeight: 0,
        documentClientHeight: 0,
        footerBottom: 0,
        footerVisible: false,
        posted: false,
      };
      void takeTurn(command, persisted ? record.id : null);
    } catch (err) {
      // The transcribe (or transcode) failed while we are already in Thinking (v5): silence the ambient
      // cue. The brain never started, so the durable record is brainSent=false and safe to auto-retry:
      // enter the calm HOLDING state (speech SAVED, not lost) instead of discarding the utterance. Without
      // a durable record (no store) fall back to the loud failure, since there is nothing to hold.
      stopThinkingCue();
      setCaptureError(gatewayErrorMessage(err));
      if (persisted) {
        await enterHolding();
      } else {
        announceError(gatewayErrorMessage(err));
        await enterListening();
      }
    }
  }, [announceError, enterListening, stopThinkingCue, takeTurn, refreshHeldTurns, enterHolding]);

  // The hands-free end-phrase tick: while Listening, re-transcribe the captured audio and, if it now ends
  // with the owner's sign-off phrase, take the turn (reusing the transcript so there is no second round
  // trip). This is the voice equivalent of tapping "Over and out"; the button remains the instant fallback.
  // There is NO self-trigger risk here because the assistant is silent while the owner is talking.
  const endPhraseTick = useCallback(async () => {
    if (endPollBusyRef.current || phaseRef.current !== "listening") return;
    const rec = recorderRef.current;
    if (rec === null) return;
    endPollBusyRef.current = true;
    const startedAt = performance.now();
    try {
      // Flushed snapshot: without forcing MediaRecorder's buffered tail out first, the just-spoken
      // sign-off ("over and out") can be missing from the clip and the phrase is never detected -
      // and when it IS detected, this very clip becomes the persisted command audio, so the tail
      // must be complete here too.
      const clip = await rec.snapshotFlushed();
      if (clip.size < MIN_CLIP_BYTES) return;
      const transcodeStart = performance.now();
      const decoded = await blobToWav16kMono(clip);
      const transcodeMs = performance.now() - transcodeStart;
      const transcript = (await transcribeCarModeAudio(decoded.wav)).trim();
      // This tick reached the Gateway: the connection is alive. Reset the silent-stall counter and clear
      // the connection-down state / announce-once latch, so a recovered connection stops warning.
      endFailCountRef.current = 0;
      if (connDownAnnouncedRef.current) {
        connDownAnnouncedRef.current = false;
        setConnectionDown(false);
      }
      // The owner may have tapped the button (or the session ended) during this ~1s transcribe: only commit
      // while STILL Listening, so the button and the watch can never both take one turn.
      if (phaseRef.current !== "listening") return;
      setLastHeard(transcript);
      if (!detectPhraseAtEnd(transcript, endPhraseRef.current).ended) return; // not done yet - keep listening
      // Heard the sign-off. Mirror the button's end-of-turn exactly: instant "my turn" cue, switch to
      // Thinking, start the gentle working tone, stop the watch, then take the turn with the transcript AND
      // the very clip we already transcribed (transcribeAndTake reuses both, persists the audio, strips the
      // phrase).
      playReadyCue();
      setPhaseBoth("thinking");
      setCaptureState("thinking");
      stopThinkingCue();
      thinkingCueStopRef.current = startThinkingCue();
      stopEndPhraseWatch();
      void transcribeAndTake({ transcript, transcodeMs, pauseDetectedAt: startedAt, clip, decodedSeconds: decoded.decodedSeconds });
    } catch (err) {
      // A failed rolling transcribe must not kill the turn - skip this tick and try again next second. But
      // a dead zone must not silently swallow "over and out" forever: after several consecutive failures,
      // say ONCE that the connection is down (the silent-stall fix). The owner's audio keeps accumulating
      // locally the whole time, so nothing is lost - when the connection returns, his "over and out" lands.
      endFailCountRef.current += 1;
      if (endFailCountRef.current >= END_PHRASE_FAIL_THRESHOLD && !connDownAnnouncedRef.current) {
        connDownAnnouncedRef.current = true;
        setConnectionDown(true);
        speakLocal(CONNECTION_DOWN_MESSAGE);
      }
      console.log(`[CarMode] end-phrase watch skipped a tick: ${err instanceof Error ? err.message : String(err)}`);
    } finally {
      endPollBusyRef.current = false;
    }
  }, [setPhaseBoth, stopThinkingCue, stopEndPhraseWatch, transcribeAndTake, speakLocal]);
  // Keep the interval (started in enterListening, before this tick is defined) pointing at the latest tick.
  endTickRef.current = endPhraseTick;

  // Touch controls (the app is fully usable by touch). "Over and out" ends the turn by transcribing what was
  // captured; "Stop" cuts the reply off instantly. The button is the INSTANT fallback for the hands-free
  // end-phrase watch above.
  const endTurn = useCallback(() => {
    if (phaseRef.current !== "listening") return;
    // The button is taking the turn: stop the hands-free watch so it cannot also fire for this same turn.
    stopEndPhraseWatch();
    // v4: fire the "my turn" acknowledgement cue SYNCHRONOUSLY as the FIRST thing on tap, before any of
    // the async end-of-turn work (mic snapshot -> WAV transcode -> transcribe round trip). That async
    // work takes ~2 seconds, so the cue used to lag the tap badly; firing it here gives an immediate
    // (<150ms) audible "got it". Firing inside the tap gesture also guarantees mobile permits it to sound.
    playReadyCue();
    // v5 (responsive-first): switch the SCREEN to Thinking SYNCHRONOUSLY too, so the orb/status change the
    // instant he taps instead of sitting on Listening for the whole ~2s transcribe+brain+tts. This also
    // makes the takeTurn guard (which now checks for "thinking") pass, and blocks a double-tap (a second
    // endTurn sees phase != "listening" and no-ops).
    setPhaseBoth("thinking");
    setCaptureState("thinking");
    // And fill the silent working gap with the gentle ambient droplet, started HERE inside the tap gesture
    // so mobile lets its audio run; speakAndPlay stops it the instant the reply starts.
    stopThinkingCue();
    thinkingCueStopRef.current = startThinkingCue();
    void transcribeAndTake();
  }, [setPhaseBoth, stopThinkingCue, stopEndPhraseWatch, transcribeAndTake]);

  const interrupt = useCallback(() => {
    if (phaseRef.current !== "speaking") return;
    console.log('[CarMode] "stop" tapped -> silencing and returning the turn');
    haltPlayback();
    setCaptureState("interrupt");
    void enterListening();
  }, [enterListening, haltPlayback]);

  // Speak the curated Help explanation out loud (Help Mode, issue #1441). This is NOT a brain turn: it reads
  // the ONE server-owned help script from the model-free GET /carmode/help and plays it through the same
  // voice path as any reply, so the button is instant, reliable, and costs no credits. It mirrors the
  // end-of-turn handshake (my-turn cue, Thinking synchronously, the gentle working tone) and, when the
  // script finishes, hands the microphone back via speakAndPlay's own enterListening. Assumes Car Mode is
  // already started (the audio was primed in the Start gesture); the public help() below starts it first
  // from idle. A no-op while the brain is Thinking, so it cannot cut a live turn in half; while Speaking it
  // cuts off the current reply and speaks help instead.
  const speakHelp = useCallback(async () => {
    if (phaseRef.current === "thinking") return;
    if (phaseRef.current === "speaking") haltPlayback();
    // Stop the hands-free end-phrase watch so it cannot also take a turn while help plays.
    stopEndPhraseWatch();
    // Release the microphone before Thinking/Speaking (v3 rule: nothing touches getUserMedia during playback).
    const rec = recorderRef.current;
    try {
      if (rec !== null && rec.isRecording) await rec.stop();
    } catch {
      // the segment is being torn down; nothing more to do
    }
    // Responsive-first + the audible handshake: fire the "my turn" cue, switch to Thinking synchronously, and
    // start the gentle working tone. The cue AudioContext was primed in the Start gesture, so these sound.
    playReadyCue();
    setPhaseBoth("thinking");
    setCaptureState("thinking");
    setError(null);
    stopThinkingCue();
    thinkingCueStopRef.current = startThinkingCue();
    const controller = new AbortController();
    abortRef.current = controller;
    try {
      const helpContent = await getCarModeHelp(controller.signal);
      if (controller.signal.aborted) return;
      const spoken = helpContent.spoken;
      if (spoken.length === 0) throw new Error("Help is unavailable right now.");
      turnMetricsRef.current = null; // help is not a brain turn - it carries no performance telemetry
      setTranscript("Help");
      setReply(spoken);
      setActions([]);
      setPendingConfirmation(false);
      // Speak the help through the one good voice; speakAndPlay hands the microphone back when it finishes.
      await speakAndPlay(spoken, controller.signal);
    } catch (err) {
      if (controller.signal.aborted) return;
      stopThinkingCue();
      announceError(gatewayErrorMessage(err));
      await enterListening();
    }
  }, [haltPlayback, stopEndPhraseWatch, setPhaseBoth, stopThinkingCue, speakAndPlay, announceError, enterListening]);

  // The live microphone level for the on-screen meter, polled by the page on an animation frame. Reads the
  // capture stream's AnalyserNode (display only) and returns 0 when the microphone is not capturing.
  const getMicLevel = useCallback(() => recorderRef.current?.level() ?? 0, []);

  // Owner explicitly sends a stale held turn now (a turn older than the auto-fire cap is surfaced for a
  // yes). Safe regardless of whether it was sent to the brain before: Phase 4b's server idempotency (the
  // record id is the Idempotency-Key) makes the re-drive act at most once. Only drives while the microphone
  // is the owner's and no other drive is running.
  const sendHeldTurn = useCallback(
    async (id: string) => {
      if (drivingRef.current || phaseRef.current !== "listening") return;
      let rec: PendingCarModeTurn | null;
      try {
        rec = await getPendingTurn(id);
      } catch {
        return;
      }
      if (rec === null) return; // gone (already delivered or discarded)
      drivingRef.current = true;
      try {
        await driveHeldTurn(rec);
      } finally {
        drivingRef.current = false;
      }
    },
    [driveHeldTurn],
  );

  // Owner explicitly discards a held turn, dropping its saved audio for good. Clears the held banner and
  // the connection-down state when nothing is left waiting.
  const discardHeldTurn = useCallback(
    async (id: string) => {
      try {
        await deletePendingTurn(id);
      } catch {
        // store hiccup; a later resume simply re-drops it
      }
      if (currentRecordIdRef.current === id) currentRecordIdRef.current = null;
      const remaining = await (async () => {
        try {
          return await listPendingTurns();
        } catch {
          return [];
        }
      })();
      setHeldTurns(remaining);
      if (remaining.length === 0) setHoldMessage(null);
    },
    [],
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

    // Warm-on-entry (Car Mode performance round): fire a warmup the INSTANT the owner taps Start, so the
    // hosted model + text-to-speech are hot by the time the first utterance is transcribed - cold-start is
    // the measured dominant latency. Then keep them warm every few minutes WHILE Car Mode is open, so
    // credits are spent only during active use. Best-effort - it never blocks Start.
    void postCarModeWarmup();
    if (keepWarmRef.current !== null) clearInterval(keepWarmRef.current);
    keepWarmRef.current = window.setInterval(() => void postCarModeWarmup(), KEEP_WARM_MS);

    recorderRef.current = new MicRecorder();
    // The reply's sentence chunks are played by playBlob, which sets onended per clip; the speak loop hands
    // the microphone back after the LAST chunk, so no global onended handler is needed here.
    const audio = new Audio();
    audioRef.current = audio;
    // Unlock this element for autoplay WHILE we are still inside the Start tap's user gesture, BEFORE the
    // first await below. The same element is reused for every reply, so once unlocked here, later replies
    // (which play seconds after the tap, past the gesture window) are allowed to sound on mobile.
    unlockAudioElement(audio);
    // Open the ONE shared cue audio channel inside this Start tap gesture, so every cue this session - the
    // "my turn" beep and the "thinking" tone, INCLUDING when the hands-free watch fires them from a timer -
    // sounds cleanly and does not churn the mobile audio session (which was ducking the spoken reply in v7).
    primeCueAudio();

    // Offline resilience (Phase 4a): install the connectivity kickers so a held turn re-drives the instant
    // the network returns or the app comes back to the foreground - not only on the cadence timer. On a
    // kick the backoff resets to hard so recovery is immediate. Held both in refs so stop()/unmount removes
    // exactly these listeners.
    const onOnline = () => {
      driveAttemptRef.current = 0;
      driveTickRef.current();
    };
    const onVisible = () => {
      if (typeof document !== "undefined" && !document.hidden) {
        driveAttemptRef.current = 0;
        driveTickRef.current();
      }
    };
    onlineHandlerRef.current = onOnline;
    visibilityHandlerRef.current = onVisible;
    if (typeof window !== "undefined") window.addEventListener("online", onOnline);
    if (typeof document !== "undefined") document.addEventListener("visibilitychange", onVisible);
    // Resume: surface (and, when the mic settles into Listening, auto-drive) any turns saved on a previous
    // visit that never got delivered. refreshHeldTurns shows them now; driveTickRef drains the safe ones.
    void refreshHeldTurns();
    driveTickRef.current();

    // The turn ends hands-free when the rolling end-phrase watch hears the sign-off phrase, or instantly
    // when the owner taps "Over and out". The AnalyserNode drives the on-screen level meter (getMicLevel).
    await enterListening();
  }, [started, unsupported, enterListening, refreshHeldTurns]);

  // The big "Help" button (Help Mode, issue #1441). From idle it starts Car Mode FIRST - start() primes the
  // cue audio and unlocks the reply <audio> element INSIDE this tap gesture (required or the phone silently
  // blocks the spoken help, the v7/v8 audio lesson) and settles into Listening - then speaks the help. While
  // running it speaks help immediately (speakHelp cuts off any current reply). Guarded against the
  // unsupported browser so it never enters a half state. Defined after start() so its dependency can name it.
  const help = useCallback(async () => {
    if (unsupported) {
      setError("Car Mode needs Chrome or another Chromium browser for hands-free voice.");
      return;
    }
    if (!started) {
      await start();
    }
    await speakHelp();
  }, [unsupported, started, start, speakHelp]);

  const stop = useCallback(() => {
    console.log("[CarMode] stop");
    abortRef.current?.abort();
    // Unblock any in-flight playback so the speak loop unwinds instead of awaiting an ended event that will
    // never fire.
    playbackStopRef.current?.();
    // Silence the "thinking" ambient cue if the session ends mid-turn (v5).
    stopThinkingCue();
    // Stop the hands-free end-phrase watch so its timer does not outlive the session.
    stopEndPhraseWatch();
    // Release the shared cue audio channel opened at Start.
    releaseCueAudio();
    // Offline resilience (Phase 4a): remove the connectivity kickers and cancel the retry cadence timer so
    // nothing drives held turns after the owner leaves Car Mode. The saved AUDIO is deliberately kept in
    // the durable store - it is never lost by ending the session - and re-surfaces next time Car Mode opens.
    if (onlineHandlerRef.current !== null && typeof window !== "undefined") {
      window.removeEventListener("online", onlineHandlerRef.current);
      onlineHandlerRef.current = null;
    }
    if (visibilityHandlerRef.current !== null && typeof document !== "undefined") {
      document.removeEventListener("visibilitychange", visibilityHandlerRef.current);
      visibilityHandlerRef.current = null;
    }
    if (driveTimerRef.current !== null) {
      clearTimeout(driveTimerRef.current);
      driveTimerRef.current = null;
    }
    driveAttemptRef.current = 0;
    connDownAnnouncedRef.current = false;
    endFailCountRef.current = 0;
    setConnectionDown(false);
    setHoldMessage(null);
    if (keepWarmRef.current !== null) {
      clearInterval(keepWarmRef.current);
      keepWarmRef.current = null;
    }
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
  }, [revokeClip, setPhaseBoth, stopThinkingCue, stopEndPhraseWatch]);

  // Tear everything down if the page unmounts mid-session (navigating away from Car Mode).
  useEffect(() => {
    return () => {
      abortRef.current?.abort();
      playbackStopRef.current?.();
      thinkingCueStopRef.current?.();
      if (keepWarmRef.current !== null) clearInterval(keepWarmRef.current);
      // Offline resilience (Phase 4a): remove the connectivity kickers and cancel the retry cadence timer
      // on unmount too (navigating away from Car Mode). The saved audio stays durable.
      if (onlineHandlerRef.current !== null && typeof window !== "undefined")
        window.removeEventListener("online", onlineHandlerRef.current);
      if (visibilityHandlerRef.current !== null && typeof document !== "undefined")
        document.removeEventListener("visibilitychange", visibilityHandlerRef.current);
      if (driveTimerRef.current !== null) clearTimeout(driveTimerRef.current);
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

  // Derive the owner-facing held state from the store mirror. The oldest turn past the staleness cap is
  // surfaced as askOwnerTurn for an explicit send/discard (Phase 4b: staleness is the only reason a held
  // turn waits for the owner, since server idempotency makes any retry safe).
  const now = Date.now();
  const askOwnerRec = heldTurns
    .filter((r) => classifyHeldTurn(r, now) === "ask-owner")
    .sort((a, b) => a.createdAt - b.createdAt)[0];
  const askOwnerTurn = askOwnerRec
    ? { id: askOwnerRec.id, transcript: askOwnerRec.transcript ?? "" }
    : null;

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
    holding: heldTurns.length > 0,
    heldCount: heldTurns.length,
    holdMessage,
    askOwnerTurn,
    connectionDown,
    sendHeldTurn: (id: string) => void sendHeldTurn(id),
    discardHeldTurn: (id: string) => void discardHeldTurn(id),
    getMicLevel,
    endTurn,
    interrupt,
    help: () => void help(),
    start,
    stop,
  };
}
