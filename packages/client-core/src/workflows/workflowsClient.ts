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
