// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import {
  loadContractFixtures,
  servedBodyFor,
  browserRenderedAnswer,
  type BrowserRendered,
} from "@devthrottle/client-core/stats/throttleContractFixtures";

// THE CONTRACT ON THE RENDERED PHONE PAGE - THE WHOLE ANSWER (fix-round finding F-01, and F-08's "through
// the real consumer"). Every fixture in the product's tools/throttle-conformance/contract directory is
// served to the REAL page over the real client, and everything the page prints from the figure is read
// back off the DOM: both rings' printed percent, arc length and both counts, every surface segment's
// width, label, count and percent, and the counted total. The result is compared, as one object, with the
// answer the fixture records - the same answer the Cockpit page and the mentor report are held to.

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

function renderAt(url: string) {
  return render(
    <MemoryRouter initialEntries={[url]}>
      <Routes>
        <Route path="/throttle" element={<YourThrottle />} />
      </Routes>
    </MemoryRouter>,
  );
}

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  window.localStorage.clear();
});

const R = 42;
const C = 2 * Math.PI * R;

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

function arcShare(ring: Element): number | null {
  const arc = ring.querySelector(".mthr-ring-arc") as HTMLElement;
  const [filled, whole] = (arc.style.strokeDasharray || "").split(" ").map(Number);
  expect(whole).toBeCloseTo(C, 6);
  return round10(filled / whole);
}

function pct(el: Element): number {
  return Number(((el as HTMLElement).style.width || "0%").replace("%", ""));
}

function pageFromDom(container: HTMLElement) {
  const metrics = Array.from(container.querySelectorAll(".mthr-metric"));
  expect(metrics).toHaveLength(2);
  const [voiceRing, phoneRing] = metrics;
  const legend = (ring: Element) => Array.from(ring.querySelectorAll(".mthr-leg b")).map((b) => count(b.textContent));
  const [voiceTurns, typedTurns] = legend(voiceRing);
  // Broken out surface by surface, exactly as on the Cockpit (owner's ask, 2026-09-06).
  const [phoneTurns, ...restOfPhoneRing] = legend(phoneRing);
  const segments = Array.from(container.querySelectorAll(".mthr-split-seg")).map((seg) => {
    const title = /^(.+): (\d+) \((.+)\)$/.exec(seg.getAttribute("title") ?? "");
    return { label: title?.[1], turns: count(title?.[2]), percent: percent(title?.[3]), share: round10(pct(seg) / 100) };
  });
  const legendSurfaces = Array.from(container.querySelectorAll(".mthr-split-legend .mthr-leg")).map((leg) => ({
    label: (leg.textContent ?? "").replace(leg.querySelector("b")?.textContent ?? "", "").trim(),
    turns: count(leg.querySelector("b")?.textContent),
  }));
  return {
    denominator: count(container.querySelector(".mthr-stat-value")!.textContent),
    voiceTurns, typedTurns, phoneTurns,
    restOfPhoneRing,
    voiceShare: arcShare(voiceRing), phoneShare: arcShare(phoneRing),
    voicePercent: percent(voiceRing.querySelector(".mthr-ring-pct")!.textContent),
    phonePercent: percent(phoneRing.querySelector(".mthr-ring-pct")!.textContent),
    segments, legendSurfaces,
  };
}

describe("the Your Throttle contract, on the rendered phone page - the whole answer", () => {
  for (const fixture of loadContractFixtures()) {
    it(fixture.name.replace(/-/g, " "), async () => {
      vi.stubGlobal("fetch", vi.fn(async () =>
        new Response(JSON.stringify(servedBodyFor(fixture.wire)), { status: 200, headers: { "Content-Type": "application/json" } }),
      ));
      const { container } = renderAt("/throttle?week=2026-W35");
      if (fixture.expected.outcome === "refused") {
        const banner = await screen.findByRole("alert");
        expect(banner.textContent).toMatch(/GET \/stats\/data answered/);
        expect(container.querySelectorAll(".mthr-ring-pct")).toHaveLength(0);
        return;
      }
      if (fixture.expected.outcome === "empty") {
        await screen.findByText(/No turn counted in this window/);
        expect(container.querySelectorAll(".mthr-ring-pct")).toHaveLength(0);
        return;
      }
      const expected: BrowserRendered = browserRenderedAnswer(fixture.expected.rendered!);
      await waitFor(() => expect(container.querySelectorAll(".mthr-ring-pct")).toHaveLength(2));
      expect(pageFromDom(container)).toEqual({
        denominator: expected.denominator,
        voiceTurns: expected.voiceTurns, typedTurns: expected.typedTurns,
        phoneTurns: expected.phoneTurns,
        restOfPhoneRing: expected.surfaces.filter((s) => s.surface !== "phone" && s.turns > 0).map((s) => s.turns),
        voiceShare: expected.voiceShare, phoneShare: expected.phoneShare,
        voicePercent: expected.voicePercent, phonePercent: expected.phonePercent,
        segments: expected.segments.map((s) => ({ label: s.label, turns: s.turns, percent: s.percent, share: s.share })),
        legendSurfaces: expected.segments.map((s) => ({ label: s.label, turns: s.turns })),
      });
    });
  }
});
