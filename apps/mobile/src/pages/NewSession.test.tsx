// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";

const api = vi.hoisted(() => ({
  createSession: vi.fn(),
  getAgents: vi.fn(),
  getDirectors: vi.fn(),
  getKnownRepositories: vi.fn(),
  getRepos: vi.fn(),
}));

vi.mock("@devthrottle/client-core/api/client", () => ({
  ...api,
  gatewayErrorMessage: (error: unknown) => error instanceof Error ? error.message : String(error),
}));

vi.mock("@devthrottle/client-core/sessions/waiting", () => ({
  durationLabel: () => "",
  useNow: () => Date.now(),
}));

import { NewSession } from "./NewSession";

interface Deferred<T> {
  promise: Promise<T>;
  resolve: (value: T) => void;
}

function deferred<T>(): Deferred<T> {
  let resolve: (value: T) => void = () => {};
  const promise = new Promise<T>((complete) => {
    resolve = complete;
  });
  return { promise, resolve };
}

function director(directorId: string, displayName: string, machineName: string) {
  return {
    directorId,
    displayName,
    machineName,
    version: "1.2.3",
    startedAt: "2026-09-01T10:00:00Z",
    lastSeen: "2026-09-01T12:00:00Z",
    controlEndpoint: "",
  };
}

function agent(type: string, displayName: string, modelLabel: string) {
  return { type, displayName, modelLabel, defaultModel: modelLabel.toLocaleLowerCase() };
}

function repository(number: number) {
  return {
    name: "Repository " + number,
    path: "D:\\Repositories\\repository-" + number,
    lastUsed: "2026-08-" + String(20 - number).padStart(2, "0") + "T12:00:00Z",
  };
}

function renderPage() {
  return render(
    <MemoryRouter initialEntries={["/new"]}>
      <Routes>
        <Route path="/new" element={<NewSession />} />
        <Route path="/session/:sessionId" element={<div>Opened session</div>} />
        <Route path="/" element={<div>Roster</div>} />
      </Routes>
    </MemoryRouter>,
  );
}

async function openRepositoryStep() {
  fireEvent.click(await screen.findByRole("button", { name: "Select Director North Director" }));
  fireEvent.click(await screen.findByRole("button", { name: "Select agent Terminal agent" }));
  await screen.findByLabelText("Search known repositories");
}

describe("mobile new session", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.localStorage.clear();
    api.getDirectors.mockResolvedValue([
      director("north", "North Director", "SOREN_NORTH"),
      director("south", "South Director", "SOREN_SOUTH"),
    ]);
    api.getAgents.mockImplementation(async (directorId: string) =>
      directorId === "north"
        ? [agent("DefaultAgent", "Default agent", "Configured model"), agent("RawCli", "Terminal agent", "Shell")]
        : [agent("SouthAgent", "South agent", "Configured model")],
    );
    api.getRepos.mockResolvedValue([1, 2, 3, 4, 5].map(repository));
    api.getKnownRepositories.mockResolvedValue([1, 2, 3, 4, 5, 6, 7, 8].map(repository));
    api.createSession.mockResolvedValue({ sessionId: "created-session" });
  });

  afterEach(() => cleanup());

  it("searches beyond five recent repositories and creates only after explicit review", async () => {
    renderPage();
    await openRepositoryStep();

    expect(screen.getByRole("button", { name: "Select repository Repository 1" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Select repository Repository 8" })).toBeNull();

    fireEvent.change(screen.getByLabelText("Search known repositories"), {
      target: { value: "repository 8" },
    });
    fireEvent.click(await screen.findByRole("button", { name: "Select repository Repository 8" }));

    expect(api.createSession).not.toHaveBeenCalled();
    expect(await screen.findByRole("heading", { name: "Review the new session" })).toBeTruthy();
    expect(screen.getByText("D:\\Repositories\\repository-8")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Create session" }));

    await waitFor(() => expect(api.createSession).toHaveBeenCalledTimes(1));
    expect(api.createSession).toHaveBeenCalledWith(
      "north",
      "D:\\Repositories\\repository-8",
      { agent: "RawCli" },
    );
    expect(await screen.findByText("Opened session")).toBeTruthy();
  });

  it("ignores late agent and repository responses from a previous Director", async () => {
    const northAgents = deferred<ReturnType<typeof agent>[]>();
    const northRecent = deferred<ReturnType<typeof repository>[]>();
    const northKnown = deferred<ReturnType<typeof repository>[]>();
    api.getAgents.mockImplementation((directorId: string) =>
      directorId === "north"
        ? northAgents.promise
        : Promise.resolve([agent("SouthAgent", "South agent", "Configured model")]),
    );
    api.getRepos.mockImplementation((directorId: string) =>
      directorId === "north" ? northRecent.promise : Promise.resolve([repository(20)]),
    );
    api.getKnownRepositories.mockImplementation((directorId: string) =>
      directorId === "north" ? northKnown.promise : Promise.resolve([repository(21)]),
    );

    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: "Select Director South Director" }));

    expect(await screen.findByRole("button", { name: "Select agent South agent" })).toBeTruthy();
    northAgents.resolve([agent("RawCli", "Stale north agent", "Shell")]);
    northRecent.resolve([repository(1)]);
    northKnown.resolve([repository(8)]);

    await waitFor(() => {
      expect(screen.queryByRole("button", { name: "Select agent Stale north agent" })).toBeNull();
    });
    expect(screen.getByRole("button", { name: "Select agent South agent" })).toBeTruthy();
  });

  it("shows an empty agent list explicitly and does not enable review", async () => {
    api.getAgents.mockResolvedValue([]);

    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: "Select Director North Director" }));

    expect(await screen.findByText(/No agents are configured on this Director/i)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Review selections" })).toHaveProperty("disabled", true);
  });

  it("reports an agent loading error and retries before allowing selection", async () => {
    api.getAgents
      .mockRejectedValueOnce(new Error("agent inventory unavailable"))
      .mockResolvedValueOnce([agent("RawCli", "Terminal agent", "Shell")]);

    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: "Select Director North Director" }));

    expect((await screen.findByRole("alert")).textContent).toContain(
      "Could not load agents: agent inventory unavailable",
    );
    expect(screen.getByRole("button", { name: "Review selections" })).toHaveProperty("disabled", true);

    fireEvent.click(screen.getByRole("button", { name: "Retry" }));

    expect(await screen.findByRole("button", { name: "Select agent Terminal agent" })).toBeTruthy();
    expect(api.getAgents).toHaveBeenCalledTimes(2);
  });

  it("allows recent repositories when history fails and reports the partial failure", async () => {
    api.getKnownRepositories.mockRejectedValue(new Error("history unavailable"));

    renderPage();
    await openRepositoryStep();

    expect((await screen.findByRole("alert")).textContent).toContain(
      "Repository history could not be loaded: history unavailable",
    );
    expect(screen.getByRole("button", { name: "Select repository Repository 1" })).toBeTruthy();
  });

  it("keeps the loading state honest while an empty recent source waits for repository history", async () => {
    const history = deferred<ReturnType<typeof repository>[]>();
    api.getRepos.mockResolvedValue([]);
    api.getKnownRepositories.mockImplementation(() => history.promise);

    renderPage();
    await openRepositoryStep();

    expect(await screen.findByText("Loading repositories…")).toBeTruthy();
    expect(screen.queryByText("No repositories are known on this machine yet.")).toBeNull();

    history.resolve([repository(8)]);
    expect(await screen.findByRole("button", { name: "Select repository Repository 8" })).toBeTruthy();
  });

  it("uses an immediate in-flight guard so rapid confirmation taps create one session", async () => {
    const creation = deferred<{ sessionId: string }>();
    api.createSession.mockImplementation(() => creation.promise);

    renderPage();
    await openRepositoryStep();
    fireEvent.click(screen.getByRole("button", { name: "Select repository Repository 1" }));
    const createButton = await screen.findByRole("button", { name: "Create session" });

    fireEvent.click(createButton);
    fireEvent.click(createButton);

    expect(api.createSession).toHaveBeenCalledTimes(1);
    creation.resolve({ sessionId: "created-session" });
    expect(await screen.findByText("Opened session")).toBeTruthy();
  });

  it("reports a blank manual path and accepts a populated manual path", async () => {
    renderPage();
    await openRepositoryStep();

    fireEvent.click(screen.getByRole("button", { name: "Enter a path manually" }));
    fireEvent.click(screen.getByRole("button", { name: "Use this path" }));

    expect((await screen.findByRole("alert")).textContent).toContain(
      "Enter a repository path before continuing.",
    );

    fireEvent.change(screen.getByLabelText("Repository path"), {
      target: { value: "/repos/manual-project" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Use this path" }));
    expect(await screen.findByRole("heading", { name: "Review the new session" })).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Create session" }));
    await waitFor(() => expect(api.createSession).toHaveBeenCalledWith(
      "north",
      "/repos/manual-project",
      { agent: "RawCli" },
    ));
  });

  it("preserves choices and typed input while retrying repository sources", async () => {
    api.getKnownRepositories
      .mockRejectedValueOnce(new Error("history unavailable"))
      .mockResolvedValueOnce([repository(8)]);

    renderPage();
    await openRepositoryStep();
    await screen.findByText(/Repository history could not be loaded/);

    fireEvent.change(screen.getByLabelText("Search known repositories"), {
      target: { value: "repository 1" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Select repository Repository 1" }));
    fireEvent.click(screen.getByRole("button", { name: /Repository Repository 1/ }));
    fireEvent.click(screen.getByRole("button", { name: "Enter a path manually" }));
    fireEvent.change(screen.getByLabelText("Repository path"), {
      target: { value: "/repos/still-typing" },
    });

    fireEvent.click(screen.getByRole("button", { name: "Retry repository sources" }));

    await waitFor(() => expect(api.getKnownRepositories).toHaveBeenCalledTimes(2));
    expect((screen.getByLabelText("Search known repositories") as HTMLInputElement).value).toBe("repository 1");
    expect((screen.getByLabelText("Repository path") as HTMLInputElement).value).toBe("/repos/still-typing");
    expect(screen.getByText("Terminal agent")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Review selections" })).toHaveProperty("disabled", false);
    expect(api.getAgents).toHaveBeenCalledTimes(1);
  });

  it("caps broad search rendering, reports the full match count, and keeps every result reachable", async () => {
    api.getRepos.mockResolvedValue([]);
    api.getKnownRepositories.mockResolvedValue(
      Array.from({ length: 60 }, (_, index) => ({
        name: "Match " + String(index + 1).padStart(2, "0"),
        path: "/repos/match-" + String(index + 1).padStart(2, "0"),
        lastUsed: "2026-08-20T12:00:00Z",
      })),
    );

    renderPage();
    await openRepositoryStep();
    fireEvent.change(screen.getByLabelText("Search known repositories"), { target: { value: "match" } });

    expect(await screen.findByText("Showing 50 of 60 matches. Type more to narrow the results.")).toBeTruthy();
    expect(screen.getAllByRole("button", { name: /Select repository Match/ })).toHaveLength(50);
    expect(screen.queryByRole("button", { name: "Select repository Match 60" })).toBeNull();

    fireEvent.change(screen.getByLabelText("Search known repositories"), { target: { value: "match 60" } });
    expect(await screen.findByRole("button", { name: "Select repository Match 60" })).toBeTruthy();
  });

  it("keeps case-distinct Unix repository paths as separate choices", async () => {
    api.getRepos.mockResolvedValue([]);
    api.getKnownRepositories.mockResolvedValue([
      { name: "Uppercase project", path: "/repos/Project", lastUsed: "2026-08-20T12:00:00Z" },
      { name: "Lowercase project", path: "/repos/project", lastUsed: "2026-08-19T12:00:00Z" },
    ]);

    renderPage();
    await openRepositoryStep();

    expect(await screen.findByRole("button", { name: "Select repository Uppercase project" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Select repository Lowercase project" })).toBeTruthy();
  });
});
