// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup } from "@testing-library/react";
import type { WorkflowDefinition } from "@devthrottle/client-core/workflows/workflowsClient";

// Rendered proof for the register's preview + clone actions (owner ask, 2026-07-24). The Gateway
// behavior (clone semantics, conduct reads) is proven server-side; what this pins is that the
// register actually PAINTS the two actions on every row, that Preview opens a popup showing the
// REAL fetched conduct pinned to the row's version, and that Clone asks first and then calls the
// client with the suggested id and navigates to the clone. The Gateway client is mocked so the view
// is driven without a running Gateway.

const {
  getWorkflows,
  getWorkflowRuns,
  getWorkflowInstructions,
  cloneWorkflow,
  setWorkflowEnabled,
  createWorkflow,
  suggestWorkflowId,
  navigateSpy,
} = vi.hoisted(() => ({
  getWorkflows: vi.fn(),
  getWorkflowRuns: vi.fn(),
  getWorkflowInstructions: vi.fn(),
  cloneWorkflow: vi.fn(),
  setWorkflowEnabled: vi.fn(),
  createWorkflow: vi.fn(),
  suggestWorkflowId: vi.fn(() => "suggested-id"),
  navigateSpy: vi.fn(),
}));

vi.mock("@devthrottle/client-core/workflows/workflowsClient", () => ({
  getWorkflows,
  getWorkflowRuns,
  getWorkflowInstructions,
  cloneWorkflow,
  setWorkflowEnabled,
  createWorkflow,
  suggestWorkflowId,
}));

vi.mock("react-router-dom", () => ({
  Link: ({ children }: { children: React.ReactNode }) => <a>{children}</a>,
  useNavigate: () => navigateSpy,
}));

import { WorkflowsView } from "./WorkflowsView";

const MISSION: WorkflowDefinition = {
  id: "mission",
  name: "Mission",
  summary: "An Architect settles the design.",
  whenToUse: "Big work.",
  humanCheckpoint: "Once, at the report.",
  steps: [
    { name: "Settle the design", description: "d", doer: "Architect", reviewer: null, done: "written" },
    { name: "Build", description: "d", doer: "Worker", reviewer: "Manager", done: "merged" },
  ],
  version: 5,
  isBuiltIn: true,
  enabled: true,
};

describe("WorkflowsView register actions", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    getWorkflows.mockResolvedValue([MISSION]);
    getWorkflowRuns.mockResolvedValue([]);
    getWorkflowInstructions.mockResolvedValue("# How a mission runs\n\nThe conduct body.");
    cloneWorkflow.mockResolvedValue({ ...MISSION, id: "mission-copy", isBuiltIn: false, editable: true });
  });
  afterEach(cleanup);

  it("paints Preview and Clone on the row", async () => {
    render(<WorkflowsView />);
    expect(await screen.findByRole("button", { name: "Preview Mission" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Clone Mission" })).toBeTruthy();
  });

  it("Preview opens the popup with the fetched conduct, pinned to the row's version", async () => {
    render(<WorkflowsView />);
    fireEvent.click(await screen.findByRole("button", { name: "Preview Mission" }));

    const dialog = await screen.findByRole("dialog", { name: "Preview of Mission" });
    expect(dialog).toBeTruthy();
    // The shape: steps and the human checkpoint are visible in the popup.
    expect(screen.getByText("Settle the design")).toBeTruthy();
    expect(screen.getByText(/Once, at the report/)).toBeTruthy();
    // The REAL conduct arrives (rendered markdown), fetched pinned to v5 - the torn-read and
    // off-workflow discipline the detail page established.
    await waitFor(() => expect(screen.getByText("How a mission runs")).toBeTruthy());
    expect(getWorkflowInstructions).toHaveBeenCalledWith("mission", 5, expect.anything());
  });

  it("Clone from the popup asks first, then clones and navigates to the copy", async () => {
    render(<WorkflowsView />);
    fireEvent.click(await screen.findByRole("button", { name: "Preview Mission" }));
    fireEvent.click(await screen.findByRole("button", { name: "Clone" }));

    // The confirm dialog names the suggested id; nothing has been cloned yet.
    expect(screen.getAllByText(/mission-copy/).length).toBeGreaterThan(0);
    expect(cloneWorkflow).not.toHaveBeenCalled();

    // Confirm: the client is called with the suggested id and the cockpit actor, and the view
    // navigates to the new clone's page.
    const confirm = screen.getAllByRole("button", { name: "Clone" }).at(-1)!;
    fireEvent.click(confirm);
    await waitFor(() => expect(cloneWorkflow).toHaveBeenCalledWith("mission", "mission-copy", "cockpit"));
    await waitFor(() => expect(navigateSpy).toHaveBeenCalledWith("/workflows/mission-copy"));
  });

  it("a failed clone lands in the page error state, never silent", async () => {
    // A real Gateway refusal (409, id taken). The page must SHOW it rather than swallow the failure.
    //
    // CONTRACT CHANGE (issue #2189): this used to assert the wording "rejected the request (error 409)".
    // That phrasing is gone - it was a bare status number dressed up as a sentence. The message now says
    // what a 409 actually means and what to do about it. The property under test is unchanged: the failure
    // reaches the screen.
    const { GatewayError } = await import("@devthrottle/client-core/api/client");
    cloneWorkflow.mockRejectedValue(new GatewayError(409, "id taken"));
    render(<WorkflowsView />);
    fireEvent.click(await screen.findByRole("button", { name: "Clone Mission" }));
    const confirm = screen.getAllByRole("button", { name: "Clone" }).at(-1)!;
    fireEvent.click(confirm);
    await waitFor(() => expect(screen.getByText(/something else changed it first/)).toBeTruthy());
    expect(screen.queryByText(/rejected the request/)).toBeNull();
  });
});
