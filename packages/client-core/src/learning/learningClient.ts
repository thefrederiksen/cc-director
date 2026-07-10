// The product Q&A ("Ask Wingman") surface of the Gateway (issue #977, epic #967): the typed,
// same-origin client the React Cockpit's Learning page uses. It is the shared-library port of the
// Blazor Cockpit's GatewayClient WingmanAskDevThrottleAsync, so the desktop React shell keeps exactly
// one copy of the /wingman/ask-devthrottle contract.
//
// The page asks a free-text question ABOUT THE PRODUCT and shows the Wingman's answer. The Cockpit
// talks only to the Gateway here - never a Director. Like the Blazor method, this NEVER throws on a
// handled error: the result carries `error` instead, so the page shows an explicit message rather
// than failing silently (the no-fallback rule).
import { authHeaders, gatewayErrorMessage } from "../api/client";

/** The Wingman's answer, mirroring the C# WingmanVoiceResult: `spoken` is the speakable answer on
 *  success; `error` is a human-readable message when the ask could not complete (null on success). */
export interface WingmanAskResult {
  spoken: string | null;
  error: string | null;
}

// POST /wingman/ask-devthrottle { text } - ask the product-docs Q&A path. Returns the answer or, on a
// handled failure, an { error } message - it never throws (a network exception is caught and returned
// as an error too), matching the Blazor WingmanAskDevThrottleAsync contract.
export async function askDevThrottle(text: string, signal?: AbortSignal): Promise<WingmanAskResult> {
  try {
    const res = await fetch("/wingman/ask-devthrottle", {
      method: "POST",
      headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
      body: JSON.stringify({ text }),
      signal,
    });
    const body = (await res.json().catch(() => ({}))) as { spoken?: string | null; error?: string | null };
    if (!res.ok) {
      return { spoken: null, error: body.error ?? `gateway returned ${res.status}` };
    }
    return { spoken: body.spoken ?? null, error: body.error ?? null };
  } catch (err) {
    // A network exception (the Gateway is unreachable) must not surface the browser's raw
    // "Failed to fetch" - collapse it to the shared friendly transport line (issue #1028).
    return { spoken: null, error: gatewayErrorMessage(err) };
  }
}

/** The display outcome of an Ask-Wingman request for the Learning page: exactly one of `answer` or
 *  `error` is set. `answer` is the speakable text to show on success; `error` is a human-readable
 *  message to show in the page's error banner. */
export interface WingmanAskOutcome {
  answer: string | null;
  error: string | null;
}

// Run one Ask-Wingman request and resolve it to a display outcome for the Learning page (issue #1250).
// This NEVER throws: askDevThrottle already returns a handled { error } for a reachable-but-failing
// Gateway, but if the request itself throws (an unexpected transport error) it is caught here and
// surfaced as a message. That guarantee is what keeps the Learning page from ever failing silently -
// the page had a try/finally with no catch, so a thrown ask showed nothing at all (the no-fallback
// rule). Extracting the mapping here also makes the outcome logic unit-testable without a browser.
export async function runWingmanAsk(text: string, signal?: AbortSignal): Promise<WingmanAskOutcome> {
  try {
    const result = await askDevThrottle(text, signal);
    if (result.error !== null && result.error.length > 0) {
      return { answer: null, error: result.error };
    }
    if (result.spoken === null || result.spoken.trim().length === 0) {
      return { answer: null, error: "Wingman returned an empty answer." };
    }
    return { answer: result.spoken, error: null };
  } catch (err) {
    return { answer: null, error: gatewayErrorMessage(err) };
  }
}
