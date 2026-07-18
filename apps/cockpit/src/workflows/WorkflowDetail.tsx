import { useCallback, useEffect, useState } from "react";
import { Link, useParams } from "react-router-dom";
import {
  getWorkflow,
  getWorkflowInstructions,
  type WorkflowDefinition,
} from "@devthrottle/client-core/workflows/workflowsClient";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import { markdownToHtml } from "@devthrottle/client-core/history/historyMarkdown";
import { ErrorBanner, LoadingState } from "../components";

// One workflow, in full (Workflows mission, phase 7). The list row answered "what exists"; this page
// answers "what does it actually say": the metadata and step summary up top (the machine-readable
// SHAPE), then the instruction markdown rendered read-only - the AUTHORITATIVE conduct, the exact
// text an agent fetches with `cc-devthrottle workflow instructions <id>` and follows. Rendered
// through the same sanitized renderer the file viewer trusts; read-only on purpose, because
// authoring is agent-driven and a half-editable page would lie about where edits really happen.
export function WorkflowDetail() {
  const { id } = useParams<{ id: string }>();
  const [workflow, setWorkflow] = useState<WorkflowDefinition | null>(null);
  const [instructions, setInstructions] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(
    async (signal?: AbortSignal) => {
      if (id === undefined) return;
      try {
        const [wf, md] = await Promise.all([
          getWorkflow(id, signal),
          getWorkflowInstructions(id, signal),
        ]);
        setWorkflow(wf);
        setInstructions(md);
        setError(null);
      } catch (err) {
        if (signal?.aborted === true) return;
        setError(gatewayErrorMessage(err));
      }
    },
    [id],
  );

  useEffect(() => {
    const ctrl = new AbortController();
    void load(ctrl.signal);
    return () => ctrl.abort();
  }, [load]);

  return (
    <div className="page wf">
      <header className="ui-page-header">
        <div className="ui-page-header-text">
          <p className="wf-crumb">
            <Link to="/workflows">Workflows</Link>
          </p>
          <h1 className="ui-page-title">{workflow?.name ?? id}</h1>
          {workflow ? <p className="ui-page-subtitle">{workflow.summary}</p> : null}
        </div>
      </header>

      {error !== null ? (
        <ErrorBanner message={error} onRetry={() => void load()} />
      ) : workflow === null || instructions === null ? (
        <LoadingState message="Loading workflow..." />
      ) : (
        <>
          <div className="wf-detail-facts">
            <span className={workflow.isBuiltIn === true ? "wf-badge wf-badge-builtin" : "wf-badge wf-badge-custom"}>
              {workflow.isBuiltIn === true ? "Built-in" : "Custom"}
            </span>
            {typeof workflow.version === "number" ? <span className="wf-badge">v{workflow.version}</span> : null}
            {workflow.hasDraft === true ? <span className="wf-badge wf-badge-draft">Draft waiting</span> : null}
          </div>

          <dl className="wf-facts">
            <dt>When to use it</dt>
            <dd>{workflow.whenToUse}</dd>
            <dt>You are asked</dt>
            <dd>{workflow.humanCheckpoint}</dd>
          </dl>

          <ol className="wf-detail-steps">
            {workflow.steps.map((step, i) => (
              <li key={step.name}>
                <span className="wf-detail-step-num">{i + 1}.</span> <strong>{step.name}</strong>{" "}
                - {step.doer}
                {step.reviewer !== null ? `, reviewed by ${step.reviewer}` : ", no review"}; done when{" "}
                {step.done}
              </li>
            ))}
          </ol>

          <section className="wf-conduct">
            <h2 className="wf-conduct-title">The conduct</h2>
            <p className="wf-conduct-hint">
              This is the exact text an agent fetches and follows:{" "}
              <code>cc-devthrottle workflow instructions {workflow.id}</code>
            </p>
            <div
              className="wf-conduct-body"
              dangerouslySetInnerHTML={{ __html: markdownToHtml(instructions) }}
            />
          </section>
        </>
      )}
    </div>
  );
}
