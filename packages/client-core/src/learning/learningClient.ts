// The product Q&A ("Ask Wingman") surface of the Gateway (issue #977, epic #967): the typed,
// same-origin client the React Cockpit's Learning page uses. It is the shared-library port of the
// Blazor Cockpit's GatewayClient WingmanAskDevThrottleAsync, so the desktop React shell keeps exactly
// one copy of the /wingman/ask-devthrottle contract.
//
// The page asks a free-text question ABOUT THE PRODUCT and shows the Wingman's answer. The Cockpit
// talks only to the Gateway here - never a Director. Like the Blazor method, this NEVER throws on a
// handled error: the result carries `error` instead, so the page shows an explicit message rather
// than failing silently (the no-fallback rule).
import { authHeaders } from "../api/client";

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
    return { spoken: null, error: err instanceof Error ? err.message : "Ask Wingman failed" };
  }
}
