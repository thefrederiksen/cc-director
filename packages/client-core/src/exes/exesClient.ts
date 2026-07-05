// The local-builds / running-Directors surface of the Gateway (issue #977, epic #967): the typed,
// same-origin client the React Cockpit's Exes page reads and drives. It is the shared-library port of
// the Blazor Cockpit's GatewayClient methods (GetExesAsync / KillDirectorAsync / BuildStartSlotAsync
// / DeleteSlotAsync), so the desktop React shell keeps exactly one copy of each /exes contract.
//
// The page lists the Directors running on THIS computer and the 1-4 build slots, and offers Kill /
// Build & start / Delete. Every request is root-relative to the Gateway front door (never a Director
// address) and carries the same Bearer via authHeaders(). A user action throws GatewayError carrying
// the Gateway's own message (the build-output tail for a failed build) on a non-2xx.
import { authHeaders, GatewayError } from "../api/client";

/** One session under a running Director, as the /exes/list payload projects it. */
export interface ExesSession {
  sessionId: string;
  name?: string | null;
  agent?: string | null;
  activityState?: string | null;
  statusColor?: string | null;
  repoPath?: string | null;
}

/** One running Director process on this computer. slot is 1-4 when its exe resolves to a build slot. */
export interface ExesDirector {
  directorId: string;
  pid: number;
  slot?: number | null;
  exePath: string;
  controlEndpoint: string;
  directorUrl?: string | null;
  version?: string | null;
  startedAt?: string | null;
  source?: string | null;
  sessionError?: string | null;
  sessions: ExesSession[];
}

/** The running-Director side of a build slot when a local Director's exe resolves to this slot file. */
export interface ExesSlotRunning {
  pid: number;
  directorId: string;
}

/** One build slot (1-4): whether its exe exists on disk, its size/build time, and whether it is running. */
export interface ExesSlot {
  slot: number;
  exists: boolean;
  exePath: string;
  lastBuiltUtc?: string | null;
  sizeBytes: number;
  running?: ExesSlotRunning | null;
}

/** The GET /exes/list payload: local Directors + build-slot status on this machine. repoRoot is empty
 *  when the Gateway does not run from inside the cc-director repo (which disables slot management). */
export interface ExesList {
  machineName: string;
  repoRoot: string;
  directors: ExesDirector[];
  slots: ExesSlot[];
}

/** The POST /exes/slots/{n}/build-start success body. */
export interface BuildStartResult {
  built: boolean;
  started: boolean;
  slot: number;
  pid: number;
  buildTail?: string | null;
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

// GET /exes/list - local Directors + build-slot status. Throws on transport failure so the Exes page
// surfaces it as an error banner (no fallback empty list).
export async function getExes(signal?: AbortSignal): Promise<ExesList> {
  const res = await fetch("/exes/list", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "GET /exes/list");
  const body = (await res.json()) as Partial<ExesList> | null;
  return {
    machineName: body?.machineName ?? "",
    repoRoot: body?.repoRoot ?? "",
    directors: body?.directors ?? [],
    slots: body?.slots ?? [],
  };
}

// DELETE /directors/{id} { force: true } - kill a Director and all its sessions. Throws with the
// server error on failure.
export async function killDirector(directorId: string, signal?: AbortSignal): Promise<void> {
  const res = await fetch(`/directors/${encodeURIComponent(directorId)}`, {
    method: "DELETE",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ force: true }),
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, `DELETE /directors/${directorId}`);
}

// POST /exes/slots/{n}/build-start - build a slot then launch it. Throws with the server's detail
// (the build output tail) on failure so the page can show it.
export async function buildStartSlot(slot: number, signal?: AbortSignal): Promise<BuildStartResult> {
  const res = await fetch(`/exes/slots/${slot}/build-start`, {
    method: "POST",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, `POST /exes/slots/${slot}/build-start`);
  return (await res.json()) as BuildStartResult;
}

// DELETE /exes/slots/{n} - delete a slot's built exe. Throws with the server error on failure.
export async function deleteSlot(slot: number, signal?: AbortSignal): Promise<void> {
  const res = await fetch(`/exes/slots/${slot}`, {
    method: "DELETE",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, `DELETE /exes/slots/${slot}`);
}
