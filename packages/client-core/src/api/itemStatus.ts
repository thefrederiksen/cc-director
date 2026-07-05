// Work-item title + status for the Cockpit Lists view (issue #275), resolved through the Gateway so
// the browser holds NO secret (issue #970). The Blazor Cockpit called GitHub itself with a bearer
// token; the React Cockpit is a browser single-page application, so the resolve moved onto the
// Gateway. This calls it same-origin through a root-relative path - the GitHub token never reaches
// the browser (it stays on the Gateway host).
import { authHeaders, GatewayError } from "./client";

// The per-item badge, derived live from the github item's flow:* label. These strings are the wire
// contract with the Gateway's ItemStatusEndpoint (kept in step with GatewayWorkItemStatus.ToWire):
//   queued      - no flow label / flow:ready-dev / flow:in-progress / a non-github source
//   running     - flow:ready-qa or the transient flow:qa-failed (a loop is on it)
//   done        - flow:done
//   needs-human - flow:needs-human
//   failed      - flow:failed
//   unknown     - could not be derived (GitHub unreachable / no token) - shown explicitly, never as
//                 a wrong "queued" (the no-fallback rule)
export type WorkItemStatus =
  | "queued"
  | "running"
  | "done"
  | "needs-human"
  | "failed"
  | "unknown";

// The GitHub-derived view of one work-list item: its display title plus the flow-derived status. For
// a non-github item only status (queued) is meaningful; title is null and the row shows the bare id.
export interface WorkItemInfo {
  title: string | null;
  status: WorkItemStatus;
  detail: string | null;
}

// GET /gateway/lists/item-status?source={source}&id={id} - resolve one work-list item ref. Never
// carries a secret: the response is only { title, status, detail }, and the request authenticates
// with the same-origin device key the rest of the client uses.
export async function resolveItemStatus(
  source: string,
  id: string,
  signal?: AbortSignal,
): Promise<WorkItemInfo> {
  const q = `source=${encodeURIComponent(source)}&id=${encodeURIComponent(id)}`;
  const res = await fetch(`/gateway/lists/item-status?${q}`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) {
    throw new GatewayError(res.status, `GET item-status failed: ${res.status}`);
  }
  return (await res.json()) as WorkItemInfo;
}
