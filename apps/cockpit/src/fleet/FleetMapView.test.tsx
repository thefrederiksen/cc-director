// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup, fireEvent, waitFor } from "@testing-library/react";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import type { DirectorReachability } from "@devthrottle/client-core/fleet/fleetClient";
import type { SharedRoster } from "@devthrottle/client-core/fleet/rosterStore";

// Rendered proof for the "By machine" pivot's machine fold: a machine that has a Director but no sessions
// must still appear as a lane, an idle Director inside a machine must render as a "free slot" rather than
// vanishing, and an UNREACHABLE machine must render dimmed and dated rather than being dropped. Only the
// machine pivot does this - repository / working tree / agent still group real sessions only. The pure
// folding is unit tested in fleetMapFormat.test; this test proves the view actually paints the lane, the
// placeholder, and the unreachable sub-header.

// The Fleet Map reads the ONE shared roster store; mock it so the test drives an exact fleet without a
// Gateway. Canvas measures the DOM with ResizeObserver, which jsdom lacks - stub it so layout runs.
const rosterValue: { current: SharedRoster } = {
  current: { sessions: [], machineErrors: [], directors: [], error: null, refreshNow: () => {} },
};
vi.mock("@devthrottle/client-core/fleet/rosterStore", () => ({
  useSharedRoster: () => rosterValue.current,
}));
vi.mock("react-router-dom", () => ({ useNavigate: () => vi.fn() }));

// The Fleet Map "+ New session" button opens the SAME NewSessionDialog the Sessions tab uses, which
// loads its machine / repo / agent pickers from the Gateway. Mock those data calls so the dialog renders
// against a fixed fleet without a real Gateway. getDirectors is what proves the pre-selection: the dialog
// default-selects the NEWEST-started Director, so we make "soren-1" newest and assert the CLICKED
// "north-1" is selected instead - which can only happen if the Fleet Map passed it through.
const getDirectorsMock = vi.fn();
vi.mock("@devthrottle/client-core/api/client", () => ({
  getDirectors: (...args: unknown[]) => getDirectorsMock(...args),
  getRepos: () => Promise.resolve([]),
  getAgents: () => Promise.resolve([]),
  createSession: () => Promise.resolve({ sessionId: "new" }),
  gatewayErrorMessage: (e: unknown) => String(e),
}));

import { FleetMapView } from "./FleetMapView";

// A fully Gateway-stamped session (effectiveColor / stateLabel / effectiveColorHex are required fields the
// dumb client renders verbatim, so a fixture missing them would throw or paint the protocol sentinel).
function session(overrides: Partial<SessionDto> = {}): SessionDto {
  return {
    sessionId: "s1",
    machineName: "SOREN_NORTH",
    directorId: "north-1",
    repoName: "thefrederiksen/devthrottle",
    repoPath: "D:/ReposFred/devthrottle",
    agent: "ClaudeCode",
    activityState: "Working",
    createdAt: "2026-07-23T10:00:00Z",
    number: 104,
    name: "release",
    effectiveColor: "blue",
    stateLabel: "Working",
    effectiveColorHex: "#3b82f6",
    ...overrides,
  } as SessionDto;
}

function director(overrides: Partial<DirectorReachability> = {}): DirectorReachability {
  return { directorId: "d", machineName: "SOREN", state: "online", ...overrides };
}

beforeEach(() => {
  window.localStorage.clear();
  // jsdom has no ResizeObserver; a no-op stub is enough for the Canvas layout effect.
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
});

afterEach(() => cleanup());

describe("FleetMapView - By machine free slots", () => {
  it("shows an idle machine as a lane with a free slot, even though it has no sessions", () => {
    rosterValue.current = {
      sessions: [session()], // one busy session on SOREN_NORTH
      machineErrors: [],
      directors: [
        director({ directorId: "north-1", machineName: "SOREN_NORTH", state: "online" }),
        director({ directorId: "soren-1", machineName: "SOREN", state: "online" }), // idle, no sessions
        director({ directorId: "mac-1", machineName: "Sorens-Mac-mini", state: "online" }), // idle
      ],
      error: null,
      refreshNow: () => {},
    };

    render(<FleetMapView />);

    // The header (and the canvas root) count all three reachable machines, not just the one running work.
    expect(screen.getAllByText(/\b3 machines\b/).length).toBeGreaterThan(0);

    // The idle machines each get a lane header - they were completely absent before this fix.
    expect(screen.getByText("SOREN")).toBeTruthy();
    expect(screen.getByText("Sorens-Mac-mini")).toBeTruthy();

    // Idle Directors render as free slots (one per idle machine here).
    const freeSlots = screen.getAllByText(/free slot/i);
    expect(freeSlots.length).toBe(2);

    // The busy machine still shows its session and does NOT show a free slot for its only Director.
    expect(screen.getByText("release")).toBeTruthy();
  });

  it("does not fold idle Directors into the repository pivot - only machine shows capacity", () => {
    window.localStorage.setItem("cockpit.fleetMapPivot", "repo");
    rosterValue.current = {
      sessions: [session()],
      machineErrors: [],
      directors: [
        director({ directorId: "north-1", machineName: "SOREN_NORTH", state: "online" }),
        director({ directorId: "soren-1", machineName: "SOREN", state: "online" }),
      ],
      error: null,
      refreshNow: () => {},
    };

    render(<FleetMapView />);

    // The repository pivot groups real sessions; an idle machine is not a repository, so no free slots.
    expect(screen.queryByText(/free slot/i)).toBeNull();
    expect(screen.getByText("thefrederiksen/devthrottle")).toBeTruthy();
  });

  // An offline machine used to be EXCLUDED here, on the argument that it is not available capacity. That
  // argument deleted the machine from the map altogether - which is the delete-instead-of-dim defect the
  // Gateway has stopped committing (it now serves an unreachable machine's sessions with their age). The
  // machine is shown, dimmed and dated; what it must NOT do is claim to be a free slot or offer an action
  // that cannot be honoured.
  it("shows an offline machine, dated and with no new-session action - dimmed, never dropped", () => {
    rosterValue.current = {
      // Director ids whose last segment differs, because the sub-header shortens a Director to that
      // segment ("Director alpha"), and two ids ending in the same segment would be indistinguishable.
      sessions: [session({ directorId: "north-alpha" })],
      machineErrors: [],
      directors: [
        director({ directorId: "north-alpha", machineName: "SOREN_NORTH", state: "online" }),
        director({ directorId: "dead-beta", machineName: "DEAD_MACHINE", state: "offline", lastSeenAgeSeconds: 420 }),
      ],
      error: null,
      refreshNow: () => {},
    };

    render(<FleetMapView />);

    // The lane exists, and it says what state the machine is in and how old that answer is.
    expect(screen.getByText("DEAD_MACHINE")).toBeTruthy();
    expect(screen.getByText(/Offline - last seen 7m ago/)).toBeTruthy();
    // It is not offered as capacity: no "free slot" line, and no button that could not be honoured.
    expect(screen.queryByText(/free slot/i)).toBeNull();
    expect(screen.getByText(/machine unreachable/i)).toBeTruthy();
    expect(screen.queryByRole("button", { name: /Start a new session on Director beta/ })).toBeNull();
    // The reachable machine keeps its own action.
    expect(screen.getByRole("button", { name: /Start a new session on Director alpha/ })).toBeTruthy();
    // Both machines are counted, so the header agrees with the lanes below it.
    expect(screen.getAllByText(/\b2 machines\b/).length).toBeGreaterThan(0);
  });
});

describe("FleetMapView - Director sub-header new-session button", () => {
  it("opens the shared dialog pre-targeted to the clicked Director, not the newest one", async () => {
    // The dialog would otherwise default to the NEWEST-started Director. Make "soren-bbb" newest, so if
    // the Fleet Map failed to pass the clicked Director through, the dialog would select SOREN_SOUTH.
    getDirectorsMock.mockResolvedValue([
      {
        directorId: "north-aaa",
        machineName: "SOREN_NORTH",
        version: "1",
        startedAt: "2026-07-20T00:00:00Z",
        lastSeen: "",
        controlEndpoint: "http://127.0.0.1:7880",
      },
      {
        directorId: "soren-bbb",
        machineName: "SOREN_SOUTH",
        version: "1",
        startedAt: "2026-07-24T00:00:00Z", // newest -> the dialog's default pick
        lastSeen: "",
        controlEndpoint: "http://127.0.0.1:7990",
      },
    ]);

    rosterValue.current = {
      sessions: [session({ directorId: "north-aaa", machineName: "SOREN_NORTH" })],
      machineErrors: [],
      directors: [director({ directorId: "north-aaa", machineName: "SOREN_NORTH", state: "online" })],
      error: null,
      refreshNow: () => {},
    };

    render(<FleetMapView />);

    // The machine pivot renders a "+ New session" button on the Director sub-header (top-right).
    const newBtn = screen.getByRole("button", { name: /Start a new session on Director/ });
    fireEvent.click(newBtn);

    // The SAME dialog the Sessions tab opens appears, and it default-selects the clicked Director's
    // machine (SOREN_NORTH) rather than the newest-started one (SOREN_SOUTH).
    const dialog = await screen.findByRole("dialog", { name: /Start a new session/ });
    await waitFor(() => {
      const north = dialog.querySelector("button.newsess-machine.sel");
      expect(north?.textContent).toContain("SOREN_NORTH");
    });
  });
});
