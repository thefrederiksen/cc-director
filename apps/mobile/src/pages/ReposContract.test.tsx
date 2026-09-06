// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import {
  loadContractFixtures,
  servedBodyFor,
  browserRenderedAnswer,
} from "@devthrottle/client-core/stats/throttleContractFixtures";

// THE CONTRACT ON THE PHONE'S REPOS PAGE (fix-round finding F-01): the cards are the Gateway's summary and
// every row's printed share and drawn voice split are the row's own served ratios, under both rankings.
// The page used to total the rows and divide; served hostile shares that disagree with the counts would
// then have printed different numbers from the Cockpit's Repos tab over the same figure.

vi.mock("@devthrottle/client-core/api/client", () => ({
  authHeaders: () => ({}),
  gatewayErrorMessage: (e: unknown) => (e instanceof Error ? e.message : String(e)),
  GatewayError: class GatewayError extends Error {
    constructor(public status: number, message: string) {
      super(message);
    }
  },
}));

import { Repos } from "./Repos";

function renderRepos() {
  return render(
    <MemoryRouter initialEntries={["/repos"]}>
      <Routes>
        <Route path="/repos" element={<Repos />} />
      </Routes>
    </MemoryRouter>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function count(text: string | null | undefined): number {
  return Number(String(text ?? "").replace(/,/g, "").trim());
}

function percent(text: string | null | undefined): number | null {
  const t = String(text ?? "").trim();
  return t === "n/a" ? null : Number(t.replace("%", ""));
}

function round10(n: number | null): number | null {
  return n === null ? null : Math.round(n * 1e10) / 1e10;
}

function rowsFromDom(container: HTMLElement) {
  return Array.from(container.querySelectorAll(".repo-item")).map((row) => ({
    name: row.querySelector(".repo-item-name")!.textContent,
    value: count((row.querySelector(".repo-item-val")!.textContent ?? "").split(" - ")[0]),
    share: percent((row.querySelector(".repo-item-share")!.textContent ?? "").replace(" - ", "")),
    voiceWidth: round10(Number(((row.querySelector(".repo-item-voice") as HTMLElement).style.width || "0%").replace("%", "")) / 100),
  }));
}

describe("the Your Throttle contract, on the rendered phone Repos page", () => {
  for (const fixture of loadContractFixtures()) {
    it(fixture.name.replace(/-/g, " "), async () => {
      vi.stubGlobal("fetch", vi.fn(async () =>
        new Response(JSON.stringify(servedBodyFor(fixture.wire)), { status: 200, headers: { "Content-Type": "application/json" } }),
      ));
      const { container } = renderRepos();
      if (fixture.expected.outcome === "refused") {
        const banner = await screen.findByRole("alert");
        expect(banner.textContent).toMatch(/GET \/stats\/data answered/);
        expect(container.querySelectorAll(".repo-item")).toHaveLength(0);
        return;
      }
      if (fixture.expected.outcome === "empty") {
        await waitFor(() => expect(container.querySelector(".thr-caveats")).not.toBeNull());
        expect(container.querySelectorAll(".repo-item")).toHaveLength(0);
        expect(container.querySelectorAll(".thr-card")).toHaveLength(0);
        return;
      }
      const expected = browserRenderedAnswer(fixture.expected.rendered!);
      await waitFor(() => expect(container.querySelectorAll(".repo-item").length).toBeGreaterThan(0));

      const s = expected.reposSummary;
      const cards = Array.from(container.querySelectorAll(".thr-card-value")).map((el) => el.textContent);
      expect(cards).toEqual([String(s.repoCount), s.totalTurns.toLocaleString(), s.topPercent === null ? "n/a" : `${s.topPercent}%`]);
      const caption = container.querySelector(".thr-caption")!.textContent ?? "";
      if (s.topRepoName !== null) expect(caption).toContain(`${s.topRepoName} leads;`);
      expect(caption).toContain(`${s.totalSessions} session${s.totalSessions === 1 ? "" : "s"} in total.`);

      expect(rowsFromDom(container)).toEqual(expected.repos.map((row) => ({
        name: row.repoName, value: row.turns, share: row.turnPercent, voiceWidth: row.voiceShare,
      })));
      fireEvent.click(within(container.querySelector(".repos-seg")! as HTMLElement).getByRole("tab", { name: "Sessions" }));
      expect(rowsFromDom(container).map((r) => ({ value: r.value, share: r.share }))).toEqual(
        expected.repos.map((row) => ({ value: row.sessions, share: row.sessionPercent })),
      );
    });
  }
});
