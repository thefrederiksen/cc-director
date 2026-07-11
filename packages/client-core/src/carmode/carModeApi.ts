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

/** The brain's answer for one turn (POST /carmode/turn). `spoken` is what the assistant says out loud;
 *  `actions` is what it did this turn (empty for a pure question); `pendingConfirmation` is true when the
 *  assistant is holding a destructive action and is waiting for a spoken "confirm" next turn. */
export interface CarModeTurnResult {
  spoken: string;
  actions: CarModeAction[];
  pendingConfirmation: boolean;
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
    spoken: (body.spoken ?? "").trim(),
    actions: Array.isArray(body.actions) ? body.actions : [],
    pendingConfirmation: Boolean(body.pendingConfirmation),
  };
}
