// The Gateway "machine / connection" settings surface (issue #1025, epic #967): the typed, same-origin
// client the React Cockpit's Settings page reads and drives for everything that is NOT the AI tab (that
// tab reuses ../api/ai). It is the shared-library port of the pieces of the retired Blazor
// wwwroot/pages/settings.html "This machine" tab.
//
// Every request is root-relative to the Gateway front door (never a Director address) and carries the
// same Bearer via authHeaders(). A user action throws GatewayError carrying the Gateway's own message on
// a non-2xx (the no-fallback rule).
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
  // Snooze Length mission: the per-user default snooze length in minutes (one value across every device,
  // because they all talk to this one Gateway). Default 60. Always one of snoozePresets.
  snoozeDefaultMinutes: number;
  // The lengths every Snooze menu offers, ascending. Shipped as [15, 60, 240, 480] until the user edits
  // them. Gateway-owned like the default, so the same lengths appear on desktop, phone, and Cockpit.
  snoozePresets: number[];
  // The most lengths the list may hold, so the Settings page can disable "Add a length" when full.
  snoozeMaxPresets: number;
  // The display time zone (IANA id) the private dashboards' hourly charts read local hours in. Auto-
  // defaults to the Gateway machine's own zone when unset; the owner can override it here.
  timeZone: string;
  // What "automatic" resolves to: the Gateway machine's own zone. Lets the page show it and offer a reset.
  timeZoneMachineDefault: string;
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
    snoozeDefaultMinutes: Number(body.snoozeDefaultMinutes ?? 60),
    snoozePresets: Array.isArray(body.snoozePresets)
      ? body.snoozePresets.map(Number)
      : [15, 60, 240, 480],
    snoozeMaxPresets: Number(body.snoozeMaxPresets ?? 5),
    timeZone: typeof body.timeZone === "string" && body.timeZone.length > 0 ? body.timeZone : "UTC",
    timeZoneMachineDefault:
      typeof body.timeZoneMachineDefault === "string" && body.timeZoneMachineDefault.length > 0
        ? body.timeZoneMachineDefault
        : "UTC",
  };
}

// PUT /gateway/time-zone { timeZone } - set the display time zone (an IANA id) the private dashboards read
// local hours in. Read at render time, so a change applies on the next refresh with no restart. Returns
// the applied id.
export async function setTimeZone(timeZone: string, signal?: AbortSignal): Promise<string> {
  const body = await putJson<{ timeZone?: string }>("/gateway/time-zone", "PUT /gateway/time-zone", { timeZone }, signal);
  return typeof body.timeZone === "string" && body.timeZone.length > 0 ? body.timeZone : timeZone;
}

// PUT /gateway/snooze-default { minutes } - set the per-user default snooze length (Snooze Length
// mission). Read at snooze time, so a change applies to the next snooze with no restart, and it is the
// same value on every device (all talk to this one Gateway). Returns the applied minutes.
export async function setSnoozeDefaultMinutes(minutes: number, signal?: AbortSignal): Promise<number> {
  const body = await putJson<{ minutes?: number }>("/gateway/snooze-default", "PUT /gateway/snooze-default", { minutes }, signal);
  return Number(body.minutes ?? minutes);
}

// The snooze lengths and which of them is the default, as one value. They travel together because they
// have an invariant between them: the default must be one of the lengths.
export interface SnoozePresets {
  presets: number[];
  defaultMinutes: number;
  maxPresets: number;
}

// PUT /gateway/snooze-presets { presets, defaultMinutes } - set the snooze lengths every Snooze menu
// offers and which one the plain Snooze click uses. Written in ONE call so the list and its default can
// never disagree. Read at snooze time, so a change applies to the next snooze with no restart, and it is
// the same on every device (all talk to this one Gateway). Returns the applied list, ascending.
export async function setSnoozePresets(
  presets: number[],
  defaultMinutes: number,
  signal?: AbortSignal,
): Promise<SnoozePresets> {
  const body = await putJson<Partial<SnoozePresets>>(
    "/gateway/snooze-presets",
    "PUT /gateway/snooze-presets",
    { presets, defaultMinutes },
    signal,
  );
  return {
    presets: Array.isArray(body.presets) ? body.presets.map(Number) : presets,
    defaultMinutes: Number(body.defaultMinutes ?? defaultMinutes),
    maxPresets: Number(body.maxPresets ?? 5),
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
