// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render as renderBare, screen, fireEvent, waitFor, cleanup } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { GatewayError } from "@devthrottle/client-core/api/client";
import type { RuleWriteBody, SessionRule } from "@devthrottle/client-core/rules/rulesClient";

// Rendered proof for the Rules page (Session Rules mission, phase 3). The Gateway's half - the model
// call, the validation, the store's gate - is proven server-side; what this pins is the page's half,
// and specifically the four things that decide whether this feature is safe to put in front of
// somebody:
//
//   - saying what you want produces a rule you can READ before anything is stored;
//   - a question comes back as a question, and answering it carries the whole conversation forward
//     so the model can see which question the answer belongs to;
//   - the drafted rule is posted back UNCHANGED, so what was read and what is stored are one
//     document;
//   - a refusal is the Gateway's own sentence, shown verbatim, never flattened into "could not save".
//
// The Gateway client is mocked so the page is driven without a running Gateway.

const { getRules, getRuleFirings, draftRule, createRule, promoteRule, deleteRule } = vi.hoisted(() => ({
  getRules: vi.fn(),
  getRuleFirings: vi.fn(),
  draftRule: vi.fn(),
  createRule: vi.fn(),
  promoteRule: vi.fn(),
  deleteRule: vi.fn(),
}));

vi.mock("@devthrottle/client-core/rules/rulesClient", async () => {
  const real = await vi.importActual<typeof import("@devthrottle/client-core/rules/rulesClient")>(
    "@devthrottle/client-core/rules/rulesClient",
  );
  return {
    getRules,
    getRuleFirings,
    draftRule,
    createRule,
    promoteRule,
    deleteRule,
    // The two describers are pure formatting and are exercised for real - mocking them would leave
    // the page's own words untested.
    describeScope: real.describeScope,
    describeWait: real.describeWait,
  };
});

import { RulesView } from "./RulesView";

// The page mounts links, so it needs a router around it.
function render(ui: React.ReactElement) {
  return renderBare(<MemoryRouter initialEntries={["/rules"]}>{ui}</MemoryRouter>);
}

const THE_OUTAGE_SENTENCE =
  "When the provider stops working, wait a while and then start the session back up.";

const DRAFTED: RuleWriteBody = {
  instruction: THE_OUTAGE_SENTENCE,
  screenDescription: "The session has stopped on an error from the provider rather than on its own work.",
  triggerWords: ["API Error", "overloaded"],
  checks: [],
  scope: "all-sessions",
  cooldownSeconds: 900,
  dailyCap: 6,
};

const READ_BACK =
  "When one of your sessions stops on a provider error I will wait fifteen minutes and then tell it " +
  "to carry on, at most six times a day for any one session.";

const STORED: SessionRule = {
  id: "11111111-1111-1111-1111-111111111111",
  instruction: THE_OUTAGE_SENTENCE,
  screenDescription: DRAFTED.screenDescription,
  triggerWords: DRAFTED.triggerWords,
  checks: [],
  scope: { agent: null, repository: null, machine: null, mission: null },
  cooldownSeconds: 900,
  dailyCap: 6,
  state: "dry_run",
  promotedBy: "",
  createdUtc: "2026-09-03T09:00:00Z",
  updatedUtc: "2026-09-03T09:00:00Z",
};

function say(text: string) {
  fireEvent.change(screen.getByLabelText("What you want the rule to do"), { target: { value: text } });
}

describe("The Rules page", () => {
  beforeEach(() => {
    cleanup();
    vi.clearAllMocks();
    getRules.mockResolvedValue([]);
    getRuleFirings.mockResolvedValue([]);
  });

  it("turns what you said into a rule you can read before anything is stored", async () => {
    draftRule.mockResolvedValue({ proposal: { readBack: READ_BACK, rule: DRAFTED, exampleScreen: "" } });
    render(<RulesView />);

    say(THE_OUTAGE_SENTENCE);
    fireEvent.click(screen.getByRole("button", { name: "Work it out" }));

    await waitFor(() => expect(screen.getByText(READ_BACK)).toBeTruthy());

    // What it would watch for, in the words a person reads - not a rule id and a list of fields.
    expect(screen.getByText("API Error")).toBeTruthy();
    expect(screen.getByText(/every session/)).toBeTruthy();
    expect(screen.getByText(/15 minutes/)).toBeTruthy();

    // NOTHING WAS STORED. Reading a rule and having one is not the same event.
    expect(createRule).not.toHaveBeenCalled();
  });

  it("says out loud that storing it does not turn it on", async () => {
    draftRule.mockResolvedValue({ proposal: { readBack: READ_BACK, rule: DRAFTED, exampleScreen: "" } });
    render(<RulesView />);

    say(THE_OUTAGE_SENTENCE);
    fireEvent.click(screen.getByRole("button", { name: "Work it out" }));

    await waitFor(() => expect(screen.getByText(/Storing this does not turn it on/)).toBeTruthy());
    expect(screen.getByRole("button", { name: "Store it as a dry run" })).toBeTruthy();
  });

  it("posts the drafted rule back unchanged", async () => {
    draftRule.mockResolvedValue({ proposal: { readBack: READ_BACK, rule: DRAFTED, exampleScreen: "" } });
    createRule.mockResolvedValue(STORED);
    render(<RulesView />);

    say(THE_OUTAGE_SENTENCE);
    fireEvent.click(screen.getByRole("button", { name: "Work it out" }));
    await waitFor(() => expect(screen.getByText(READ_BACK)).toBeTruthy());

    fireEvent.click(screen.getByRole("button", { name: "Store it as a dry run" }));

    // THE SAME DOCUMENT. Not a rebuilt one that happens to look similar - if the page assembled its
    // own body, a scope or a check could differ from the one that was read and agreed to.
    await waitFor(() => expect(createRule).toHaveBeenCalledWith(DRAFTED));
  });

  it("asks a question back, and the answer carries the whole conversation forward", async () => {
    draftRule
      .mockResolvedValueOnce({ question: "Should this apply to every session, or only one repository?" })
      .mockResolvedValueOnce({ proposal: { readBack: READ_BACK, rule: DRAFTED, exampleScreen: "" } });
    render(<RulesView />);

    say(THE_OUTAGE_SENTENCE);
    fireEvent.click(screen.getByRole("button", { name: "Work it out" }));

    await waitFor(() =>
      expect(screen.getByText("Should this apply to every session, or only one repository?")).toBeTruthy(),
    );

    say("All of them.");
    fireEvent.click(screen.getByRole("button", { name: "Answer" }));

    await waitFor(() => expect(draftRule).toHaveBeenCalledTimes(2));

    // The question and the answer both go back, in the order they happened - an answer sent without
    // the question it answers is an answer to nothing.
    expect(draftRule.mock.calls[1][0]).toEqual([
      { who: "person", text: THE_OUTAGE_SENTENCE },
      { who: "devthrottle", text: "Should this apply to every session, or only one repository?" },
      { who: "person", text: "All of them." },
    ]);
  });

  /**
   * A REFUSAL IS A GatewayError CARRYING THE GATEWAY'S OWN SENTENCE, and that is what reaches the
   * page. A plain transport failure is a different event and gets the generic unreachable message -
   * writing this test with a bare Error at first is what showed the two apart.
   */
  it("shows the Gateway's refusal in its own words", async () => {
    draftRule.mockRejectedValue(
      new GatewayError(
        400,
        "a rule needs at least one word to watch for, or it would cost a model call on every screen.",
        { reason: "a rule needs at least one word to watch for, or it would cost a model call on every screen." },
      ),
    );
    render(<RulesView />);

    say("do something clever");
    fireEvent.click(screen.getByRole("button", { name: "Work it out" }));

    await waitFor(() => expect(screen.getByText(/at least one word to watch for/)).toBeTruthy());
  });


  // ---- writing one in plain English -----------------------------------------------------------------

  /**
   * THIS IS THE ONLY WAY TO WRITE A RULE ON THIS PAGE TODAY, and the page has to say so rather than
   * leaving somebody believing their rule was checked against a real screen.
   *
   * The Gateway will check trigger words against a captured screen when it is given one - the command
   * line does exactly that (`cc-devthrottle rule add --session`) - but the page cannot get one yet. The
   * intended way is the authoring agent fetching the terminal itself, which is not built.
   */
  it("writes a rule from plain English, and does not claim a check it never made", async () => {
    draftRule.mockResolvedValue({ proposal: { readBack: READ_BACK, rule: DRAFTED, exampleScreen: "" } });
    render(<RulesView />);

    expect(screen.getByText(/describing this from memory/)).toBeTruthy();

    say(THE_OUTAGE_SENTENCE);
    fireEvent.click(screen.getByRole("button", { name: "Work it out" }));

    await waitFor(() => expect(draftRule).toHaveBeenCalled());
    expect(draftRule.mock.calls[0][1]).toBe("");

    await waitFor(() => expect(screen.getByText(READ_BACK)).toBeTruthy());
    expect(screen.queryByText(/checked against the screen you captured/)).toBeNull();
  });

  /** No session picker, and no capture on this page - the screen comes from the agent, once that is
   *  built. Pinned so neither quietly reappears. */
  it("offers no way to capture a screen of its own", () => {
    render(<RulesView />);

    expect(screen.queryByRole("button", { name: "Capture a screen" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Make a rule" })).toBeNull();
  });

  it("shows a stored rule as a dry run that types nothing, and offers to make it live", async () => {
    getRules.mockResolvedValue([STORED]);
    render(<RulesView />);

    await waitFor(() => expect(screen.getByText("Dry run - types nothing")).toBeTruthy());
    expect(screen.getByText(THE_OUTAGE_SENTENCE)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Make it live" })).toBeTruthy();
  });

  /**
   * MAKING A RULE LIVE ASKS FIRST. It is the moment a standing instruction stops being a record of
   * what it would have done and starts typing into real sessions, so it is not a one-click state
   * toggle on a row.
   */
  it("asks before making a rule live", async () => {
    getRules.mockResolvedValue([STORED]);
    promoteRule.mockResolvedValue({ ...STORED, state: "live", promotedBy: "someone" });
    render(<RulesView />);

    await waitFor(() => expect(screen.getByRole("button", { name: "Make it live" })).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Make it live" }));

    expect(screen.getByText("Make this rule live?")).toBeTruthy();
    expect(promoteRule).not.toHaveBeenCalled();
  });

  /**
   * A DECLINE IS A FIRING TOO. A rule that decided not to act must not read the same as a rule that
   * did nothing because something broke, so the record shows the decline and its reason.
   */
  it("shows a decline on the record, with its reason", async () => {
    getRules.mockResolvedValue([STORED]);
    getRuleFirings.mockResolvedValue([
      {
        id: "f1",
        ruleId: STORED.id,
        sessionId: "abc123",
        occurredUtc: "2026-09-03T09:30:00Z",
        screenText: "the docs mention the usage limit notice",
        understanding: "The session is reading documentation that mentions a limit.",
        decision: "decline",
        reason: "the words are in something the session is reading, not in its report of its own state.",
        checksRun: [],
        typedText: "",
        outcome: "nothing was typed.",
        grounding: "grounding: the quoted words are on the screen.",
      },
    ]);
    render(<RulesView />);

    await waitFor(() => expect(screen.getByRole("button", { name: "What it has done" })).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "What it has done" }));

    await waitFor(() => expect(screen.getByText("decline")).toBeTruthy());
    expect(screen.getByText(/not in its report of its own state/)).toBeTruthy();
  });
});
