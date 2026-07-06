// The phone-recording / transcript surface of the Gateway (issue #977, epic #967): the typed,
// same-origin client the React Cockpit's Voice Recorder (Transcripts) page reads and writes. It is
// the shared-library port of the Blazor Cockpit's GatewayClient recording methods (GetRecordingsAsync
// / GetTranscriptAsync / DeleteRecordingAsync / PromoteRecordingAsync / UpdateRecordingMetaAsync /
// GetAgentInfoAsync), so the desktop React shell keeps exactly one copy of each /ingest contract.
//
// Recordings are uploaded from the phone and transcribed on the Gateway; they are kept locally and
// are temporary. Every request is root-relative to the Gateway front door (never a Director address)
// and carries the same Bearer via authHeaders(). A user action (delete / promote / save details)
// throws GatewayError carrying the Gateway's own message on a non-2xx.
import { authHeaders, GatewayError } from "../api/client";

/** One row in the Gateway transcripts list: enough to render the card and link to the transcript
 *  text + audio segments. camelCase mirror of the C# RecordingListItem record. */
export interface RecordingListItem {
  recordingId: string;
  title: string;
  startedAt: string;
  /** One of: receiving, incomplete, queued, transcribing, cleaning, transcribed, filed, error. */
  state: string;
  segments: number;
  durationMs: number;
  hasTranscript: boolean;
  transcriptPath?: string | null;
  inVault: boolean;
  subtitle?: string | null;
  summary?: string | null;
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

// GET /ingest/recordings - every local recording/transcript. Throws on transport failure so the page
// surfaces it (no fallback to a misleading empty list).
export async function getRecordings(signal?: AbortSignal): Promise<RecordingListItem[]> {
  const res = await fetch("/ingest/recordings", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "GET /ingest/recordings");
  // The endpoint contract is a JSON array, but a malformed/unexpected body (object, null, string)
  // must never reach the caller as a non-array - TranscriptsView does body.map() and would throw
  // "r.map is not a function". Guard so a non-array degrades to an empty list (issue #1050).
  const body = (await res.json()) as unknown;
  return Array.isArray(body) ? (body as RecordingListItem[]) : [];
}

// GET /ingest/recording/{id}/transcript - the cleaned transcript text; null on 404 (none stored) or a
// transport failure, so the page renders a placeholder instead of a hard error (matches the Blazor
// GetTranscriptAsync which returns null on any non-2xx).
export async function getTranscript(recordingId: string, signal?: AbortSignal): Promise<string | null> {
  try {
    const res = await fetch(`/ingest/recording/${encodeURIComponent(recordingId)}/transcript`, {
      method: "GET",
      headers: { Accept: "text/plain", ...authHeaders() },
      signal,
    });
    if (!res.ok) return null;
    return await res.text();
  } catch {
    return null;
  }
}

// DELETE /ingest/recording/{id} - delete one transient local recording. Throws with the server error.
export async function deleteRecording(recordingId: string, signal?: AbortSignal): Promise<void> {
  const res = await fetch(`/ingest/recording/${encodeURIComponent(recordingId)}`, {
    method: "DELETE",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, `DELETE /ingest/recording/${recordingId}`);
}

// POST /ingest/recording/{id}/promote - copy a recording's transcript + audio into the vault. Throws.
export async function promoteRecording(recordingId: string, signal?: AbortSignal): Promise<void> {
  const res = await fetch(`/ingest/recording/${encodeURIComponent(recordingId)}/promote`, {
    method: "POST",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, `POST /ingest/recording/${recordingId}/promote`);
}

// PATCH /ingest/recording/{id}/meta { title, subtitle, summary } -> the updated record. Any field may
// be null to leave it unchanged (a blank title is ignored server-side so a transcript never loses its
// title). Throws with the server error on failure.
export async function updateRecordingMeta(
  recordingId: string,
  meta: { title?: string | null; subtitle?: string | null; summary?: string | null },
  signal?: AbortSignal,
): Promise<RecordingListItem> {
  const res = await fetch(`/ingest/recording/${encodeURIComponent(recordingId)}/meta`, {
    method: "PATCH",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ title: meta.title, subtitle: meta.subtitle, summary: meta.summary }),
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, `PATCH /ingest/recording/${recordingId}/meta`);
  return (await res.json()) as RecordingListItem;
}

// GET /ingest/agent-info - the copy-paste agent API guide (plain text) so an external agent can
// connect and process these transcripts. Throws with the server error on failure.
export async function getAgentInfo(signal?: AbortSignal): Promise<string> {
  const res = await fetch("/ingest/agent-info", {
    method: "GET",
    headers: { Accept: "text/plain", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "GET /ingest/agent-info");
  return await res.text();
}

// The same-origin URL for one audio segment of a recording, root-relative to the Gateway (never a
// Director address). Used as the <audio> element's src, which cannot carry an Authorization header;
// it authenticates via the cc-gateway-token cookie the shell mirrors at startup (ensureGatewayCookie),
// exactly like the live terminal WebSocket. Segments play in order 0..segments-1.
export function recordingAudioUrl(recordingId: string, segmentIndex: number): string {
  return `/ingest/recording/${encodeURIComponent(recordingId)}/audio/${segmentIndex}`;
}
