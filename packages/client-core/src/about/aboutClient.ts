// The Gateway About / diagnostics surface (issue #978, epic #967): the typed, same-origin client the
// React Cockpit's About page reads. It is the shared-library port of the Blazor Cockpit's
// GatewayClient.GetAboutAsync.
//
// The Gateway serves this at GET /gateway/about (the /about path itself is left for the Blazor page
// during migration; the data endpoint is /gateway/about). Read-only. Every request is root-relative to
// the Gateway and carries the same Bearer via authHeaders().
import { authHeaders, GatewayError } from "../api/client";

/**
 * The build identity of one web bundle the Gateway serves, from the build.json its Vite build emits.
 * The bundles carry no semantic version of their own (they ship with the Gateway), so the commit plus
 * the build time IS their version.
 */
export interface BundleStamp {
  /** The short commit the bundle was built from. */
  commit: string;
  /** When the bundle was built (ISO 8601 UTC), or null when the stamp carries no time. */
  buildTime?: string | null;
}

/**
 * The GET /gateway/about body: what the three SERVER-SIDE products are running - this Gateway, the
 * Cockpit bundle it serves, and the mobile app bundle it serves - plus how it is reached.
 *
 * The Director is deliberately absent (owner ruling 2026-07-26): it has its own About box and its own
 * Cockpit screen. The install root, machine name, run mode and installer component manifest went with
 * it - internal detail on the hosted service, and the install root leaked the operating-system user
 * name to every enrolled device.
 */
export interface AboutInfo {
  /** Full informational version of the running Gateway, e.g. "0.6.15+sha". */
  version: string;
  /** Build date of the running Gateway executable ("yyyy-MM-dd HH:mm:ss"), or null. */
  buildDate?: string | null;
  /** The Cockpit bundle this Gateway serves, or null when no built bundle is staged. */
  cockpit?: BundleStamp | null;
  /** The mobile app bundle this Gateway serves, or null when no built bundle is staged. */
  mobile?: BundleStamp | null;
  /** The folded deployment label, rendered verbatim: "Hosted service" or "Self-hosted". */
  deployment: string;
  /**
   * The auto-resolved public base address this Gateway is reached at (no surface path), or null in
   * self-host when Tailscale is down. Manual network addressing was dropped in issue #2022.
   */
  address?: string | null;
  /** The one front-door URL the Cockpit is reached at, or null when Tailscale is down. */
  cockpitUrl?: string | null;
  /**
   * The Gateway's own listen port, or NULL on the hosted service, where callers reach it only through
   * `address` on 443 and the internal port composes with nothing. Gateway-owned: the client renders the
   * row when a port is given and omits it when it is not - it never decides which deployment "should"
   * have one.
   */
  port?: number | null;
  uptimeSeconds: number;
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
    version: body?.version ?? "",
    buildDate: body?.buildDate ?? null,
    cockpit: bundleStamp(body?.cockpit),
    mobile: bundleStamp(body?.mobile),
    deployment: body?.deployment ?? "",
    address: body?.address ?? null,
    cockpitUrl: body?.cockpitUrl ?? null,
    // A MISSING port and a port of 0 are different answers and must not collapse: hosted omits the
    // field on purpose, and the page renders no port row for it. Coercing absence to 0 would print
    // "Port 0", which is a lie about a real listening socket.
    port: typeof body?.port === "number" ? body.port : null,
    uptimeSeconds: Number(body?.uptimeSeconds ?? 0),
    serverTime: body?.serverTime ?? "",
  };
}

// A bundle stamp survives only if it actually names a build. An object with no commit is not a stamp,
// and reporting it as one would show an empty build on the About page instead of saying plainly that
// this Gateway serves no built bundle.
function bundleStamp(value: BundleStamp | null | undefined): BundleStamp | null {
  const commit = value?.commit?.trim() ?? "";
  if (commit.length === 0) return null;
  return { commit, buildTime: value?.buildTime ?? null };
}
