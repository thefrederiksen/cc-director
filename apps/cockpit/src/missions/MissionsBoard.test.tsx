// @vitest-environment jsdom
import { describe, expect, it, vi, beforeEach, afterEach } from "vitest";
import { render, screen, waitFor, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { SessionDto } from "@devthrottle/client-core/api/client";
import type { MissionDto } from "@devthrottle/client-core/missions/missions";

// The WHY store is keyed by the mission's lower-cased NAME; the board now groups by mission ID. Those two
// keys are not the same string, and the gap between them is invisible to the pure grouping tests - the
// board renders, the cards are right, and the WHY text is just silently gone.
//
// That is exactly what happened when the grouping moved to ids: the card looked its WHY up by the group's
// key, which had become an id, and every WHY the owner had written disappeared without any error. These
// tests render the real board so a repeat cannot pass.
vi.mock("@devthrottle/client-core/missions/missionNotes", () => ({
  getMissionNotes: vi.fn(),
  setMissionNote: vi.fn(),
}));

import { getMissionNotes } from "@devthrottle/client-core/missions/missionNotes";
import { MissionsBoard } from "./MissionsBoard";

const M_RELEASE: MissionDto = { missionId: "45e28f0c", missionName: "Release 2.0.1" };

// The Gateway stamps the color and the label; the shared ordering module THROWS rather than re-deriving
// them (the client-is-dumb rule), so a fixture has to carry them exactly as the roster would.
function session(fields: Record<string, unknown>): SessionDto {
  return {
    effectiveColor: "blue",
    stateLabel: "Working",
    dotHex: "#4a9eff",
    ...fields,
  } as unknown as SessionDto;
}

const ARCHITECT = session({
  name: "Release 2.0.1 - Architect",
  number: 124,
  sessionId: "s-124",
  missionId: "45e28f0c",
  sessionRole: "Architect",
});

const WORKER = session({
  name: "fix: 2481 delete bias path",
  number: 113,
  sessionId: "s-113",
  missionId: "45e28f0c",
  sessionRole: "Worker",
});

function renderBoard(props: Parameters<typeof MissionsBoard>[0]) {
  return render(
    <MemoryRouter>
      <MissionsBoard {...props} />
    </MemoryRouter>,
  );
}

beforeEach(() => {
  vi.mocked(getMissionNotes).mockResolvedValue([]);
});

afterEach(() => {
  cleanup();
});

describe("MissionsBoard", () => {
  it("shows the mission's WHY - the store is keyed by NAME, the grouping by ID", async () => {
    vi.mocked(getMissionNotes).mockResolvedValue([
      {
        key: "release 2.0.1",
        mission: "Release 2.0.1",
        why: "So we can get the Video Competition started",
        updatedAt: "2026-08-07T00:00:00Z",
      },
    ]);

    renderBoard({ sessions: [ARCHITECT], missions: [M_RELEASE] });

    await waitFor(() =>
      expect(screen.getByText("So we can get the Video Competition started")).toBeTruthy(),
    );
    // ...and therefore NOT the "no why set" flag.
    expect(screen.queryByText(/No why set/i)).toBeNull();
  });

  it("shows the loud flag when a mission genuinely has no WHY", async () => {
    renderBoard({ sessions: [ARCHITECT], missions: [M_RELEASE] });
    await waitFor(() => expect(screen.getByText(/No why set/i)).toBeTruthy());
  });

  it("lists every attached session by NAME, with its role as a badge", async () => {
    renderBoard({ sessions: [ARCHITECT, WORKER], missions: [M_RELEASE] });

    // The name is what tells one worker from another - the role alone would repeat down the card.
    expect(screen.getByText("fix: 2481 delete bias path")).toBeTruthy();
    expect(screen.getByText("Release 2.0.1 - Architect")).toBeTruthy();
    expect(screen.getByText("Architect")).toBeTruthy();
    expect(screen.getByText("Worker")).toBeTruthy();
    expect(screen.getByText("2 sessions")).toBeTruthy();
  });

  it("renders a mission nobody is on yet", async () => {
    renderBoard({ sessions: [], missions: [M_RELEASE] });

    expect(screen.getByText("Release 2.0.1")).toBeTruthy();
    expect(screen.getByText("0 sessions")).toBeTruthy();
    expect(screen.getByText("no sessions yet")).toBeTruthy();
  });

  it("marks a mission the Gateway's mission list does not contain", async () => {
    renderBoard({
      sessions: [
        session({
          name: "worker one",
          number: 140,
          sessionId: "s-140",
          missionId: "ghost",
          missionName: "BPM Studio QA cleanup",
          sessionRole: "Worker",
        }),
      ],
      missions: [M_RELEASE],
    });

    expect(screen.getByText("BPM Studio QA cleanup")).toBeTruthy();
    expect(screen.getByText(/not in the mission list/i)).toBeTruthy();
  });

  it("says so loudly when the mission list could not be loaded", async () => {
    renderBoard({ sessions: [], missions: [], error: "Gateway said 503." });

    expect(screen.getByText(/could not be loaded/i)).toBeTruthy();
    expect(screen.getByText(/Gateway said 503\./)).toBeTruthy();
  });
});
