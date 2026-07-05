// The named-work-list surface of the Gateway (issue #977, epic #967): the typed, same-origin client
// the React Cockpit's Lists page reads and writes. It is the shared-library port of the Blazor
// Cockpit's GatewayClient work-list methods (GetWorkListsAsync / CreateWorkListAsync /
// AppendWorkListItemAsync / ReorderWorkListItemsAsync / RemoveWorkListItemAsync), so the desktop
// React shell keeps exactly one copy of each /lists contract.
//
// The Cockpit is a CLIENT of the shared Gateway list object (issue #273): every create / append /
// reorder / remove goes through these calls, so the Cockpit never owns a copy or its own ordering.
// Per-item title + flow:* status are resolved separately through the Gateway item-status endpoint
// (api/itemStatus), never a browser-held GitHub token (issue #970).
//
// Every request is root-relative to the Gateway front door (never a Director address), the same
// Gateway-only-ingress rule the rest of client-core obeys, and carries the same Bearer via
// authHeaders(). A user action (create/append/reorder/remove) throws GatewayError carrying the
// Gateway's own message on a non-2xx (e.g. a 409 when a list name is taken) so the page shows the
// real reason inline instead of a bare status code - no fallback that hides the problem.
import { authHeaders, GatewayError } from "../api/client";

// ===== The work-list shapes as they travel over the Gateway REST surface =====
// camelCase mirrors of the C# WorkListDto / WorkListItemRef (CcDirector.Gateway.Contracts). These
// are not in the generated OpenAPI schema (the /lists endpoints return via Results.Json without a
// [Produces] annotation), so they are declared here as narrow local shapes, the same pattern
// schedule/scheduleClient uses for the cron types.

/** A structured reference to one work item in a named list: which source system, its id within that
 *  source, and an optional free-text area label for display. The store never interprets status. */
export interface WorkListItemRef {
  /** The source system: "github" | "devops" | "jira" (github/devops are runnable; jira is displayed only). */
  source: string;
  /** The item id within its source - a string so it holds a Jira key ("CCD-44") as well as "262". */
  id: string;
  /** Optional free-text grouping label (e.g. Gateway / Core / Installer / Cockpit); display only. */
  area?: string | null;
}

/** A named work list: a name + an ordered list of item refs + the single-consumer claim token. It
 *  deliberately carries NO item-status field - order + refs + consumer only (issue #273). */
export interface WorkList {
  name: string;
  items: WorkListItemRef[];
  /** The single active draining consumer's claim token, or null when the list is unclaimed. */
  consumer?: string | null;
}

// Pull the Gateway's own error text out of a non-2xx body so the caller shows the real reason (a 409
// name clash, a 404 list) rather than a bare status. Falls back to the status code when the body is
// not a shape we recognize (it may be a JSON { error } or a plain-text problem detail).
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

// GET /lists -> { lists: [ WorkListDto ] }. Read path: an empty list on a null/absent body. Throws on
// transport failure (the Lists page surfaces it as a banner) so a dead Gateway never looks like an
// empty fleet.
export async function getWorkLists(signal?: AbortSignal): Promise<WorkList[]> {
  const res = await fetch("/lists", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "GET /lists");
  const body = (await res.json()) as { lists?: WorkList[] };
  return body.lists ?? [];
}

// POST /lists { name } - create a named list. Throws on failure (incl. a 409 when the name is taken)
// so the create dialog can show the server's message.
export async function createWorkList(name: string, signal?: AbortSignal): Promise<void> {
  const res = await fetch("/lists", {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ name }),
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "POST /lists");
}

// POST /lists/{name}/items (body one item ref) - append one item to a list. Throws on failure so the
// add-item form can show the server's message.
export async function appendWorkListItem(
  name: string,
  item: WorkListItemRef,
  signal?: AbortSignal,
): Promise<void> {
  const res = await fetch(`/lists/${encodeURIComponent(name)}/items`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify(item),
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, `POST /lists/${name}/items`);
}

// PATCH /lists/{name}/items (body full ordered array) - replace a list's items with a new order. This
// is how a reorder is committed to the shared object (never a local-only reorder). Throws on failure.
export async function reorderWorkListItems(
  name: string,
  items: WorkListItemRef[],
  signal?: AbortSignal,
): Promise<void> {
  const res = await fetch(`/lists/${encodeURIComponent(name)}/items`, {
    method: "PATCH",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify(items),
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, `PATCH /lists/${name}/items`);
}

// DELETE /lists/{name}/items/{source}/{id} - remove the item addressed by source + id. Throws on failure.
export async function removeWorkListItem(
  name: string,
  source: string,
  id: string,
  signal?: AbortSignal,
): Promise<void> {
  const res = await fetch(
    `/lists/${encodeURIComponent(name)}/items/${encodeURIComponent(source)}/${encodeURIComponent(id)}`,
    {
      method: "DELETE",
      headers: { Accept: "application/json", ...authHeaders() },
      signal,
    },
  );
  if (!res.ok) throw await gatewayErrorFrom(res, `DELETE /lists/${name}/items/${source}/${id}`);
}
