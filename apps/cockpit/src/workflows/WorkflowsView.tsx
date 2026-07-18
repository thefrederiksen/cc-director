import { useCallback, useEffect, useState } from "react";
import { Link } from "react-router-dom";
import {
  createWorkflow,
  getWorkflows,
  suggestWorkflowId,
  type WorkflowDefinition,
} from "@devthrottle/client-core/workflows/workflowsClient";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import { Button, ErrorBanner, LoadingState } from "../components";

// The Workflows page (issue #1617; rebuilt by the Workflows mission, phase 7): the named ways of
// working this fleet defines - Mission and its siblings, plus every workflow the user's agents author.
//
// The page's job is to LIST and BROWSE. One compact row per workflow; the conduct itself lives on the
// detail page. Authoring is AGENT-driven by design - workflows are markdown + optional helper files
// that agents pull, edit, and push through the cc-devthrottle workflow commands - so "Add workflow"
// deliberately asks only for a name and a summary, creates a DRAFT, and hands you the exact prompt to
// give an agent. No markdown box, no step builder: a form that pretended to be the authoring surface
// would be worse than the one that already exists in every agent.
//
// The definitions come from the GATEWAY, not this bundle: the Gateway is the home, every Director and
// every agent asks it, and the catalog this page shows is the same catalog every session's preamble
// indexes.
export function WorkflowsView() {
  const [workflows, setWorkflows] = useState<WorkflowDefinition[] | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [adding, setAdding] = useState(false);

  const load = useCallback(async (signal?: AbortSignal) => {
    try {
      const fresh = await getWorkflows(signal);
      setWorkflows(fresh);
      setError(null);
    } catch (err) {
      if (signal?.aborted === true) return;
      // No fallback to an empty list: an unreadable catalog says so, it does not render as "you have
      // no workflows".
      setError(gatewayErrorMessage(err));
    }
  }, []);

  useEffect(() => {
    const ctrl = new AbortController();
    void load(ctrl.signal);
    return () => ctrl.abort();
  }, [load]);

  return (
    <div className="page wf">
      <header className="ui-page-header">
        <div className="ui-page-header-text">
          <h1 className="ui-page-title">Workflows</h1>
          <p className="ui-page-subtitle">
            Named ways of working, stored on the Gateway and usable by every agent on every machine.
          </p>
        </div>
        <Button variant="primary" onClick={() => setAdding(true)}>Add workflow</Button>
      </header>

      {error !== null ? (
        <ErrorBanner message={error} onRetry={() => void load()} />
      ) : workflows === null ? (
        <LoadingState message="Loading workflows..." />
      ) : (
        <div className="wf-rows">
          {workflows.map((wf) => (
            <WorkflowRow key={wf.id} workflow={wf} />
          ))}
        </div>
      )}

      {adding ? (
        <AddWorkflowDialog
          onClose={() => setAdding(false)}
          onCreated={() => void load()}
        />
      ) : null}
    </div>
  );
}

function WorkflowRow({ workflow }: { workflow: WorkflowDefinition }) {
  return (
    <Link className="wf-row" to={`/workflows/${encodeURIComponent(workflow.id)}`}>
      <div className="wf-row-main">
        <span className="wf-row-name">{workflow.name}</span>
        <span className={workflow.isBuiltIn === true ? "wf-badge wf-badge-builtin" : "wf-badge wf-badge-custom"}>
          {workflow.isBuiltIn === true ? "Built-in" : "Custom"}
        </span>
        {workflow.hasDraft === true ? <span className="wf-badge wf-badge-draft">Draft waiting</span> : null}
      </div>
      <p className="wf-row-summary">{workflow.summary}</p>
      <div className="wf-row-facts">
        {typeof workflow.version === "number" ? <span>v{workflow.version}</span> : null}
        <span>
          {workflow.steps.length} {workflow.steps.length === 1 ? "step" : "steps"}
        </span>
      </div>
    </Link>
  );
}

// The add dialog: name + one-line summary, nothing else. Submitting creates a DRAFT on the Gateway
// (invisible to the fleet until an agent fleshes it out and publishes) and shows the copyable handoff
// prompt. Errors surface inline and the dialog stays open - the failure is never swallowed.
function AddWorkflowDialog({ onClose, onCreated }: { onClose: () => void; onCreated: () => void }) {
  const [name, setName] = useState("");
  const [summary, setSummary] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [createdId, setCreatedId] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  const id = suggestWorkflowId(name);
  const handoff = createdId === null
    ? ""
    : `Author the '${name.trim()}' workflow (id ${createdId}). Pull it with: cc-devthrottle workflow pull ${createdId} --dir <a working directory> - write its instructions.md (the conduct agents will follow), fill workflow.json (steps, outcome criteria), then push and publish: cc-devthrottle workflow push ${createdId} --dir <the directory> && cc-devthrottle workflow publish ${createdId}`;

  const submit = async () => {
    setBusy(true);
    setError(null);
    try {
      const draft = await createWorkflow({ id, name: name.trim(), summary: summary.trim() });
      setCreatedId(draft.workflowId);
      onCreated();
    } catch (err) {
      setError(gatewayErrorMessage(err));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="wf-dialog-backdrop" role="presentation" onClick={createdId === null ? onClose : undefined}>
      <div
        className="wf-dialog"
        role="dialog"
        aria-modal="true"
        aria-label="Add workflow"
        onClick={(e) => e.stopPropagation()}
      >
        {createdId === null ? (
          <>
            <h2 className="wf-dialog-title">Add workflow</h2>
            <p className="wf-dialog-hint">
              This creates a DRAFT. Your agents do the actual authoring - you will get the exact
              prompt to hand one after this step.
            </p>
            <label className="wf-dialog-field">
              <span>Name</span>
              <input
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="Release train"
                autoFocus
              />
            </label>
            <label className="wf-dialog-field">
              <span>One-line summary</span>
              <input
                value={summary}
                onChange={(e) => setSummary(e.target.value)}
                placeholder="Cut, verify, and announce a release."
              />
            </label>
            {id.length > 1 ? <p className="wf-dialog-id">Id: {id}</p> : null}
            {error !== null ? <p className="wf-dialog-error">{error}</p> : null}
            <div className="wf-dialog-actions">
              <Button variant="secondary" onClick={onClose} disabled={busy}>
                Cancel
              </Button>
              <Button
                variant="primary"
                onClick={() => void submit()}
                disabled={busy || id.length < 2 || summary.trim().length === 0}
              >
                {busy ? "Creating..." : "Create draft"}
              </Button>
            </div>
          </>
        ) : (
          <>
            <h2 className="wf-dialog-title">Draft created</h2>
            <p className="wf-dialog-hint">
              Hand this to any agent - it authors the workflow and publishes it to the whole fleet:
            </p>
            <pre className="wf-dialog-handoff">{handoff}</pre>
            <div className="wf-dialog-actions">
              <Button
                variant="secondary"
                onClick={() => {
                  void navigator.clipboard.writeText(handoff).then(() => setCopied(true));
                }}
              >
                {copied ? "Copied" : "Copy prompt"}
              </Button>
              <Button variant="primary" onClick={onClose}>Done</Button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
