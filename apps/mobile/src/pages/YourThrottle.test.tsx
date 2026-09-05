// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";

// The phone's Your Throttle page inside a real router, at the URL the mentor report's link carries on
// the phone (mission "Clean up Your Throttle", rulings R4 and R5): /throttle?week=2026-W35 asks the
// Gateway for that week and nothing else, the page shows the Gateway's label verbatim, and choosing a
// length from the shared selector asks for that length and puts it in the URL. The Gateway is a fetch
// stub answering the shape the real feed serves; the page decides nothing about what a window means.

vi.mock("@devthrottle/client-core/api/client", () => ({
  authHeaders: () => ({}),
  gatewayErrorMessage: (e: unknown) => (e instanceof Error ? e.message : String(e)),
  GatewayError: class GatewayError extends Error {
    constructor(public status: number, message: string) {
      super(message);
    }
  },
}));

import { YourThrottle } from "./YourThrottle";

const WEEK_LABEL = "Week 35 of 2026, Monday 24 August to Sunday 30 August (America/Toronto)";
const CHOICES = [
  { days: 1, label: "Last 24 hours" },
  { days: 7, label: "Last 7 days" },
  { days: 14, label: "Last 14 days" },
  { days: 30, label: "Last 30 days" },
];

function windowFor(url: string) {
  const query = new URL(url, "http://gateway.test").searchParams;
  const week = query.get("week");
  const days = query.get("days");
  if (week !== null) {
    return { fromUtc: "2026-08-24T04:00:00Z", toUtc: "2026-08-31T04:00:00Z", isDefault: false, label: WEEK_LABEL, kind: "week", days: null, week, choices: CHOICES };
  }
  if (days !== null) {
    return { fromUtc: "2026-08-22T16:00:00Z", toUtc: "2026-09-05T16:00:00Z", isDefault: false, label: `Last ${days} days`, kind: "days", days: Number(days), week: null, choices: CHOICES };
  }
  return { fromUtc: "2026-08-29T16:00:00Z", toUtc: "2026-09-05T16:00:00Z", isDefault: true, label: "Last 7 days", kind: "default", days: 7, week: null, choices: CHOICES };
}

function bodyFor(url: string) {
  return {
    available: true,
    generatedAtUtc: "2026-09-05T16:00:00Z",
    timeZone: "America/Toronto",
    throttle: {
      definition: "the predicate",
      unit: "submitted turns",
      window: windowFor(url),
      ledger: { retentionDays: 30, earliestUtc: "2026-08-06T04:00:00Z" },
      turns: 4,
      voiceTurns: 3,
      typedTurns: 1,
      sessions: 1,
      buckets: [
        { modality: "voice", surface: "phone", turns: 3 },
        { modality: "typed", surface: "desktop", turns: 1 },
      ],
      hourlyTurns: [],
      agents: [],
      repos: [],
      reposUnattributedTurns: 0,
      excluded: { noInputOrigin: 0, agentDriven: 0, framework: 0, unresolved: 0 },
      agentDrivenTurns: 0,
    },
    concurrency: null,
    statisticsUnavailableReason: "no store",
    notCaptured: [],
  };
}

function stubGateway() {
  const fetchMock = vi.fn(async (input: RequestInfo | URL) =>
    new Response(JSON.stringify(bodyFor(String(input))), { status: 200, headers: { "Content-Type": "application/json" } }),
  );
  vi.stubGlobal("fetch", fetchMock);
  return fetchMock;
}

function LocationProbe() {
  const location = useLocation();
  return <div data-testid="location">{location.pathname + location.search}</div>;
}

function renderAt(url: string) {
  return render(
    <MemoryRouter initialEntries={[url]}>
      <Routes>
        <Route
          path="/throttle"
          element={
            <>
              <YourThrottle />
              <LocationProbe />
            </>
          }
        />
        <Route path="/" element={<div>Home</div>} />
      </Routes>
    </MemoryRouter>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("YourThrottle on the phone at the mentor report's link", () => {
  it("asks the Gateway for exactly the week in the URL and shows the Gateway's label for it verbatim", async () => {
    const fetchMock = stubGateway();
    renderAt("/throttle?week=2026-W35");

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    expect(String(fetchMock.mock.calls[0][0])).toBe("/stats/data?week=2026-W35");

    const note = await screen.findByTestId("mthr-window");
    expect(note.textContent).toContain(WEEK_LABEL);
    expect(screen.getByTestId("thr-window-week").textContent).toBe(WEEK_LABEL);
    expect(screen.queryAllByRole("button", { pressed: true })).toHaveLength(0);
  });

  it("choosing a length re-asks the Gateway with days=N, puts days=N in the URL, and marks the served choice", async () => {
    const fetchMock = stubGateway();
    renderAt("/throttle?week=2026-W35");
    await screen.findByTestId("thr-window-week");

    fireEvent.click(screen.getByRole("button", { name: "Last 30 days" }));

    await waitFor(() => expect(fetchMock.mock.calls.map((c) => String(c[0]))).toContain("/stats/data?days=30"));
    expect(screen.getByTestId("location").textContent).toBe("/throttle?days=30");
    await screen.findByRole("button", { name: "Last 30 days", pressed: true });
    expect((await screen.findByTestId("mthr-window")).textContent).toContain("Last 30 days");
    expect(screen.queryByTestId("thr-window-week")).toBeNull();
  });

  it("a URL with neither asks for the Gateway's default and shows the served label", async () => {
    const fetchMock = stubGateway();
    renderAt("/throttle");

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    expect(String(fetchMock.mock.calls[0][0])).toBe("/stats/data");
    expect((await screen.findByTestId("mthr-window")).textContent).toContain("Last 7 days");
    await screen.findByRole("button", { name: "Last 7 days", pressed: true });
  });
});
