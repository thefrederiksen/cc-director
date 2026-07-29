// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup } from "@testing-library/react";
import type { SkillDefinition } from "@devthrottle/client-core/skills/skillsClient";

// Rendered proof for the Skills register (devthrottle_internal issue 995). The Gateway behavior is
// proven server-side; what this pins is the page's half of the contract, and specifically the three
// things the owner asked for:
//
//   - a built-in cannot be edited here, but CAN be cloned;
//   - any skill can be switched off, and turning one off ASKS FIRST because it removes a capability
//     from every agent in the fleet;
//   - the register itself shows no skill body - the body is fetched only when someone opens Read.
//
// The Gateway client is mocked so the view is driven without a running Gateway.

const {
  getSkills,
  getSkillBody,
  cloneSkill,
  setSkillEnabled,
  createSkill,
  suggestSkillId,
} = vi.hoisted(() => ({
  getSkills: vi.fn(),
  getSkillBody: vi.fn(),
  cloneSkill: vi.fn(),
  setSkillEnabled: vi.fn(),
  createSkill: vi.fn(),
  suggestSkillId: vi.fn(() => "suggested-id"),
}));

vi.mock("@devthrottle/client-core/skills/skillsClient", () => ({
  getSkills,
  getSkillBody,
  cloneSkill,
  setSkillEnabled,
  createSkill,
  suggestSkillId,
}));

import { SkillsView } from "./SkillsView";

const MOVE_SESSION: SkillDefinition = {
  id: "move-session",
  name: "Move a session",
  summary: "Relocate a live session to another Director.",
  triggers: ["move session", "migrate session"],
  version: 5,
  isBuiltIn: true,
  hasDraft: false,
  fileCount: 0,
  enabled: true,
  editable: false,
};

const MINE: SkillDefinition = {
  id: "our-rules",
  name: "Our rules",
  summary: "Our own move rules.",
  triggers: ["our rules"],
  version: 2,
  isBuiltIn: false,
  hasDraft: false,
  fileCount: 1,
  enabled: true,
  editable: true,
};

describe("SkillsView", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    getSkills.mockResolvedValue([MOVE_SESSION, MINE]);
    getSkillBody.mockResolvedValue("# Move Session\n\nRelocate a live session.");
    cloneSkill.mockResolvedValue({ ...MINE, id: "move-session-copy" });
    setSkillEnabled.mockResolvedValue(undefined);
  });

  afterEach(() => cleanup());

  it("lists every skill with its one line and marks whose it is", async () => {
    render(<SkillsView />);

    expect(await screen.findByText("move-session")).toBeTruthy();
    expect(screen.getByText("Relocate a live session to another Director.")).toBeTruthy();
    expect(screen.getByText("Built-in")).toBeTruthy();
    expect(screen.getByText("Yours")).toBeTruthy();
  });

  it("shows no skill body in the register itself", async () => {
    render(<SkillsView />);
    await screen.findByText("move-session");

    // The register is the same small shape every session's briefing is rendered from. If a body ever
    // reaches it, discovery costs what use should cost - the exact inversion this feature exists to
    // prevent. Nothing has been fetched because nothing needed it.
    expect(getSkillBody).not.toHaveBeenCalled();
    expect(screen.queryByText(/Relocate a live session\.$/)).toBeNull();
  });

  it("fetches the body only when Read is opened, pinned to the row's version", async () => {
    render(<SkillsView />);
    await screen.findByText("move-session");

    fireEvent.click(screen.getAllByRole("button", { name: /^Read / })[0]);

    await waitFor(() => expect(getSkillBody).toHaveBeenCalled());
    // Pinned: an unpinned read racing a publish could pair one version's summary with another's body.
    expect(getSkillBody.mock.calls[0][0]).toBe("move-session");
    expect(getSkillBody.mock.calls[0][1]).toBe(5);
  });

  it("offers Clone everywhere, and the edit affordance ONLY where the Gateway says editable", async () => {
    render(<SkillsView />);
    await screen.findByText("move-session");

    // Cloning is how a read-only built-in is customized, so the action is on every row.
    expect(screen.getAllByRole("button", { name: /^Clone / }).length).toBe(2);

    // THE RULE THIS PINS (rule 7 - the client is dumb): editability is the GATEWAY's verdict,
    // rendered verbatim and never derived here. The built-in sends editable:false and gets no way to
    // edit its files at all - a button whose write would be refused reads as broken. The tenant's own
    // skill sends editable:true and gets one.
    const editButtons = screen.getAllByRole("button", { name: /^Edit the files of / });
    expect(editButtons.length).toBe(1);
    expect(editButtons[0].getAttribute("aria-label")).toContain("Our rules");
  });

  it("asks before switching a skill off, then calls the Gateway with an actor", async () => {
    render(<SkillsView />);
    await screen.findByText("move-session");

    fireEvent.click(screen.getAllByRole("switch")[0]);

    // Removing a capability from every agent in the fleet is consequential enough to confirm.
    expect(setSkillEnabled).not.toHaveBeenCalled();
    expect(await screen.findByText(/Turn 'Move a session' off\?/)).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Turn off" }));

    await waitFor(() => expect(setSkillEnabled).toHaveBeenCalled());
    expect(setSkillEnabled.mock.calls[0][0]).toBe("move-session");
    expect(setSkillEnabled.mock.calls[0][1]).toBe(false);
    // A governance change is never anonymous.
    expect(setSkillEnabled.mock.calls[0][2]).toBe("cockpit");
  });

  it("turning a skill back on does not ask", async () => {
    getSkills.mockResolvedValue([{ ...MOVE_SESSION, enabled: false }]);
    render(<SkillsView />);
    await screen.findByText("move-session");

    fireEvent.click(screen.getAllByRole("switch")[0]);

    await waitFor(() => expect(setSkillEnabled).toHaveBeenCalled());
    expect(setSkillEnabled.mock.calls[0][1]).toBe(true);
  });

  it("says plainly that an off skill is not offered to agents", async () => {
    getSkills.mockResolvedValue([{ ...MOVE_SESSION, enabled: false }]);
    render(<SkillsView />);

    expect(await screen.findByText("agents will not see or fetch this")).toBeTruthy();
    expect(screen.getByText("Off")).toBeTruthy();
  });

  it("shows the Gateway's error rather than an empty register", async () => {
    getSkills.mockRejectedValue(new Error("gateway down"));
    render(<SkillsView />);

    // An empty list would read as "you have no skills", which is a different and false statement.
    // The wording comes from the shared error mapper (a transport failure is reported as a reach
    // problem, not as an empty register), so this asserts the banner and the RETRY, not the text.
    expect(await screen.findByRole("button", { name: "Try again" })).toBeTruthy();
    expect(screen.queryByText("move-session")).toBeNull();
  });

  it("hides the switch when the Gateway did not report one", async () => {
    getSkills.mockResolvedValue([{ ...MOVE_SESSION, enabled: undefined }]);
    render(<SkillsView />);
    await screen.findByText("move-session");

    // An older Gateway without the switch routes must not show a control that can only fail.
    expect(screen.queryByRole("switch")).toBeNull();
  });
});
