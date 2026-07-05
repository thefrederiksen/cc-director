// The fleet-wide usage-telemetry consent surface of the Gateway (issue #978, epic #967): the typed,
// same-origin client the React Cockpit's Telemetry page reads and toggles. It is the shared-library
// port of the Blazor Cockpit's GatewayClient.GetTelemetryConsentAsync / SetTelemetryConsentAsync.
//
// One setting governs the whole fleet, and it lives on the Gateway, so the page READS it from
// GET /gateway/telemetry-consent and CHANGES it via PUT /gateway/telemetry-consent. The toggle gates
// ONLY the richer usage telemetry; the always-on sign-in / startup auth-floor events are never gated
// by it. The contract carries no token and no user data - only the boolean. Every request is
// root-relative to the Gateway and carries the same Bearer via authHeaders().
import { authHeaders, GatewayError } from "../api/client";

/** The GET/PUT /gateway/telemetry-consent body: whether the fleet has consented to the richer usage
 *  telemetry (default ON). */
export interface TelemetryConsent {
  enabled: boolean;
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

// GET /gateway/telemetry-consent - the fleet-wide richer-usage-telemetry consent, default ON. Throws
// on transport failure so the Telemetry page shows an error banner rather than a dead Gateway
// masquerading as a consented-off fleet.
export async function getTelemetryConsent(signal?: AbortSignal): Promise<TelemetryConsent> {
  const res = await fetch("/gateway/telemetry-consent", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "GET /gateway/telemetry-consent");
  const body = (await res.json()) as Partial<TelemetryConsent> | null;
  return { enabled: Boolean(body?.enabled) };
}

// PUT /gateway/telemetry-consent { enabled } - set the fleet-wide consent. Turning it off stops the
// richer usage events fleet-wide. Returns the post-set value the Gateway echoes back. Throws with the
// server error on failure.
export async function setTelemetryConsent(enabled: boolean, signal?: AbortSignal): Promise<TelemetryConsent> {
  const res = await fetch("/gateway/telemetry-consent", {
    method: "PUT",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ enabled }),
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "PUT /gateway/telemetry-consent");
  const body = (await res.json()) as Partial<TelemetryConsent> | null;
  return { enabled: Boolean(body?.enabled) };
}
