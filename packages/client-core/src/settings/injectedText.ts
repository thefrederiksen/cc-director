// The Gateway-owned injected text: the whole of what DevThrottle puts in front of an agent at the start
// of a session, and the user's choice to run their own words instead of ours. Gateway-owned so the
// choice is the same on every machine (they all talk to this one Gateway); each Director downloads and
// caches it and injects it at launch.
//
// Every request is root-relative to the Gateway front door (never a Director address) and carries the
// same Bearer via authHeaders(). A non-2xx throws GatewayError with the Gateway's own message (the
// no-fallback rule) - the page shows that rather than a fabricated state.
import { authHeaders, GatewayError } from "../api/client";

/** The injected-text setting as the Cockpit reads and writes it. */
export interface InjectedText {
  /** Whether the user's own text is live instead of the shipped default. */
  useYours: boolean;
  /** The user's own text, kept even while ours is live so they can switch back. Null until they write one. */
  yours: string | null;
  /** The text DevThrottle ships, always current - shown so the user can read and adopt the default. */
  ours: string;
  /** The square-bracket tokens the renderer substitutes, listed so the page can show what stays editable. */
  placeholders: string[];
}

async function gatewayErrorFrom(res: Response, label: string): Promise<GatewayError> {
  let detail = `${res.status}`;
  try {
    const text = await res.text();
    if (text.length > 0) {
      try {
        const body = JSON.parse(text) as { error?: string; detail?: string };
        detail = body.error ?? body.detail ?? text;
      } catch {
        detail = text;
      }
    }
  } catch {
    /* body unreadable - keep the status code */
  }
  return new GatewayError(res.status, `${label} failed: ${detail}`);
}

// The Gateway speaks snake_case here to match the config.json keys; map it to the camelCase the app uses.
interface InjectedTextWire {
  use_yours?: boolean;
  yours?: string | null;
  ours?: string;
  placeholders?: string[];
}

function fromWire(body: InjectedTextWire | null): InjectedText {
  const b = body ?? {};
  return {
    useYours: Boolean(b.use_yours),
    yours: typeof b.yours === "string" ? b.yours : null,
    ours: typeof b.ours === "string" ? b.ours : "",
    placeholders: Array.isArray(b.placeholders) ? b.placeholders.map(String) : [],
  };
}

// GET /gateway/injected-text - the current setting plus the shipped default and the placeholder tokens.
export async function getInjectedText(signal?: AbortSignal): Promise<InjectedText> {
  const res = await fetch("/gateway/injected-text", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "GET /gateway/injected-text");
  return fromWire((await res.json()) as InjectedTextWire);
}

// PUT /gateway/injected-text { use_yours, yours } - set whose text is live and the user's own text.
// The Gateway validates the template and rejects an unrenderable one with a plain-English message, which
// surfaces as a GatewayError. `yours` may be "" (the user's right to inject nothing) but not omitted when
// use_yours is true. Returns the applied setting.
export async function setInjectedText(
  useYours: boolean,
  yours: string | null,
  signal?: AbortSignal,
): Promise<InjectedText> {
  const res = await fetch("/gateway/injected-text", {
    method: "PUT",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ use_yours: useYours, yours }),
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "PUT /gateway/injected-text");
  return fromWire((await res.json()) as InjectedTextWire);
}
