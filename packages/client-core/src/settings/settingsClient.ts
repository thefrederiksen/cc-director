// The Gateway "machine / connection" settings surface (issue #1025, epic #967): the typed, same-origin
// client the React Cockpit's Settings page reads and drives for everything that is NOT the AI tab (that
// tab reuses ../api/ai). It is the shared-library port of the pieces of the retired Blazor
// wwwroot/pages/settings.html "This machine" tab plus the #497 OpenAI-key panel.
//
// Every request is root-relative to the Gateway front door (never a Director address) and carries the
// same Bearer via authHeaders(). No secret is ever read into the page: the OpenAI key panel reads only
// the vault's KEY NAMES (to show set/not-set) and writes the key write-only (security rule DT-05). A
// user action throws GatewayError carrying the Gateway's own message on a non-2xx (the no-fallback rule).
import { authHeaders, GatewayError } from "../api/client";

/** The fleet network addressing mode: Tailscale front door (default) or the machine's real LAN IP. */
export type AddressingMode = "tailscale" | "lan";

/** The Gateway process + Cockpit block of GET /gateway/settings. */
export interface GatewayCockpit {
  port: number;
  up: boolean;
  url: string | null;
}

/** The per-user autostart Run-key state. `supported` is false on a host with no tray hook. */
export interface AutostartState {
  supported: boolean;
  /** The effective state when supported; null when the host cannot report it. */
  enabled: boolean | null;
}

/** The GET /gateway/settings snapshot the "This machine" tab renders. Read-only diagnostics plus the
 *  current values of the mutable machine settings (addressing mode, autostart, training capture). */
export interface GatewaySettings {
  version: string;
  state: string;
  port: number;
  uptimeSeconds: number;
  directors: number;
  mode: string;
  addressingMode: AddressingMode;
  cockpit: GatewayCockpit;
  autostart: AutostartState;
  wingmanTrainingCapture: boolean;
  telemetryConsent: boolean;
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

async function getJson<T>(path: string, label: string, signal?: AbortSignal): Promise<T> {
  const res = await fetch(path, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, label);
  return (await res.json()) as T;
}

async function putJson<T>(path: string, label: string, body: unknown, signal?: AbortSignal): Promise<T> {
  const res = await fetch(path, {
    method: "PUT",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify(body),
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, label);
  return (await res.json()) as T;
}

// GET /gateway/settings - the whole "This machine" snapshot. Throws on transport failure so the page
// shows an error banner rather than a fabricated empty state.
export async function getGatewaySettings(signal?: AbortSignal): Promise<GatewaySettings> {
  const body = (await getJson<Partial<GatewaySettings> & { cockpit?: Partial<GatewayCockpit>; autostart?: Partial<AutostartState> }>(
    "/gateway/settings",
    "GET /gateway/settings",
    signal,
  )) ?? {};
  return {
    version: body.version ?? "",
    state: body.state ?? "",
    port: Number(body.port ?? 0),
    uptimeSeconds: Number(body.uptimeSeconds ?? 0),
    directors: Number(body.directors ?? 0),
    mode: body.mode ?? "unknown",
    addressingMode: body.addressingMode === "lan" ? "lan" : "tailscale",
    cockpit: {
      port: Number(body.cockpit?.port ?? 0),
      up: Boolean(body.cockpit?.up),
      url: body.cockpit?.url ?? null,
    },
    autostart: {
      supported: Boolean(body.autostart?.supported),
      enabled: body.autostart?.enabled ?? null,
    },
    wingmanTrainingCapture: Boolean(body.wingmanTrainingCapture),
    telemetryConsent: Boolean(body.telemetryConsent),
  };
}

// PUT /gateway/addressing-mode { mode } - set the fleet network addressing mode. Applies to this host's
// own Directors on their next restart (a per-machine, read-at-start setting). Returns the applied mode.
export async function setAddressingMode(mode: AddressingMode, signal?: AbortSignal): Promise<AddressingMode> {
  const body = await putJson<{ mode?: string }>("/gateway/addressing-mode", "PUT /gateway/addressing-mode", { mode }, signal);
  return body.mode === "lan" ? "lan" : "tailscale";
}

// PUT /gateway/autostart { enabled } - toggle the per-user autostart Run-key. A host with no tray hook
// answers { supported: false }; the caller keeps the checkbox disabled in that case.
export async function setAutostart(enabled: boolean, signal?: AbortSignal): Promise<AutostartState> {
  const body = await putJson<Partial<AutostartState>>("/gateway/autostart", "PUT /gateway/autostart", { enabled }, signal);
  return { supported: Boolean(body.supported), enabled: body.enabled ?? null };
}

// PUT /gateway/wingman/training-capture { enabled } - capture wingman training data. Takes effect
// immediately (read at capture time), no restart. Returns the applied state.
export async function setTrainingCapture(enabled: boolean, signal?: AbortSignal): Promise<boolean> {
  const body = await putJson<{ enabled?: boolean }>("/gateway/wingman/training-capture", "PUT /gateway/wingman/training-capture", { enabled }, signal);
  return Boolean(body.enabled);
}

/** The vault key name the OpenAI provider key is stored under (matches OpenAiKeyResolver.KeyName). */
export const OPENAI_KEY_NAME = "OPENAI_API_KEY";

// GET /vault/keys - the vault's key NAMES only (never values). Used to show whether a given key is set
// without ever reading the secret into the page (security rule DT-05). Throws on transport failure.
export async function getVaultKeyNames(signal?: AbortSignal): Promise<string[]> {
  const body = await getJson<{ names?: string[] }>("/vault/keys", "GET /vault/keys", signal);
  return Array.isArray(body.names) ? body.names : [];
}

// PUT /vault/keys/{name} { value } - store a key write-only. The value only ever travels browser ->
// Gateway; it is never read back into the page. Throws on a non-2xx with the Gateway's message.
export async function setVaultKey(name: string, value: string, signal?: AbortSignal): Promise<void> {
  await putJson<{ set?: boolean }>(`/vault/keys/${encodeURIComponent(name)}`, `PUT /vault/keys/${name}`, { value }, signal);
}
