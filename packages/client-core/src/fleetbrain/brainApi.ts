// The FLEET BRAIN's browser-side calls - the pieces every surface that talks to the brain shares.
//
// The brain is one server-side tool-calling loop with one conversation store, reached through its own front
// door per surface. The Assistant (POST /assistant/turn, in assistant/assistantApi.ts) is the surface that
// exists; this file holds what is common to any of them: read the reply aloud, and keep the providers warm.
//
// It was called carModeApi.ts and lived in a carmode/ directory, because Car Mode was the first surface to
// drive the brain. Car Mode has been removed from the product; the brain, its stores, and these two calls
// were always shared with the Assistant and stay. The name is now what the thing actually is.
//
// The browser stays thin: it hands text to the brain and speaks the reply. Both steps are same-origin against
// the Gateway front door with the enrolled per-device key, exactly like the rest of client.ts. No model key
// and no fleet logic ever live in the browser.
//
// These route through gatewayFetch (NOT a raw fetch) so every contact feeds the ONE app-wide
// connection-health signal (connection/health.ts): a thrown fetch or a front-proxy 502/503/504 flips the
// shared "bad connection" state, and any answered request clears it.
import { authHeaders, creditsErrorFrom, gatewayFetch, GatewayError } from "../api/client";

/**
 * Synthesize speech for a reply through the single good voice (POST /wingman/tts) and return the raw audio
 * bytes as a Blob the page plays in an <audio> element (never base64). The voice defaults to the account's
 * configured text-to-speech voice server-side, so the body is just the text. A 402 is the shared credits
 * error; any other non-2xx throws a specific GatewayError.
 */
export async function speakText(text: string, signal?: AbortSignal): Promise<Blob> {
  const res = await gatewayFetch("/wingman/tts", {
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

/** One action the brain reports having taken during a turn, so the page can show it on screen alongside the
 *  spoken reply. Display-only; the action already happened server-side. */
export interface BrainAction {
  /** A short machine label for the tool that ran, e.g. "count_sessions" or "message_session". */
  tool: string;
  /** A one-line, human-readable summary of what the tool did, e.g. "Messaged Local Files - Manager". */
  summary: string;
}

/** The per-stage SERVER timing the brain measured for one turn: every hosted-model round trip and every
 *  fleet/roster read, plus the whole-turn server wall-clock. Milliseconds. */
export interface BrainServerTiming {
  totalMs: number;
  modelCallCount: number;
  modelMsTotal: number;
  modelMs: number[];
  fleetReadCount: number;
  fleetReadMsTotal: number;
  rounds: number;
}

/** The brain's answer for one turn. `spoken` is what it says out loud; `actions` is what it did this turn
 *  (empty for a pure question); `pendingConfirmation` is true when it is holding a destructive action and is
 *  waiting for a confirmation on the next turn. */
export interface BrainTurnResult {
  turnId: string;
  spoken: string;
  actions: BrainAction[];
  pendingConfirmation: boolean;
  timing: BrainServerTiming | null;
}

/**
 * Warm the hosted model + text-to-speech provider (POST /brain/warmup). A surface calls this the instant it
 * opens - so the providers are hot before the first utterance - and every few minutes while it stays open, so
 * cold start (the measured dominant latency) is paid before the conversation rather than during it. The
 * Gateway gates it on the keep-warm configuration and runs the actual warmup in the background.
 * Best-effort: it never throws and never blocks a turn.
 */
export async function postBrainWarmup(): Promise<void> {
  try {
    await fetch("/brain/warmup", { method: "POST", headers: { ...authHeaders() }, keepalive: true });
  } catch (err) {
    console.log(`[FleetBrain] warmup ping failed (ignored): ${String(err)}`);
  }
}
