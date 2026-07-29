// The Daily report card (issue #1000), rendered against a fake Gateway.
//
// What these prove that a Gateway test cannot: the card SHOWS the account its current answer and SENDS the
// one it clicked. The Gateway's own tests prove the cadence is stored and that an account holding "off" is
// dropped from the recipient list; a card that rendered the wrong radio, or sent nothing, would leave both
// of those green while the person looking at Settings believed the opposite of what is true.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { DailyReportCard } from "./NotificationsTab";
import { SettingsTabPanel } from "./SettingsTabs";
import { MemoryRouter } from "react-router-dom";

// The per-account snapshot GET /gateway/settings answers with. Only the field under test varies.
function snapshot(dailyReportCadence: string) {
  return {
    snoozeDefaultMinutes: 60,
    snoozePresets: [15, 60, 240, 480],
    snoozeMaxPresets: 5,
    timeZone: "America/Toronto",
    timeZoneMachineDefault: "America/Toronto",
    dailyReportCadence,
  };
}

// Every request the card makes, recorded, so a test can assert what actually went over the wire rather
// than what the component says it did.
let calls: { url: string; method: string; body: unknown }[] = [];

function fakeGateway(cadence: string) {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      const method = init?.method ?? "GET";
      const body = init?.body === undefined ? undefined : JSON.parse(String(init.body));
      calls.push({ url, method, body });
      if (url === "/gateway/settings") {
        return new Response(JSON.stringify(snapshot(cadence)), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      }
      if (url === "/gateway/daily-report") {
        // The Gateway echoes what it applied; the card must render THAT, not what it sent.
        return new Response(JSON.stringify({ cadence: (body as { cadence: string }).cadence }), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      }
      throw new Error(`unexpected request: ${method} ${url}`);
    }),
  );
}

beforeEach(() => {
  calls = [];
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe("the Daily report card", () => {
  it("shows an account that never chose as receiving the report", async () => {
    fakeGateway("daily");
    render(<DailyReportCard />);

    const on = (await screen.findByLabelText("Send it every morning")) as HTMLInputElement;
    const off = screen.getByLabelText("Do not send it") as HTMLInputElement;
    expect(on.checked).toBe(true);
    expect(off.checked).toBe(false);
  });

  it("shows an account that turned it off as turned off", async () => {
    fakeGateway("off");
    render(<DailyReportCard />);

    const off = (await screen.findByLabelText("Do not send it")) as HTMLInputElement;
    expect(off.checked).toBe(true);
    expect((screen.getByLabelText("Send it every morning") as HTMLInputElement).checked).toBe(false);
  });

  it("sends the chosen cadence and confirms in words that the email has stopped", async () => {
    fakeGateway("daily");
    render(<DailyReportCard />);

    fireEvent.click(await screen.findByLabelText("Do not send it"));

    await waitFor(() => expect(calls.some((c) => c.url === "/gateway/daily-report")).toBe(true));
    const write = calls.find((c) => c.url === "/gateway/daily-report")!;
    expect(write.method).toBe("PUT");
    expect(write.body).toEqual({ cadence: "off" });
    // A silent success reads as a button that did nothing, so the card owes a sentence.
    expect(await screen.findByText(/will not get the daily report email/i)).toBeTruthy();
    expect((screen.getByLabelText("Do not send it") as HTMLInputElement).checked).toBe(true);
  });

  // NOTE on what this asserts: the shared settings client wraps a failure in a GatewayError carrying no
  // serverReason, so the shared message helper answers with its own sentence for the status rather than the
  // Gateway's words. That is true of every card on this surface, not something this one does; what matters
  // here is that a refused write SAYS SO and leaves the radio where the Gateway left it.
  it("reports a refused write rather than pretending the change took", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async (url: string) => {
        if (url === "/gateway/settings") {
          return new Response(JSON.stringify(snapshot("daily")), {
            status: 200,
            headers: { "Content-Type": "application/json" },
          });
        }
        return new Response(JSON.stringify({ error: "a tenant could not be resolved for this request" }), {
          status: 403,
          headers: { "Content-Type": "application/json" },
        });
      }),
    );
    render(<DailyReportCard />);

    fireEvent.click(await screen.findByLabelText("Do not send it"));

    // A real sentence naming the failure, never a bare number and never silence.
    const failure = await screen.findByText(/could not/i);
    expect(failure.textContent).toContain("403");
    // And it must not have moved the radio to a state the Gateway never accepted.
    expect((screen.getByLabelText("Send it every morning") as HTMLInputElement).checked).toBe(true);
  });

  // The standing rule is that Settings is one page on two surfaces. The card lives in the shared tab, so
  // this asserts it through the same panel BOTH shells mount - the phone cannot end up without it.
  it("is on the Notifications tab that both shells mount", async () => {
    fakeGateway("daily");
    render(
      <MemoryRouter>
        <SettingsTabPanel tab="notifications" />
      </MemoryRouter>,
    );

    expect(await screen.findByRole("heading", { name: /Daily report/ })).toBeTruthy();
  });
});
