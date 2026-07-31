// The fleet + machine surface of the Gateway (issue #975): the typed, same-origin client the React
// Cockpit's Fleet, Directors, and Director-detail pages read. It is the shared-library port of the
// Blazor Cockpit's GatewayClient (fleet/interrupted/rename) and DirectorClient (director settings)
// calls, so the desktop React shell and the mobile shell keep exactly one copy of each contract.
//
// Every request is root-relative to the Gateway front door (never a Director address), the same
// Gateway-only-ingress rule the rest of client-core obeys, and carries the same Bearer via
// authHeaders(). A non-2xx throws GatewayError so the caller surfaces the real reason instead of a
// silently empty list (no fallback that hides the problem).
import { authHeaders, gatewayFetch, GatewayError, POLL_TIMEOUT_MS, type SessionDto } from "../api/client";

// ===== Directors registry (GET /directors) =====

// One machine (Director) in the fleet, projected from GET /directors -> registry.ListDirectors().
// The FULL DirectorDto the registry emits (camelCase on the wire), as the Directors table and the
// Director-detail page consume it. This is the rich shape; the add-session picker's narrower
// DirectorInfo (api/client) stays separate so retargeting one never disturbs the other.
export interface FleetDirector {
  directorId: string;
  pid?: number;
  /** When the Director process started (ISO 8601 UTC). */
  startedAt?: string;
  controlEndpoint?: string;
  machineName?: string;
  user?: string;
  /** The instance's user-editable display name (devthrottle_internal#1176), e.g. "SOREN_NORTH_SLOT_2".
   * Empty/absent when unnamed or from an older Director - fall back to machineName. */
  displayName?: string;
  version?: string;
  schemaVersion?: number;
  /** When the Gateway last heard from this Director (ISO 8601), or null. */
  lastSeen?: string | null;
  tailnetEndpoint?: string | null;
  /** A flagged registration's own reason for advertising no reachable endpoint (issue #324). */
  endpointUnreachableReason?: string | null;
  /** "file" (local filesystem discovery) or "http" (push registration). */
  source?: string;
  /** When the WebSocket UPGRADE (terminal stream) path was last verified, or null. */
  streamVerifiedAt?: string | null;
  /** Set when the terminal stream leg is down while plain HTTP is reachable (cross-machine). */
  streamVerifyError?: string | null;
  /** "ok" or ENDPOINT_STATE_UNREACHABLE_BY_NAME (issue #325), or null on old Directors. */
  advertisedEndpointState?: string | null;
  advertisedEndpointCheckedAt?: string | null;
  advertisedEndpointUnreachableSince?: string | null;
  advertisedEndpointError?: string | null;
}

// The advertised-endpoint state a Director reports when it is alive (heartbeating) but the NAME it
// advertised stopped answering the Gateway's per-heartbeat probe (issue #325) - rendered distinctly
// from a full heartbeat/fan-out loss. Mirrors DirectorDto.EndpointStateUnreachableByName.
export const ENDPOINT_STATE_UNREACHABLE_BY_NAME = "unreachable-by-name";

// GET /directors - the machines registered with this Gateway, as the FULL DirectorDto. Throws
// GatewayError on non-2xx so the Directors page shows the real reason instead of an empty table.
export async function getFleetDirectors(signal?: AbortSignal): Promise<FleetDirector[]> {
  const res = await gatewayFetch("/directors", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) {
    throw new GatewayError(res.status, `GET /directors failed: ${res.status}`);
  }
  // Contract is a JSON array; a non-array body must degrade to [] so the Directors table never
  // throws "x.map is not a function" (same sibling-list guard as getRecordings, issue #1050).
  const body = (await res.json()) as unknown;
  return Array.isArray(body) ? (body as FleetDirector[]) : [];
}

// ===== Roster envelope (GET /sessions?envelope=true) =====

// One machine the Gateway could not reach on the last roster read (the envelope's machineErrors), which
// the Fleet and Directors pages surface as unreachable. It is a statement about the LINK, not about the
// roster: the machine's sessions are still served (dimmed and dated), so this no longer means "its
// sessions are missing".
export interface MachineError {
  directorId?: string;
  machineName?: string;
  error?: string;
}

// The three machine states in the roster envelope (issue #1215; re-based on the tunnel by Epic #1159
// step A). A Director reads as:
//  - "online": its tunnel is up and its last push is current - this is live data.
//  - "wobbly": its tunnel is up but nothing recent has arrived - real data, going stale, machine still
//    there. Its sessions are in the roster, shown dimmed with a "last seen N seconds ago" age.
//  - "offline": its tunnel is down - real data, dated, and the machine cannot be acted on.
//
// OFFLINE NO LONGER MEANS DELETED. It used to: the Gateway dropped an offline machine's sessions from
// the envelope once a grace window expired, which is why the phone's roster blanked. The Gateway now
// serves every session it last knew about, whatever its age, so all three states carry sessions and the
// state decides only how they are RENDERED. A session leaves the roster when its Director says so or
// when the machine passes the Gateway's eviction horizon - never because a display timer ran out.
export const REACHABILITY_ONLINE = "online";
export const REACHABILITY_WOBBLY = "wobbly";
export const REACHABILITY_OFFLINE = "offline";
export type ReachabilityState =
  | typeof REACHABILITY_ONLINE
  | typeof REACHABILITY_WOBBLY
  | typeof REACHABILITY_OFFLINE;

// One Director's reachability presentation in the roster envelope (issue #1215). The Cockpit joins a
// session to its Director by directorId (also stamped on SessionDto.directorId) to decide how to render
// it, so the list changes appearance IN PLACE and never reflows because of a transient miss.
export interface DirectorReachability {
  directorId: string;
  machineName?: string;
  /** The Director's user-editable display name (devthrottle_internal#1176). Empty/absent when unnamed
   * or from an older Gateway - fall back to machineName. */
  displayName?: string;
  /** "online" | "wobbly" | "offline". */
  state: ReachabilityState;
  /** When the Gateway last HEARD this machine - the arrival stamp of its newest push (ISO 8601 UTC), or
   * null if it has never pushed. */
  lastSeenUtc?: string | null;
  /**
   * Seconds since that stamp. A REAL age in every state, including online (where a healthy machine reads
   * a few seconds, not zero - the old Gateway wrote "now" here, which measured when the response was
   * assembled rather than when anything was heard). Null when the machine has never pushed.
   */
  lastSeenAgeSeconds?: number | null;
  /** The last poll's failure reason for wobbly/offline; null while online. */
  error?: string | null;
}

// The envelope shape GET /sessions returns with ?envelope=true: the live sessions, the machines that
// failed this fan-out, AND the per-Director reachability presentation (issue #1215). (Plain GET
// /sessions - api/client listSessions - returns just the array; the Fleet and Directors pages need the
// machineErrors and reachability too, so they ask for the envelope.)
export interface SessionsEnvelope {
  sessions: SessionDto[];
  machineErrors: MachineError[];
  /** Per-Director reachability for the Online / Wobbly / Offline rendering (issue #1215). */
  directors: DirectorReachability[];
}

// GET /sessions?envelope=true - the roster plus the unreachable-machine list and per-Director
// reachability. Throws GatewayError on non-2xx so the page surfaces the failure rather than showing a
// silently empty roster.
export async function getSessionsEnvelope(signal?: AbortSignal): Promise<SessionsEnvelope> {
  // The mobile roster polls this every couple of seconds; cap it so a hung request cannot leave the
  // health signal stuck "good" during an outage (mobile-resilience mission, Phase 4).
  const res = await gatewayFetch("/sessions?envelope=true", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  }, { timeoutMs: POLL_TIMEOUT_MS });
  if (!res.ok) {
    throw new GatewayError(res.status, `GET /sessions?envelope=true failed: ${res.status}`);
  }
  const body = (await res.json()) as Partial<SessionsEnvelope>;
  return {
    sessions: body.sessions ?? [],
    machineErrors: body.machineErrors ?? [],
    directors: body.directors ?? [],
  };
}

// Join a session to its Director's reachability (issue #1215). Returns undefined when the envelope
// carries no reachability for that Director (an older Gateway, or a Director that is fully Online with
// no entry) - the caller then renders the session normally (Online). This is how a session's card
// changes appearance IN PLACE (dimmed while Wobbly) instead of the list reflowing on a transient miss.
export function reachabilityFor(
  directors: DirectorReachability[],
  directorId: string | null | undefined,
): DirectorReachability | undefined {
  if (!directorId) return undefined;
  return directors.find((d) => d.directorId === directorId);
}

// The "last seen N ago" age label for a Wobbly/Offline card. Empty for a missing or non-positive age.
// The age is real in every state now, so the CALLER decides when to show it: every caller renders it
// only on a wobbly/offline row, because "last seen 4s ago" beside a healthy machine is noise.
export function reachabilityLastSeen(ageSeconds: number | null | undefined): string {
  if (ageSeconds === null || ageSeconds === undefined || ageSeconds <= 0) return "";
  if (ageSeconds < 60) return `last seen ${Math.round(ageSeconds)}s ago`;
  if (ageSeconds < 3600) return `last seen ${Math.floor(ageSeconds / 60)}m ago`;
  return `last seen ${Math.floor(ageSeconds / 3600)}h ago`;
}

// PATCH /sessions/{sid} { name } - rename a session; the Gateway routes to the owning Director and
// returns the updated SessionDto (with the new name). Empties/whitespace are the caller's business;
// the Gateway trims and echoes the applied name.
export async function renameSession(sessionId: string, name: string, signal?: AbortSignal): Promise<SessionDto> {
  const sid = encodeURIComponent(sessionId);
  const res = await gatewayFetch(`/sessions/${sid}`, {
    method: "PATCH",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ name }),
    signal,
  });
  if (!res.ok) {
    throw new GatewayError(res.status, `PATCH /sessions/${sessionId} (rename) failed: ${res.status}`);
  }
  return (await res.json()) as SessionDto;
}

// ===== Interrupted sessions (issue #212 W3/W4) =====

// One session lost to an unexpected Director shutdown, from the crash journal a live Director on the
// same machine reports (GET /interrupted). Grouped in the UI by dead Director + pid.
export interface InterruptedSession {
  sessionId: string;
  name?: string | null;
  repoPath?: string;
  agent?: string;
  claudeSessionId?: string | null;
  /** ISO 8601 UTC when the session was created. */
  createdAtUtc?: string;
  deadDirectorId: string;
  deadPid: number;
  machineName?: string;
  user?: string;
  /** ISO 8601 UTC when the owning Director died. */
  diedAtUtc?: string;
  /** The live Director that reported this journal - the routing key ("via") for dismiss/restore. */
  reportedByDirectorId: string;
  /** The Gateway-enriched last wingman read for this session, if known. */
  railLine?: string | null;
  /** The Gateway-enriched "was working on" headline, if known. */
  headline?: string | null;
}

// The result of restoring an interrupted session: whether a continuation session was created and,
// if so, that new session (its sessionId is the jump link).
export interface RestoreInterruptedResult {
  restored: boolean;
  targetSession?: SessionDto | null;
  contextSent?: string | null;
  journalCleaned: boolean;
}

// GET /interrupted - every interrupted session across the fleet, newest death first. Throws
// GatewayError on non-2xx.
export async function getInterrupted(signal?: AbortSignal): Promise<InterruptedSession[]> {
  const res = await gatewayFetch("/interrupted", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) {
    throw new GatewayError(res.status, `GET /interrupted failed: ${res.status}`);
  }
  // Contract is a JSON array; a non-array body must degrade to [] so the Fleet interrupted-cards
  // grouping never throws "x.map is not a function" (sibling-list guard, issue #1050).
  const body = (await res.json()) as unknown;
  return Array.isArray(body) ? (body as InterruptedSession[]) : [];
}

// DELETE /interrupted/{deadDirectorId}/{deadPid}?via={reportedBy} - dismiss a WHOLE crash journal
// (all of its sessions). Routed to the live Director that reported it via the required `via` param.
export async function dismissInterruptedJournal(
  deadDirectorId: string,
  deadPid: number,
  reportedByDirectorId: string,
  signal?: AbortSignal,
): Promise<void> {
  const dir = encodeURIComponent(deadDirectorId);
  const via = encodeURIComponent(reportedByDirectorId);
  const res = await gatewayFetch(`/interrupted/${dir}/${deadPid}?via=${via}`, {
    method: "DELETE",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) {
    throw new GatewayError(res.status, `DELETE interrupted journal failed: ${res.status}`);
  }
}

// DELETE /interrupted/{deadDirectorId}/{deadPid}/sessions/{sessionId}?via={reportedBy} - dismiss ONE
// session from a journal, keeping its siblings in the list.
export async function dismissInterruptedSession(
  deadDirectorId: string,
  deadPid: number,
  sessionId: string,
  reportedByDirectorId: string,
  signal?: AbortSignal,
): Promise<void> {
  const dir = encodeURIComponent(deadDirectorId);
  const sid = encodeURIComponent(sessionId);
  const via = encodeURIComponent(reportedByDirectorId);
  const res = await gatewayFetch(`/interrupted/${dir}/${deadPid}/sessions/${sid}?via=${via}`, {
    method: "DELETE",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) {
    throw new GatewayError(res.status, `DELETE interrupted session failed: ${res.status}`);
  }
}

// POST /interrupted/{deadDirectorId}/{deadPid}/restore { sessionId, via } - create a continuation
// session seeded with this session's surviving turn-brief context and pull the row from the journal.
// Returns the restore result whose targetSession.sessionId is the new session to jump to.
export async function restoreInterrupted(
  deadDirectorId: string,
  deadPid: number,
  sessionId: string,
  reportedByDirectorId: string,
  signal?: AbortSignal,
): Promise<RestoreInterruptedResult> {
  const dir = encodeURIComponent(deadDirectorId);
  const res = await gatewayFetch(`/interrupted/${dir}/${deadPid}/restore`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ sessionId, via: reportedByDirectorId }),
    signal,
  });
  if (!res.ok) {
    throw new GatewayError(res.status, `POST restore interrupted failed: ${res.status}`);
  }
  const body = (await res.json()) as Partial<RestoreInterruptedResult>;
  return {
    restored: Boolean(body.restored),
    targetSession: body.targetSession ?? null,
    contextSent: body.contextSent ?? null,
    journalCleaned: Boolean(body.journalCleaned),
  };
}

// ===== Director settings (GET/PUT /directors/{id}/settings) =====

// The settings body is an OPAQUE, arbitrary JSON object the Director owns - the Gateway forwards it
// verbatim (SessionWsProxyEndpoints). So it is read and written as raw JSON text, exactly like the
// Blazor DirectorClient (GetSettingsAsync/PutSettingsAsync). Routed by DIRECTOR id, not session id.

// GET /directors/{id}/settings - the Director's current settings as the raw JSON text it emits.
export async function getDirectorSettings(directorId: string, signal?: AbortSignal): Promise<string> {
  const id = encodeURIComponent(directorId);
  const res = await gatewayFetch(`/directors/${id}/settings`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) {
    throw new GatewayError(res.status, `GET /directors/${directorId}/settings failed: ${res.status}`);
  }
  return res.text();
}

// PUT /directors/{id}/settings - write the Director's settings from raw JSON text; the Director
// re-applies live. The caller validates the JSON (JSON.parse) before calling, so a malformed edit
// never reaches the wire.
export async function putDirectorSettings(directorId: string, json: string, signal?: AbortSignal): Promise<void> {
  const id = encodeURIComponent(directorId);
  const res = await gatewayFetch(`/directors/${id}/settings`, {
    method: "PUT",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: json,
    signal,
  });
  if (!res.ok) {
    throw new GatewayError(res.status, `PUT /directors/${directorId}/settings failed: ${res.status}`);
  }
}
