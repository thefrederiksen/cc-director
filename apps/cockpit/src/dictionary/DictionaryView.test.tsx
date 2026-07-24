// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, fireEvent, waitFor, cleanup } from "@testing-library/react";
import type {
  Dictionary,
  DictionarySuggestion,
  DismissedTerm,
  SuggestionsResult,
} from "@devthrottle/client-core/dictation/dictionaryClient";

// Rendered proof for the dictionary-suggestions panel (devthrottle #2075, redesigned in #2115). The
// scanning, screening, the API and the tenant scoping are proven on the server; this proves the Cockpit
// page actually PAINTS the stored scan - the suggestions with their evidence, the last-scan time, the
// Scan-now button, and the screening-unavailable notice - adds the ticked ones on one press, and
// dismisses a row. The Gateway client is mocked so the view is driven without a running Gateway.

const {
  getDictionary,
  saveDictionary,
  getSuggestions,
  scanSuggestions,
  applySuggestions,
  dismissSuggestion,
  getDismissed,
  restoreDismissed,
} = vi.hoisted(() => ({
  getDictionary: vi.fn(),
  saveDictionary: vi.fn(),
  getSuggestions: vi.fn(),
  scanSuggestions: vi.fn(),
  applySuggestions: vi.fn(),
  dismissSuggestion: vi.fn(),
  getDismissed: vi.fn(),
  restoreDismissed: vi.fn(),
}));

vi.mock("@devthrottle/client-core/dictation/dictionaryClient", () => ({
  getDictionary,
  saveDictionary,
  getSuggestions,
  scanSuggestions,
  applySuggestions,
  dismissSuggestion,
  getDismissed,
  restoreDismissed,
}));

// react-router-dom's Link + useBlocker without a data router (mirrors FleetMapView.test's approach).
vi.mock("react-router-dom", () => ({
  Link: ({ children }: { children: React.ReactNode }) => <a>{children}</a>,
  useBlocker: () => ({ state: "unblocked" as const }),
}));

import { DictionaryView } from "./DictionaryView";

const EMPTY_DICT: Dictionary = { vocabulary: [], commonMistranscriptions: {}, profiles: {} };

const MINDZIE: DictionarySuggestion = {
  term: "mindzie",
  variants: [
    { heard: "Mindsee", count: 20 },
    { heard: "Mindsy", count: 15 },
    { heard: "Mindzee", count: 12 },
  ],
  wrongCount: 47,
  totalCount: 91,
};

function scanResult(overrides?: Partial<SuggestionsResult>): SuggestionsResult {
  return {
    suggestions: [MINDZIE],
    count: 1,
    scannedAtUtc: "2026-07-24T00:05:00Z",
    screeningOk: true,
    screeningError: "",
    ...overrides,
  };
}

describe("DictionaryView suggestions panel", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    getDictionary.mockResolvedValue(EMPTY_DICT);
    saveDictionary.mockResolvedValue(EMPTY_DICT);
    getSuggestions.mockResolvedValue(scanResult());
    getDismissed.mockResolvedValue([] as DismissedTerm[]);
  });
  afterEach(() => cleanup());

  it("renders each suggestion with its evidence, counts, and the last-scan time", async () => {
    render(<DictionaryView />);

    expect(await screen.findByText("1 suggestion from your recent dictations")).toBeTruthy();
    expect(screen.getByText("mindzie")).toBeTruthy();
    // The evidence line and the count behind "wrong 47 of 91 times".
    expect(screen.getByText("Mindsee")).toBeTruthy();
    expect(screen.getByText("wrong 47 of 91 times")).toBeTruthy();
    // Pre-ticked, so the Add button offers all of them.
    expect(screen.getByText("Add 1 selected to dictionary")).toBeTruthy();
    // The scan row: when the stored scan ran, and the Scan-now doorway.
    expect(screen.getByText(/Last scan:/)).toBeTruthy();
    expect(screen.getByText("Scan now")).toBeTruthy();
  });

  it("adds the ticked terms on one press", async () => {
    applySuggestions.mockResolvedValue({
      dictionary: { ...EMPTY_DICT, vocabulary: ["mindzie"] },
      applied: ["mindzie"],
      suggestions: [],
      count: 0,
    });
    render(<DictionaryView />);

    fireEvent.click(await screen.findByText("Add 1 selected to dictionary"));

    await waitFor(() => expect(applySuggestions).toHaveBeenCalledWith(["mindzie"]));
    // The panel collapses to the quiet zero state once nothing is pending.
    expect(await screen.findByText(/No suggestions right now/)).toBeTruthy();
  });

  it("unticking a term drops it from the add count", async () => {
    render(<DictionaryView />);
    await screen.findByText("mindzie");

    fireEvent.click(screen.getByLabelText("Add mindzie")); // untick
    expect(screen.getByText("Add 0 selected to dictionary")).toBeTruthy();
  });

  it("dismissing a suggestion removes it and lists it under dismissed", async () => {
    dismissSuggestion.mockResolvedValue(0);
    render(<DictionaryView />);
    await screen.findByText("mindzie");

    // After dismiss, the refresh returns no suggestions and one dismissed term.
    getSuggestions.mockResolvedValue(scanResult({ suggestions: [], count: 0 }));
    getDismissed.mockResolvedValue([
      { term: "mindzie", variants: [{ heard: "Mindsee", count: 20 }], wrongCount: 47, totalCount: 91, dismissedAtUtc: "2026-07-24T12:00:00Z" },
    ]);

    fireEvent.click(screen.getByText("Dismiss"));

    await waitFor(() => expect(dismissSuggestion).toHaveBeenCalledWith("mindzie"));
    expect(await screen.findByText(/No suggestions right now/)).toBeTruthy();
    expect(await screen.findByText("Dismissed terms (1)")).toBeTruthy();
  });

  it("shows the never-scanned zero state with a Scan now doorway before any scan", async () => {
    getSuggestions.mockResolvedValue(
      scanResult({ suggestions: [], count: 0, scannedAtUtc: null }),
    );
    render(<DictionaryView />);

    expect(await screen.findByText(/No scan has run yet/)).toBeTruthy();
    expect(screen.getByText("Never scanned")).toBeTruthy();
    expect(screen.getByText("Scan now")).toBeTruthy();
    // No panel header, no Add button.
    expect(screen.queryByText(/suggestions from your recent dictations/)).toBeNull();
  });

  it("Scan now runs a scan and paints its result", async () => {
    getSuggestions.mockResolvedValue(
      scanResult({ suggestions: [], count: 0, scannedAtUtc: null }),
    );
    scanSuggestions.mockResolvedValue(scanResult());
    render(<DictionaryView />);

    fireEvent.click(await screen.findByText("Scan now"));

    await waitFor(() => expect(scanSuggestions).toHaveBeenCalled());
    expect(await screen.findByText("mindzie")).toBeTruthy();
    expect(screen.getByText(/Last scan:/)).toBeTruthy();
  });

  it("says so when the screening model was unreachable on the last scan", async () => {
    getSuggestions.mockResolvedValue(
      scanResult({ screeningOk: false, screeningError: "model unreachable" }),
    );
    render(<DictionaryView />);

    await screen.findByText("mindzie");
    expect(
      screen.getByText(/The screening service could not be reached on the last scan/),
    ).toBeTruthy();
    expect(screen.getByText(/model unreachable/)).toBeTruthy();
  });
});
