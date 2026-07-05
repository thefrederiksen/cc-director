// The Gateway About / diagnostics surface (issue #978, epic #967): the typed, same-origin client the
// React Cockpit's About page reads. It is the shared-library port of the Blazor Cockpit's
// GatewayClient.GetAboutAsync.
//
// The Gateway serves this at GET /gateway/about (the /about path itself is left for the Blazor page
// during migration; the data endpoint is /gateway/about). Read-only. Every request is root-relative to
// the Gateway and carries the same Bearer via authHeaders().
import { authHeaders, GatewayError } from "../api/client";

/** The GET /gateway/about body: what this Gateway is running and what is installed on its box. */
export interface AboutInfo {
  product: string;
  /** Full informational version, e.g. "0.6.15+sha". */
  version: string;
  /** Build date of the running Gateway exe ("yyyy-MM-dd HH:mm:ss"), or null. */
  buildDate?: string | null;
  machineName: string;
  /** The per-user install root on the Gateway box. */
  installRoot: string;
  /** The one front-door URL the Cockpit is reached at, or null when Tailscale is down. */
  cockpitUrl?: string | null;
  /** Installed component id -> version (from installed.json on the Gateway box). */
  installedComponents: Record<string, string>;
  /** The Gateway's current time (ISO 8601 UTC). */
  serverTime: string;
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

// GET /gateway/about - the "what is this Gateway running and what's installed" diagnostics. Throws on
// transport failure so the About page shows an error banner.
export async function getAbout(signal?: AbortSignal): Promise<AboutInfo> {
  const res = await fetch("/gateway/about", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "GET /gateway/about");
  const body = (await res.json()) as Partial<AboutInfo> | null;
  return {
    product: body?.product ?? "",
    version: body?.version ?? "",
    buildDate: body?.buildDate ?? null,
    machineName: body?.machineName ?? "",
    installRoot: body?.installRoot ?? "",
    cockpitUrl: body?.cockpitUrl ?? null,
    installedComponents: body?.installedComponents ?? {},
    serverTime: body?.serverTime ?? "",
  };
}
