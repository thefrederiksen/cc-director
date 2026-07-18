// The workflow catalog surface of the Gateway (issue #1617): the typed, same-origin client the
// Cockpit's Workflows page reads.
//
// A workflow is a named, saved definition of how a piece of work gets done by agents - which seats
// exist, which seat starts, which seat reviews, and where the human is asked. The Gateway is the home
// for them, so this is a read against the Gateway front door like every other client here; the page
// never reaches a Director.
//
// Read-only on purpose at this step: the Gateway serves a built-in set, and authoring/editing is a
// later step. When that lands, the write calls belong here beside the read.
import { authHeaders, GatewayError } from "../api/client";

/** One step of a workflow: who does it, who reviews it, and what finishing it means. */
export interface WorkflowStep {
  name: string;
  description: string;
  /** The seat that does the work. */
  doer: string;
  /** The seat that reviews it, or null when this step has no separate review seat. */
  reviewer: string | null;
  /** What finishing this step means - the workflow's own definition of done. */
  done: string;
}

/** One workflow: a shape of work the fleet knows how to run. */
export interface WorkflowDefinition {
  id: string;
  name: string;
  summary: string;
  whenToUse: string;
  /** Where the human is asked - the interruption budget this workflow spends. */
  humanCheckpoint: string;
  steps: WorkflowStep[];
  /** ADDITIVE fields from the persisted catalog (Workflows mission). Optional so this client keeps
   *  reading an older Gateway that serves only the legacy shape. */
  /** The published version number this projection reflects. */
  version?: number;
  /** True for the workflows the Gateway ships (mission, standalone, ...). Editable, never deletable. */
  isBuiltIn?: boolean;
  /** True when an unpublished draft exists beside the published version. */
  hasDraft?: boolean;
  /** The canonical content hash of the published version. */
  contentHash?: string;
  /** When the workflow head last changed (UTC, ISO). */
  updatedUtc?: string;
}

/** The response of creating a workflow: the new draft's snapshot (subset this client reads). */
export interface WorkflowDraft {
  workflowId: string;
  version: number;
  status: string;
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

// GET /gateway/workflows - every workflow the Gateway serves. Throws on a non-2xx or a transport failure so
// the page shows an error banner rather than an empty list that reads as "you have no workflows".
export async function getWorkflows(signal?: AbortSignal): Promise<WorkflowDefinition[]> {
  const res = await fetch("/gateway/workflows", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "GET /gateway/workflows");
  const body = (await res.json()) as { workflows?: WorkflowDefinition[] } | null;
  const workflows = body?.workflows;
  if (workflows === undefined) throw new GatewayError(res.status, "GET /gateway/workflows returned no workflows field");
  return workflows;
}

// GET /gateway/workflows/{id} - one workflow's published projection. 404 throws (the detail page
// shows the error, never a blank card pretending the workflow exists).
export async function getWorkflow(id: string, signal?: AbortSignal): Promise<WorkflowDefinition> {
  const res = await fetch(`/gateway/workflows/${encodeURIComponent(id)}`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, `GET /gateway/workflows/${id}`);
  return (await res.json()) as WorkflowDefinition;
}

// GET /gateway/workflows/{id}/instructions - the authoritative conduct, raw markdown. This is the
// same text an agent fetches and follows; the detail page renders it read-only. Pass the version
// from the workflow projection you already hold so the metadata and the conduct are guaranteed to
// be the SAME version - two unpinned fetches can straddle a publish and show a torn read.
export async function getWorkflowInstructions(
  id: string,
  version?: number,
  signal?: AbortSignal,
): Promise<string> {
  const versionQuery = typeof version === "number" ? `?version=${version}` : "";
  const res = await fetch(`/gateway/workflows/${encodeURIComponent(id)}/instructions${versionQuery}`, {
    method: "GET",
    headers: { Accept: "text/markdown" , ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, `GET /gateway/workflows/${id}/instructions`);
  return await res.text();
}

// POST /gateway/workflows - create a workflow as a DRAFT (invisible to the catalog until an agent
// fleshes it out and publishes). The add dialog sends only a name and summary; authoring is
// agent-driven by design, so the write surface here is deliberately this thin.
export async function createWorkflow(
  input: { id: string; name: string; summary: string },
  signal?: AbortSignal,
): Promise<WorkflowDraft> {
  const res = await fetch("/gateway/workflows", {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ ...input, authoredBy: "cockpit:add-dialog" }),
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "POST /gateway/workflows");
  return (await res.json()) as WorkflowDraft;
}

/** A workflow id slug from a display name: "Release Train" -> "release-train". The Gateway enforces
 *  the same shape server-side; this just makes the dialog's default id readable. */
export function suggestWorkflowId(name: string): string {
  return name
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "")
    .slice(0, 64);
}
