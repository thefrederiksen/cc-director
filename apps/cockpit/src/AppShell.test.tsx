// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

// The left rail's ORDER is a product decision, not an accident of the array literal, so it is pinned
// here: Sessions first, then Fleet Map, then Assistant. The sessions are the work, so the destination
// reached for most sits at the top. Without this test the order is one careless re-sort away from
// changing silently - nothing else in the app reads it.

vi.mock("@devthrottle/client-core/net/useKeepWarm", () => ({
  useKeepWarm: () => {},
}));

vi.mock("@devthrottle/client-core/dictation/dictionaryClient", () => ({
  getSuggestionCount: vi.fn(async () => 0),
}));

vi.mock("./network/CockpitStatusPill", () => ({
  CockpitStatusPill: () => null,
}));

import { AppShell } from "./AppShell";

function railLabels(): string[] {
  const list = document.querySelector(".nav-list:not(.nav-list-foot)");
  if (list === null) throw new Error("the shell rendered no main nav list");
  return Array.from(list.querySelectorAll(".nav-link-label")).map((el) => el.textContent ?? "");
}

describe("Cockpit left rail", () => {
  beforeEach(() => {
    // This project runs vitest without globals, so testing-library's automatic cleanup is not
    // registered - without this, each render leaks into the next test's document.
    cleanup();
  });

  it("opens with Sessions, then Fleet Map, then Assistant", () => {
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    expect(railLabels().slice(0, 3)).toEqual(["Sessions", "Fleet Map", "Assistant"]);
  });

  it("leaves the rest of the rail where it was", () => {
    render(
      <MemoryRouter initialEntries={["/sessions"]}>
        <AppShell />
      </MemoryRouter>,
    );

    expect(railLabels()).toEqual([
      "Sessions",
      "Fleet Map",
      "Assistant",
      "History",
      "Directors",
      "Schedule",
      "Workflows",
      // Workflows, Rules and Skills sit together on purpose: the shelf of things you DEFINE and the
      // fleet then works by. A workflow governs how a mission is run (the central catalog), a rule
      // is a standing instruction that watches a session's screen and acts on it, and a skill is a
      // capability an agent reaches for mid-task (the central skill library,
      // devthrottle_internal issue 995). Nothing else moved.
      "Rules",
      "Skills",
      "Dictionary",
      "Voice Recorder",
      "Transcription",
      "Network",
    ]);
  });
});
