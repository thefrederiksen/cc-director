// Car Mode Gateway calls (Car Mode mission, decision 2 + decision 6). The browser stays thin: it
// captures audio, gets a transcript, hands the transcript to the brain, and speaks the reply. All
// three steps are same-origin against the Gateway front door with the enrolled per-device key, exactly
// like the rest of client.ts. No model key and no fleet logic ever live in the browser.
//
// Reuse, do not rebuild (decision 6): transcription is POST /wingman/transcribe (the one Gateway
// speech-to-text front door), the voice is POST /wingman/tts (the one good voice, raw audio bytes).
// The brain is the new POST /carmode/turn (built in Phase 2). Every call fails LOUD and specific -
// no silent stall, no guess (decision 8).
import { authHeaders, creditsErrorFrom, GatewayError } from "../api/client";

/**
 * Transcribe a captured utterance through the single Gateway speech-to-text front door
 * (POST /wingman/transcribe). The audio is sent as multipart form-data with an "audio" file - the
 * exact contract GatewayWingmanVoiceEndpoint expects - and the Gateway returns the dictionary-corrected
 * transcript (the user's words; transcript integrity, CodingStyle s16). A 402 is surfaced as the shared
 * credits error; any other non-2xx throws a specific GatewayError so the caller can speak the failure.
 */
export async function transcribeCarModeAudio(wav: Blob, signal?: AbortSignal): Promise<string> {
  const form = new FormData();
  form.append("audio", wav, "carmode.wav");
  const res = await fetch("/wingman/transcribe", {
    method: "POST",
    headers: { ...authHeaders() },
    body: form,
    signal,
  });
  if (!res.ok) {
    const body = (await res.json().catch(() => ({}))) as { error?: string };
    if (res.status === 402) throw creditsErrorFrom(body);
    throw new GatewayError(res.status, body.error ?? `Transcription failed: ${res.status}`);
  }
  const body = (await res.json().catch(() => ({}))) as { transcript?: string };
  return (body.transcript ?? "").trim();
}

/**
 * Synthesize speech for a reply through the single good voice (POST /wingman/tts) and return the raw
 * audio bytes as a Blob the page plays in an <audio> element (decision 3: never base64). The voice
 * defaults to the owner's configured text-to-speech voice server-side, so the body is just the text.
 * A 402 is the shared credits error; any other non-2xx throws a specific GatewayError.
 */
export async function speakCarModeText(text: string, signal?: AbortSignal): Promise<Blob> {
  const res = await fetch("/wingman/tts", {
    method: "POST",
    headers: { "Content-Type": "application/json", ...authHeaders() },
    body: JSON.stringify({ text }),
    signal,
  });
  if (!res.ok) {
    const body = (await res.json().catch(() => ({}))) as { error?: string };
    if (res.status === 402) throw creditsErrorFrom(body);
    throw new GatewayError(res.status, body.error ?? `Text-to-speech failed: ${res.status}`);
  }
  return res.blob();
}

/** One action the brain reports having taken during a turn (Phase 2+), so the page can show it on
 *  screen alongside the spoken reply. Display-only; the action already happened server-side. */
export interface CarModeAction {
  /** A short machine label for the tool that ran, e.g. "count_sessions" or "message_session". */
  tool: string;
  /** A one-line, human-readable summary of what the tool did, e.g. "Messaged Local Files - Manager". */
  summary: string;
}

/** The per-stage SERVER timing the brain measured for one turn (performance round): every hosted-model
 *  round trip and every fleet/roster read, plus the whole-turn server wall-clock. Milliseconds. The browser
 *  echoes this back merged with its own client stamps as one telemetry record. */
export interface CarModeServerTiming {
  totalMs: number;
  modelCallCount: number;
  modelMsTotal: number;
  modelMs: number[];
  fleetReadCount: number;
  fleetReadMsTotal: number;
  rounds: number;
}

/** The brain's answer for one turn (POST /carmode/turn). `spoken` is what the assistant says out loud;
 *  `actions` is what it did this turn (empty for a pure question); `pendingConfirmation` is true when the
 *  assistant is holding a destructive action and is waiting for a spoken "confirm" next turn. `turnId` and
 *  `timing` feed the telemetry record the browser posts back. */
export interface CarModeTurnResult {
  turnId: string;
  spoken: string;
  actions: CarModeAction[];
  pendingConfirmation: boolean;
  timing: CarModeServerTiming | null;
}

/**
 * Run one turn of the fleet brain (POST /carmode/turn, built in Phase 2). The transcript of the owner's
 * command goes up, the brain calls fleet tools server-side, and a final spoken message comes back. The
 * caller speaks `spoken`. Conversation context is kept server-side keyed by the device, so multi-turn
 * references ("the latest one") resolve without the browser sending any history. A 402 is the shared
 * credits error; any other non-2xx throws a specific GatewayError so the failure is spoken, never silent.
 */
export async function carModeTurn(text: string, signal?: AbortSignal): Promise<CarModeTurnResult> {
  const res = await fetch("/carmode/turn", {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ text }),
    signal,
  });
  if (!res.ok) {
    const body = (await res.json().catch(() => ({}))) as { error?: string };
    if (res.status === 402) throw creditsErrorFrom(body);
    throw new GatewayError(res.status, body.error ?? `Car Mode turn failed: ${res.status}`);
  }
  const body = (await res.json().catch(() => ({}))) as Partial<CarModeTurnResult>;
  return {
    turnId: typeof body.turnId === "string" ? body.turnId : "",
    spoken: (body.spoken ?? "").trim(),
    actions: Array.isArray(body.actions) ? body.actions : [],
    pendingConfirmation: Boolean(body.pendingConfirmation),
    timing: body.timing ?? null,
  };
}

/** One merged per-turn timing record the browser posts to the Gateway telemetry store (performance round):
 *  the client stamps it measured, plus the server timing it received in the turn response, plus small
 *  non-text turn facts (character counts, action count). The server fills the received-at time, the device
 *  hash, and the Gateway build from its own side. No command or reply text is ever included. */
export interface CarModeTelemetryPost {
  turnId: string;
  pauseToTranscribeMs: number;
  transcodeMs: number;
  brainMs: number;
  ttsMs: number;
  firstAudioMs: number;
  totalTurnMs: number;
  /** How many pause/forced transcribe probes ran this turn before the turn was taken - the "over and out"
   *  finickiness measure. 1 means it landed on the first try; a higher count means the phrase kept being
   *  missed and the owner had to keep trying. */
  transcribeAttempts: number;
  /** How many audio clips the reply played (1 after the first-sentence split was reverted). A future
   *  streaming regression that clobbers the reply would show as a value other than 1 here. */
  chunks: number;
  /** How long the reply clip was actually audible (play-started to play-ended / cut off), milliseconds. */
  playMs: number;
  /** The synthesized reply clip's media length (audio.duration), milliseconds: the whole reply the phone
   *  received. A value far short of what replyChars implies flags a TRUNCATED SYNTHESIS. */
  clipDurationMs: number;
  /** How far INTO the clip playback reached at end/cutoff (audio.currentTime), milliseconds. A value far
   *  below clipDurationMs flags a PLAYBACK cut-off (as opposed to a short synthesis). */
  playedToMs: number;
  /** True when the reply clip played fully to its natural end; false when it was cut off (interrupt / End
   *  Car Mode). A cut-off reply - the bug this telemetry makes visible - reads as false. */
  completed: boolean;
  /** True when the reply's play() was REJECTED - the mobile autoplay block (a play() not tied to a live
   *  user gesture is refused), so the reply never sounded. The unlock-on-Start fix drives this to false. */
  playRejected: boolean;
  /** True when the rolling-"stop" watch re-opened the microphone WHILE the reply was playing (the current
   *  behavior). The mic-contention hypothesis: on mobile this ducks/interrupts the reply. */
  micReacquiredDuringPlayback: boolean;
  /** How many rolling-"stop" transcriptions ran during this reply (each re-reads the open mic). */
  speakingPollCount: number;
  /** window.innerHeight on the phone at telemetry time - the layout viewport height (the tall,
   *  toolbar-hidden height on mobile). */
  viewportInnerHeight: number;
  /** window.visualViewport.height - the ACTUALLY visible height (minus the browser toolbars / keyboard), or
   *  0 if the API is unavailable. This is the height the fixed Car Mode screen must match, and dvh proved
   *  unreliable at tracking it in the owner's Android PWA (why v5 pins the container to this number). */
  visualViewportHeight: number;
  /** document.documentElement.clientHeight - a third, independent viewport read to cross-check the other two. */
  documentClientHeight: number;
  /** The .car-foot element's getBoundingClientRect().bottom in pixels: where the primary buttons actually end. */
  footerBottom: number;
  /** footerBottom <= the visible viewport height: TRUE only when the buttons are ON-SCREEN. The direct,
   *  from-the-phone proof of whether the button-cut-off bug is fixed - false means still cut off. */
  footerVisible: boolean;
  serverTotalMs: number;
  modelCallCount: number;
  modelMsTotal: number;
  modelMs: number[];
  fleetReadCount: number;
  fleetReadMsTotal: number;
  rounds: number;
  commandChars: number;
  replyChars: number;
  actionsCount: number;
  pendingConfirmation: boolean;
}

/**
 * Post one turn's merged timing record to the Gateway telemetry store (POST /carmode/telemetry). Best-effort
 * observability: it never throws into the turn loop and never blocks the spoken reply - a failed post is
 * logged and swallowed, because losing a timing record must never disrupt the drive. Same-origin, with the
 * enrolled per-device key like every other Car Mode call.
 */
export async function postCarModeTelemetry(record: CarModeTelemetryPost): Promise<void> {
  try {
    await fetch("/carmode/telemetry", {
      method: "POST",
      headers: { "Content-Type": "application/json", ...authHeaders() },
      body: JSON.stringify(record),
      keepalive: true,
    });
  } catch (err) {
    console.log(`[CarMode] telemetry post failed (ignored): ${String(err)}`);
  }
}

/**
 * Warm the Car Mode hosted model + text-to-speech provider (POST /carmode/warmup). The page calls this the
 * instant the owner taps Start (so the providers are hot before the first utterance is transcribed) and
 * every few minutes while Car Mode is open, so cold-start - the measured dominant latency - is paid before
 * the drive, not during it. The Gateway gates it on the keep-warm config (default on for Car Mode) and runs
 * the actual warmup in the background. Best-effort: it never throws and never blocks the turn loop.
 */
export async function postCarModeWarmup(): Promise<void> {
  try {
    await fetch("/carmode/warmup", { method: "POST", headers: { ...authHeaders() }, keepalive: true });
  } catch (err) {
    console.log(`[CarMode] warmup ping failed (ignored): ${String(err)}`);
  }
}
