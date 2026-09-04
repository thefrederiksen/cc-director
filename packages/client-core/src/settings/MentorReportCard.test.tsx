// The Mentor report card (devthrottle_internal#1661), rendered against a fake Gateway.
//
// What these prove that a Gateway test cannot: the card SHOWS the account its current answer and SENDS the
// one it clicked. The Gateway's own tests prove the value is stored and served; a card that rendered the box
// ticked for an account that had turned the mentor OFF would leave those green while the person looking at
// Settings believed the opposite of what is true - and the thing they believed they had stopped is a model
// reading their own prompts, which is the one setting on this page where that mistake actually matters.
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MentorReportCard } from "./NotificationsTab";
import { SettingsTabPanel } from "./SettingsTabs";
import { MemoryRouter } from "react-router-dom";

const LABEL = "Send me the mentor report";

// The per-account snapshot GET /gateway/settings answers with. Only the field under test varies, and it is
// OMITTED entirely when undefined - that is the older-Gateway shape, which has to read as on.
function snapshot(mentorReportEnabled?: boolean) {
  const body: Record<string, unknown> = {
    snoozeDefaultMinutes: 60,
    snoozePresets: [15, 60, 240, 480],
    snoozeMaxPresets: 5,
    timeZone: "America/Toronto",
    timeZoneMachineDefault: "America/Toronto",
    dailyReportCadence: "daily",
  };
  if (mentorReportEnabled !== undefined) body.mentorReportEnabled = mentorReportEnabled;
  return body;
}

// Every request the card makes, recorded, so a test can assert what actually went over the wire rather
// than what the component says it did.
let calls: { url: string; method: string; body: unknown }[] = [];

function fakeGateway(enabled?: boolean) {
  vi.stubGlobal(
    "fetch",
    vi.fn(async (url: string, init?: RequestInit) => {
      const method = init?.method ?? "GET";
      const body = init?.body === undefined ? undefined : JSON.parse(String(init.body));
      calls.push({ url, method, body });
      if (url === "/gateway/settings") {
        return new Response(JSON.stringify(snapshot(enabled)), {
          status: 200,
          headers: { "Content-Type": "application/json" },
        });
      }
      if (url === "/gateway/mentor-report") {
        // The Gateway echoes what it applied; the card must render THAT, not what it sent.
        return new Response(JSON.stringify({ enabled: (body as { enabled: boolean }).enabled }), {
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

describe("the Mentor report card", () => {
  it("shows an account that never chose as receiving the report", async () => {
    fakeGateway(true);
    render(<MentorReportCard />);

    expect(((await screen.findByLabelText(LABEL)) as HTMLInputElement).checked).toBe(true);
  });

  it("shows an account that turned it off as turned off", async () => {
    fakeGateway(false);
    render(<MentorReportCard />);

    expect(((await screen.findByLabelText(LABEL)) as HTMLInputElement).checked).toBe(false);
  });

  // A Gateway too old to know the field answers a snapshot without it. That must read as ON, not as off:
  // an account being shown "stopped" for a report that is still being written and sent is the one wrong
  // answer here that nobody can act on, because the page tells them there is nothing to turn off.
  it("reads a snapshot with no mentor field as on rather than off", async () => {
    fakeGateway(undefined);
    render(<MentorReportCard />);

    expect(((await screen.findByLabelText(LABEL)) as HTMLInputElement).checked).toBe(true);
  });

  it("sends the opt-out and confirms in words that the prompts will not be read", async () => {
    fakeGateway(true);
    render(<MentorReportCard />);

    fireEvent.click(await screen.findByLabelText(LABEL));

    await waitFor(() => expect(calls.some((c) => c.url === "/gateway/mentor-report")).toBe(true));
    const write = calls.find((c) => c.url === "/gateway/mentor-report")!;
    expect(write.method).toBe("PUT");
    expect(write.body).toEqual({ enabled: false });
    // A silent success reads as a checkbox that did nothing, so the card owes a sentence - and the sentence
    // has to answer the question somebody turning this off is actually asking.
    expect(await screen.findByText(/prompts will not be read/i)).toBeTruthy();
    expect(((await screen.findByLabelText(LABEL)) as HTMLInputElement).checked).toBe(false);
  });

  it("sends the opt-in when an account that had turned it off turns it back on", async () => {
    fakeGateway(false);
    render(<MentorReportCard />);

    fireEvent.click(await screen.findByLabelText(LABEL));

    await waitFor(() => expect(calls.some((c) => c.url === "/gateway/mentor-report")).toBe(true));
    expect(calls.find((c) => c.url === "/gateway/mentor-report")!.body).toEqual({ enabled: true });
    expect(((await screen.findByLabelText(LABEL)) as HTMLInputElement).checked).toBe(true);
  });

  // NOTE on what this asserts: the shared settings client wraps a failure in a GatewayError carrying no
  // serverReason, so the shared message helper answers with its own sentence for the status rather than the
  // Gateway's words. That is true of every card on this surface. What matters here is that a refused write
  // SAYS SO and leaves the box where the Gateway left it - an opt-out that silently appears to have taken
  // would have somebody believing their prompts are no longer being read when they are.
  it("reports a refused write rather than pretending the change took", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(async (url: string) => {
        if (url === "/gateway/settings") {
          return new Response(JSON.stringify(snapshot(true)), {
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
    render(<MentorReportCard />);

    fireEvent.click(await screen.findByLabelText(LABEL));

    const failure = await screen.findByText(/could not/i);
    expect(failure.textContent).toContain("403");
    expect(((await screen.findByLabelText(LABEL)) as HTMLInputElement).checked).toBe(true);
  });

  // The standing rule is that Settings is one page on two surfaces. The card lives in the shared tab, so
  // this asserts it through the same panel BOTH shells mount - the phone cannot end up without it.
  it("is on the Notifications tab that both shells mount", async () => {
    fakeGateway(true);
    render(
      <MemoryRouter>
        <SettingsTabPanel tab="notifications" />
      </MemoryRouter>,
    );

    expect(await screen.findByRole("heading", { name: /Mentor report/ })).toBeTruthy();
  });
});
