import { useCallback, useEffect, useState } from "react";
import { getWorkflows, type WorkflowDefinition } from "@devthrottle/client-core/workflows/workflowsClient";
import { gatewayErrorMessage } from "@devthrottle/client-core/api/client";
import { ErrorBanner, LoadingState } from "../components";

// The Workflows page (issue #1617): the shapes of work this fleet knows how to run.
//
// A workflow is a named, saved definition of how a piece of work gets done by agents - which seats
// exist, which one starts, which one reviews, and where the human is asked. Until now that lived
// implicitly in whichever skill file an agent happened to read, which meant there was no place to look
// to see how the team works. This page is that place.
//
// The definitions come from the GATEWAY, not from this bundle. That is the whole architectural point:
// the Gateway is the home, every Director asks it, and later an administrator sets an organisation's
// workflows once there and every machine picks them up. A page that hardcoded the list would read the
// same today and be a lie tomorrow.
//
// Read-only at this step. Choosing a workflow when starting work, and authoring them here, are later
// steps - so this page deliberately ships no buttons it cannot honour.
export function WorkflowsView() {
  const [workflows, setWorkflows] = useState<WorkflowDefinition[] | null>(null);
  const [error, setError] = useState<string | null>(null);

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
            How work gets done by your agents. A workflow decides which agent starts, which one reviews,
            and where you get asked.
          </p>
        </div>
      </header>

      <p className="wf-intro">
        These are served by the Gateway, so every Director on every machine runs the same set. Editing
        them here is not built yet.
      </p>

      {error !== null ? (
        <ErrorBanner message={error} onRetry={() => void load()} />
      ) : workflows === null ? (
        <LoadingState message="Loading workflows..." />
      ) : (
        <div className="wf-list">
          {workflows.map((wf) => (
            <WorkflowCard key={wf.id} workflow={wf} />
          ))}
        </div>
      )}
    </div>
  );
}

function WorkflowCard({ workflow }: { workflow: WorkflowDefinition }) {
  return (
    <section className="wf-card">
      <div className="wf-card-head">
        <h2 className="wf-name">{workflow.name}</h2>
        <span className="wf-steps-count">
          {workflow.steps.length} {workflow.steps.length === 1 ? "step" : "steps"}
        </span>
      </div>
      <p className="wf-summary">{workflow.summary}</p>

      <dl className="wf-facts">
        <dt>When to use it</dt>
        <dd>{workflow.whenToUse}</dd>
        <dt>You are asked</dt>
        <dd>{workflow.humanCheckpoint}</dd>
      </dl>

      <ol className="wf-steps">
        {workflow.steps.map((step, i) => (
          <li className="wf-step" key={step.name}>
            <div className="wf-step-num" aria-hidden="true">
              {i + 1}
            </div>
            <div className="wf-step-body">
              <div className="wf-step-name">{step.name}</div>
              <p className="wf-step-desc">{step.description}</p>
              <div className="wf-seats">
                <span className="wf-seat">
                  <span className="wf-seat-label">Does it</span>
                  <span className="wf-seat-who">{step.doer}</span>
                </span>
                <span className="wf-seat">
                  <span className="wf-seat-label">Reviews it</span>
                  {/* "No review" is a real statement the workflow is making - show it, do not leave a gap
                      that reads as missing data. */}
                  <span className={step.reviewer === null ? "wf-seat-who wf-seat-none" : "wf-seat-who"}>
                    {step.reviewer ?? "No review"}
                  </span>
                </span>
                <span className="wf-seat wf-seat-done">
                  <span className="wf-seat-label">Done when</span>
                  <span className="wf-seat-who">{step.done}</span>
                </span>
              </div>
            </div>
          </li>
        ))}
      </ol>
    </section>
  );
}
