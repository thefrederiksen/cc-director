// The cockpit Assistant's Gateway call (fleet assistant build). The Assistant is the desk surface of
// the SAME Gateway brain Car Mode drives: one loop, one tool set, one server-side conversation store,
// reached at its own front door POST /assistant/turn so the reply arrives in the desk speech style
// (a few full sentences) rather than the car style (one or two).
//
// Reuse, do not rebuild: speech-to-text is the shared POST /wingman/transcribe and read-aloud is the
// shared POST /wingman/tts, both already wrapped in fleetbrain/brainApi.ts - the Assistant imports
// those, and this file adds only the /assistant/turn door. Every call fails LOUD and specific.
import { authHeaders, creditsErrorFrom, gatewayFetch, GatewayError } from "../api/client";
import type { BrainTurnResult } from "../fleetbrain/brainApi";

/**
 * Run one turn of the fleet brain through the Assistant's desk door (POST /assistant/turn). The typed
 * or transcribed question goes up, the brain calls fleet tools server-side, and the final reply comes
 * back with the actions it took. Conversation context is kept server-side keyed by this device, so
 * multi-turn references resolve without the browser sending any history. A 402 is the shared credits
 * error; any other non-2xx throws a specific GatewayError so the failure is shown, never silent.
 */
export async function assistantTurn(text: string, idempotencyKey?: string, signal?: AbortSignal): Promise<BrainTurnResult> {
  const idempotencyHeader: Record<string, string> = idempotencyKey ? { "Idempotency-Key": idempotencyKey } : {};
  const res = await gatewayFetch("/assistant/turn", {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...idempotencyHeader, ...authHeaders() },
    body: JSON.stringify({ text }),
    signal,
  });
  if (!res.ok) {
    const body = (await res.json().catch(() => ({}))) as { error?: string };
    if (res.status === 402) throw creditsErrorFrom(body);
    throw new GatewayError(res.status, body.error ?? `Assistant turn failed: ${res.status}`);
  }
  // Codex review finding 7: a 2xx that does not carry the turn contract is a PROTOCOL failure and
  // must throw specifically - synthesizing defaults would render a blank assistant answer and hide a
  // broken Gateway. The brain never returns an empty spoken string, so empty is malformed too.
  let body: Partial<BrainTurnResult>;
  try {
    body = (await res.json()) as Partial<BrainTurnResult>;
  } catch {
    throw new GatewayError(res.status, "The Assistant turn response was not JSON - Gateway and cockpit are out of step. Redeploy them together.");
  }
  const spoken = typeof body.spoken === "string" ? body.spoken.trim() : "";
  if (typeof body.turnId !== "string" || body.turnId.length === 0 || spoken.length === 0 || !Array.isArray(body.actions)) {
    throw new GatewayError(res.status, "The Assistant turn response was missing turnId, spoken, or actions - Gateway and cockpit are out of step. Redeploy them together.");
  }
  return {
    turnId: body.turnId,
    spoken,
    actions: body.actions,
    pendingConfirmation: Boolean(body.pendingConfirmation),
    timing: body.timing ?? null,
  };
}
