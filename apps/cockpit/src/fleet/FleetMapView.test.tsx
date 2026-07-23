// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import type { DirectorReachability } from "@devthrottle/client-core/fleet/fleetClient";
import type { SharedRoster } from "@devthrottle/client-core/fleet/rosterStore";

// Rendered proof for the "By machine" free-slots fix: a machine that has a reachable Director but no
// sessions must still appear as a lane (available capacity), and an idle Director inside a machine must
// render as a "free slot" rather than vanishing. Only the machine pivot does this - repository / working
// tree / agent still group real sessions only. The pure folding is unit tested in fleetMapFormat.test;
// this test proves the view actually paints the lane and the placeholder.

// The Fleet Map reads the ONE shared roster store; mock it so the test drives an exact fleet without a
// Gateway. Canvas measures the DOM with ResizeObserver, which jsdom lacks - stub it so layout runs.
const rosterValue: { current: SharedRoster } = {
  current: { sessions: [], machineErrors: [], directors: [], error: null, refreshNow: () => {} },
};
vi.mock("@devthrottle/client-core/fleet/rosterStore", () => ({
  useSharedRoster: () => rosterValue.current,
}));
vi.mock("react-router-dom", () => ({ useNavigate: () => vi.fn() }));

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

  it("excludes an offline Director - offline is not a free slot", () => {
    rosterValue.current = {
      sessions: [session()],
      machineErrors: [],
      directors: [
        director({ directorId: "north-1", machineName: "SOREN_NORTH", state: "online" }),
        director({ directorId: "dead-1", machineName: "DEAD_MACHINE", state: "offline" }),
      ],
      error: null,
      refreshNow: () => {},
    };

    render(<FleetMapView />);

    expect(screen.queryByText("DEAD_MACHINE")).toBeNull();
    expect(screen.queryByText(/free slot/i)).toBeNull();
    // Only the one reachable machine is counted; the offline one is not "available capacity".
    expect(screen.getAllByText(/\b1 machine\b/).length).toBeGreaterThan(0);
  });
});
