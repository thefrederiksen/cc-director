// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render as renderBare, screen, fireEvent, waitFor, cleanup, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { GatewayError } from "@devthrottle/client-core/api/client";
import type { RuleFiring, RuleWriteBody, SessionRule } from "@devthrottle/client-core/rules/rulesClient";

// Rendered proof for the Rules page (Session Rules mission, phase 3, reworked in fix round D). The
// Gateway's half - the model call, the screen read, the validation, the store's gate - is proven
// server-side; what this pins is the page's half, and specifically the things that decide whether
// this feature is safe to put in front of somebody:
//
//   - a rule is written about a SESSION the person chose, and the page sends the session id and the
//     every-agent choice - never a screen, and never nothing;
//   - saying what you want produces a rule you can READ before anything is stored;
//   - a question comes back as a question, and answering it carries the whole conversation forward
//     so the model can see which question the answer belongs to;
//   - the drafted rule is posted back UNCHANGED, so what was read and what is stored are one
//     document;
//   - a refusal is the Gateway's own sentence, shown verbatim, never flattened into "could not save";
//   - the page composes no product meaning of its own: the scope and wait words are the Gateway's;
//   - making a rule live shows its dry-run record first, and what is sent describes what was shown.
//
// The Gateway clients are mocked so the page is driven without a running Gateway.

const { getRules, getRuleFirings, draftRule, createRule, promoteRule, deleteRule, listSessions } = vi.hoisted(() => ({
  getRules: vi.fn(),
  getRuleFirings: vi.fn(),
  draftRule: vi.fn(),
  createRule: vi.fn(),
  promoteRule: vi.fn(),
  deleteRule: vi.fn(),
  listSessions: vi.fn(),
}));

vi.mock("@devthrottle/client-core/rules/rulesClient", () => ({
  getRules,
  getRuleFirings,
  draftRule,
  createRule,
  promoteRule,
  deleteRule,
}));

vi.mock("@devthrottle/client-core/api/client", async () => {
  const real = await vi.importActual<typeof import("@devthrottle/client-core/api/client")>(
    "@devthrottle/client-core/api/client",
  );
  return { ...real, listSessions };
});

import { RulesView } from "./RulesView";

// The page mounts links, so it needs a router around it.
function render(ui: React.ReactElement) {
  return renderBare(<MemoryRouter initialEntries={["/rules"]}>{ui}</MemoryRouter>);
}

const THE_OUTAGE_SENTENCE =
  "When the provider stops working, wait a while and then start the session back up.";

const SESSION_ID = "3f1a2b4c-1111-4000-8000-000000000001";

/** THE EXACT TEXT THE RULE TYPES - decided when it was written, and the thing a person is agreeing to. */
const THE_TEXT = "carry on from where you stopped";

const THE_ROSTER = [
  { sessionId: SESSION_ID, name: "refactor the parser", agent: "ClaudeCode", machineName: "SOREN_NORTH" },
  { sessionId: "3f1a2b4c-2222-4000-8000-000000000002", name: "write the docs", agent: "Codex", machineName: "SOREN_SOUTH" },
];

const DRAFTED: RuleWriteBody = {
  instruction: THE_OUTAGE_SENTENCE,
  sessionId: SESSION_ID,
  allAgents: false,
  screenDescription: "The session has stopped on an error from the provider rather than on its own work.",
  textToType: THE_TEXT,
  triggerWords: ["API Error", "overloaded"],
  checks: [],
  scope: { agent: "ClaudeCode" },
  cooldownSeconds: 900,
  dailyCap: 6,
};

const READ_BACK =
  "When one of your sessions stops on a provider error I will wait fifteen minutes and then tell it " +
  "to carry on, at most six times a day for any one session.";

const THE_EXCERPT = "> carry on\nAPI Error: 529 overloaded\n>";

const PROPOSAL = {
  readBack: READ_BACK,
  rule: DRAFTED,
  exampleScreen: THE_EXCERPT,
  scopeLabel: "agent ClaudeCode",
  waitLabel: "15 minutes",
};

const STORED: SessionRule = {
  id: "11111111-1111-1111-1111-111111111111",
  instruction: THE_OUTAGE_SENTENCE,
  screenDescription: DRAFTED.screenDescription,
  textToType: THE_TEXT,
  triggerWords: DRAFTED.triggerWords,
  checks: [],
  scope: { agent: "ClaudeCode", repository: null, machine: null, mission: null },
  scopeLabel: "agent ClaudeCode",
  cooldownSeconds: 900,
  waitLabel: "15 minutes",
  dailyCap: 6,
  state: "dry_run",
  promotedBy: "",
  acknowledgement: "",
  createdUtc: "2026-09-03T09:00:00Z",
  updatedUtc: "2026-09-03T09:00:00Z",
};

const AN_ACT: RuleFiring = {
  id: "f1",
  ruleId: STORED.id,
  sessionId: "abc123",
  occurredUtc: "2026-09-03T09:30:00Z",
  screenText: "API Error: overloaded",
  understanding: "The session stopped on a provider error.",
  decision: "act",
  reason: "the screen shows the provider's own error and no work of the session's.",
  checksRun: [],
  typedText: "",
  outcome: "dry run: nothing was typed.",
  grounding: "grounding: the quoted words are on the screen.",
};

function say(text: string) {
  fireEvent.change(screen.getByLabelText("What you want the rule to do"), { target: { value: text } });
}

/** Choose the first session on the roster through the chooser dialog. */
async function chooseTheSession() {
  fireEvent.click(screen.getByRole("button", { name: "Choose a session" }));
  const row = await screen.findByRole("button", { name: /refactor the parser/ });
  fireEvent.click(row);
  await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
}

describe("The Rules page", () => {
  beforeEach(() => {
    cleanup();
    vi.clearAllMocks();
    getRules.mockResolvedValue([]);
    getRuleFirings.mockResolvedValue([]);
    listSessions.mockResolvedValue(THE_ROSTER);
  });

  // ---- the session it is about -------------------------------------------------------------------------

  /**
   * NO SESSION, NO RULE. The page used to let a rule be written from memory and said so; now the Gateway
   * refuses a request that names no session and the page does not offer one. The button stays off until
   * a session is chosen, and the page says why.
   */
  it("will not work out a rule until a session is chosen, and says why", () => {
    render(<RulesView />);

    say(THE_OUTAGE_SENTENCE);

    expect(screen.getByText(/never from memory/)).toBeTruthy();
    expect((screen.getByRole("button", { name: "Work it out" }) as HTMLButtonElement).disabled).toBe(true);
    expect(draftRule).not.toHaveBeenCalled();
  });

  /**
   * THE SESSION ID AND THE STAR ARE WHAT IS SENT - never a screen. The Gateway reads the screen itself;
   * the page's job is to say which session and whether every agent was chosen.
   */
  it("drafts against the chosen session and sends the every-agent choice as a fact", async () => {
    draftRule.mockResolvedValue({ proposal: PROPOSAL });
    render(<RulesView />);

    await chooseTheSession();
    expect(screen.getByText("refactor the parser")).toBeTruthy();

    say(THE_OUTAGE_SENTENCE);
    fireEvent.click(screen.getByRole("button", { name: "Work it out" }));

    await waitFor(() => expect(draftRule).toHaveBeenCalledTimes(1));
    expect(draftRule.mock.calls[0]).toEqual([
      [{ who: "person", text: THE_OUTAGE_SENTENCE }],
      SESSION_ID,
      false,
    ]);
  });

  it("sends the star when the person chose every agent", async () => {
    draftRule.mockResolvedValue({ proposal: PROPOSAL });
    render(<RulesView />);

    await chooseTheSession();
    fireEvent.click(screen.getByRole("checkbox"));
    say(THE_OUTAGE_SENTENCE);
    fireEvent.click(screen.getByRole("button", { name: "Work it out" }));

    await waitFor(() => expect(draftRule).toHaveBeenCalledTimes(1));
    expect(draftRule.mock.calls[0][2]).toBe(true);
  });

  /** The chooser is a dialog of its own, searchable - never a list stuffed into the composer. */
  it("chooses the session in a dialog that can be filtered", async () => {
    render(<RulesView />);

    fireEvent.click(screen.getByRole("button", { name: "Choose a session" }));
    const dialog = await screen.findByRole("dialog");
    await within(dialog).findByRole("button", { name: /write the docs/ });

    fireEvent.change(within(dialog).getByLabelText("Filter sessions"), { target: { value: "codex" } });

    expect(within(dialog).queryByRole("button", { name: /refactor the parser/ })).toBeNull();
    expect(within(dialog).getByRole("button", { name: /write the docs/ })).toBeTruthy();
  });

  // ---- reading before storing -------------------------------------------------------------------------

  it("turns what you said into a rule you can read before anything is stored", async () => {
    draftRule.mockResolvedValue({ proposal: PROPOSAL });
    render(<RulesView />);

    await chooseTheSession();
    say(THE_OUTAGE_SENTENCE);
    fireEvent.click(screen.getByRole("button", { name: "Work it out" }));

    await waitFor(() => expect(screen.getByText(READ_BACK)).toBeTruthy());

    // What it would watch for, in the words a person reads - the GATEWAY'S words for the scope and the
    // wait, rendered verbatim, and the exact screen excerpt it was checked against.
    expect(screen.getByText("API Error")).toBeTruthy();
    expect(screen.getByText("agent ClaudeCode")).toBeTruthy();
    expect(screen.getByText(/waits 15 minutes/)).toBeTruthy();
    expect(screen.getByText(/API Error: 529 overloaded/)).toBeTruthy();
    expect(screen.getByText(/which the Gateway read itself/)).toBeTruthy();

    // NOTHING WAS STORED. Reading a rule and having one is not the same event.
    expect(createRule).not.toHaveBeenCalled();
  });

  /**
   * THE KEYSTROKE IS SHOWN, VERBATIM, BEFORE ANYTHING ELSE ABOUT THE RULE (phase 1). The text a rule
   * types is the most consequential thing it does and the read-back is what a person confirms - a
   * read-back that described the situation but hid the keystroke asked somebody to approve an action
   * they were not shown. So the proposal shows the exact string, in a monospace element, ahead of the
   * trigger words.
   */
  it("shows the exact text the drafted rule would type, verbatim, before the trigger words", async () => {
    draftRule.mockResolvedValue({ proposal: PROPOSAL });
    render(<RulesView />);

    await chooseTheSession();
    say(THE_OUTAGE_SENTENCE);
    fireEvent.click(screen.getByRole("button", { name: "Work it out" }));
    await waitFor(() => expect(screen.getByText(READ_BACK)).toBeTruthy());

    const typed = screen.getByText(THE_TEXT);
    expect(typed.tagName).toBe("CODE");
    // Ahead of the trigger words in the document, so it is read first.
    const firstWord = screen.getByText("API Error");
    expect(typed.compareDocumentPosition(firstWord) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it("says out loud that storing it does not turn it on", async () => {
    draftRule.mockResolvedValue({ proposal: PROPOSAL });
    render(<RulesView />);

    await chooseTheSession();
    say(THE_OUTAGE_SENTENCE);
    fireEvent.click(screen.getByRole("button", { name: "Work it out" }));

    await waitFor(() => expect(screen.getByText(/Storing this does not turn it on/)).toBeTruthy());
    expect(screen.getByRole("button", { name: "Store it as a dry run" })).toBeTruthy();
  });

  it("posts the drafted rule back unchanged", async () => {
    draftRule.mockResolvedValue({ proposal: PROPOSAL });
    createRule.mockResolvedValue(STORED);
    render(<RulesView />);

    await chooseTheSession();
    say(THE_OUTAGE_SENTENCE);
    fireEvent.click(screen.getByRole("button", { name: "Work it out" }));
    await waitFor(() => expect(screen.getByText(READ_BACK)).toBeTruthy());

    fireEvent.click(screen.getByRole("button", { name: "Store it as a dry run" }));

    // THE SAME DOCUMENT - carrying the session and the star, so the Gateway can ground it again. Not a
    // rebuilt one that happens to look similar: if the page assembled its own body, a scope or a check
    // could differ from the one that was read and agreed to.
    await waitFor(() => expect(createRule).toHaveBeenCalledWith(DRAFTED));
  });

  it("asks a question back, and the answer carries the whole conversation forward", async () => {
    draftRule
      .mockResolvedValueOnce({ question: "Should this apply to every session, or only one repository?" })
      .mockResolvedValueOnce({ proposal: PROPOSAL });
    render(<RulesView />);

    await chooseTheSession();
    say(THE_OUTAGE_SENTENCE);
    fireEvent.click(screen.getByRole("button", { name: "Work it out" }));

    await waitFor(() =>
      expect(screen.getByText("Should this apply to every session, or only one repository?")).toBeTruthy(),
    );

    say("All of them.");
    fireEvent.click(screen.getByRole("button", { name: "Answer" }));

    await waitFor(() => expect(draftRule).toHaveBeenCalledTimes(2));

    // The question and the answer both go back, in the order they happened - an answer sent without
    // the question it answers is an answer to nothing. And the same session, every turn.
    expect(draftRule.mock.calls[1][0]).toEqual([
      { who: "person", text: THE_OUTAGE_SENTENCE },
      { who: "devthrottle", text: "Should this apply to every session, or only one repository?" },
      { who: "person", text: "All of them." },
    ]);
    expect(draftRule.mock.calls[1][1]).toBe(SESSION_ID);
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
        "the drafted rule watches for words that are not on the screen you captured: \"ECONNREFUSED\".",
        { reason: "the drafted rule watches for words that are not on the screen you captured: \"ECONNREFUSED\"." },
      ),
    );
    render(<RulesView />);

    await chooseTheSession();
    say("do something clever");
    fireEvent.click(screen.getByRole("button", { name: "Work it out" }));

    await waitFor(() => expect(screen.getByText(/not on the screen you captured/)).toBeTruthy());
  });

  // ---- the list, and the Gateway's words -------------------------------------------------------------

  it("shows a stored rule as a dry run that types nothing, and offers to make it live", async () => {
    getRules.mockResolvedValue([STORED]);
    render(<RulesView />);

    await waitFor(() => expect(screen.getByText("Dry run - types nothing")).toBeTruthy());
    expect(screen.getByText(THE_OUTAGE_SENTENCE)).toBeTruthy();
    expect(screen.getByRole("button", { name: "Make it live" })).toBeTruthy();
  });

  it("shows the exact text a stored rule types, verbatim", async () => {
    getRules.mockResolvedValue([STORED]);
    render(<RulesView />);

    await waitFor(() => expect(screen.getByText(THE_TEXT)).toBeTruthy());
    expect(screen.getByText(THE_TEXT).tagName).toBe("CODE");
  });

  /**
   * A RULE WITH NOTHING TO TYPE IS SAID TO NEED RE-AUTHORING - the Gateway's own word for it. Such a
   * rule was stored before rules carried their text; the Gateway refuses to fire it and refuses to
   * promote it, and a card that showed it looking like every other rule would hide exactly that.
   */
  it("says a rule served with no text to type needs re-authoring", async () => {
    getRules.mockResolvedValue([{ ...STORED, textToType: "" }]);
    render(<RulesView />);

    await waitFor(() => expect(screen.getByText(/needs re-authoring/)).toBeTruthy());
  });

  /**
   * THE PAGE COMPOSES NO PRODUCT MEANING (fix round D, ruling D8; repository rule 7). The scope and
   * the wait are the Gateway's stamped words, whatever they are - the page does not look at the scope
   * object or the seconds and decide what they mean.
   */
  it("renders the Gateway's scope and wait labels verbatim and composes none of its own", async () => {
    getRules.mockResolvedValue([
      {
        ...STORED,
        scope: { agent: null, repository: null, machine: null, mission: null },
        scopeLabel: "the label the Gateway stamped",
        cooldownSeconds: 7200,
        waitLabel: "the wait the Gateway stamped",
      },
    ]);
    render(<RulesView />);

    await waitFor(() => expect(screen.getByText("the label the Gateway stamped")).toBeTruthy());
    expect(screen.getByText(/the wait the Gateway stamped apart/)).toBeTruthy();
    expect(screen.queryByText(/every session/)).toBeNull();
    expect(screen.queryByText(/2 hours/)).toBeNull();
  });

  it("shows who made a live rule live, and what they agreed to", async () => {
    getRules.mockResolvedValue([
      { ...STORED, state: "live", promotedBy: "dev-ca", acknowledgement: "I have read this rule's dry-run record: 0 firings. I am making it live." },
    ]);
    render(<RulesView />);

    await waitFor(() => expect(screen.getByText("dev-ca")).toBeTruthy());
    expect(screen.getByText(/I have read this rule's dry-run record: 0 firings/)).toBeTruthy();
  });

  // ---- making it live: from in front of the record ---------------------------------------------------

  /**
   * THE PAGE MUST NOT FABRICATE THE ACKNOWLEDGEMENT (fix round D, ruling D5). The old page sent a
   * hard-coded sentence claiming the person had read the dry-run record, whether or not the record had
   * ever been opened. Now the confirmation step SHOWS the dry-run record and the sentence that is sent
   * describes what was actually shown - and this test asserts the value sent, not merely that a dialog
   * opened, because any non-blank constant survives the weaker check.
   */
  it("shows the dry-run record before making a rule live, and sends an acknowledgement that describes it", async () => {
    getRules.mockResolvedValue([STORED]);
    getRuleFirings.mockResolvedValue([AN_ACT]);
    promoteRule.mockResolvedValue({ ...STORED, state: "live", promotedBy: "someone" });
    render(<RulesView />);

    await waitFor(() => expect(screen.getByRole("button", { name: "Make it live" })).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Make it live" }));

    // The record is in front of the person before they can agree to anything.
    await waitFor(() => expect(screen.getByText(/no work of the session's/)).toBeTruthy());
    expect(promoteRule).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole("button", { name: "Make it live, I have read the record" }));

    await waitFor(() => expect(promoteRule).toHaveBeenCalledTimes(1));
    const [id, acknowledgement] = promoteRule.mock.calls[0] as [string, string];
    expect(id).toBe(STORED.id);
    // What is sent describes what was shown: one firing, and its decision.
    expect(acknowledgement).toMatch(/1 firing/);
    expect(acknowledgement).toMatch(/act/);
  });

  it("says so when the record is empty, and sends that", async () => {
    getRules.mockResolvedValue([STORED]);
    getRuleFirings.mockResolvedValue([]);
    promoteRule.mockResolvedValue({ ...STORED, state: "live", promotedBy: "someone" });
    render(<RulesView />);

    await waitFor(() => expect(screen.getByRole("button", { name: "Make it live" })).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Make it live" }));

    await waitFor(() => expect(screen.getByText(/It has not fired yet/)).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Make it live, I have read the record" }));

    await waitFor(() => expect(promoteRule).toHaveBeenCalledTimes(1));
    expect(promoteRule.mock.calls[0][1]).toMatch(/0 firings/);
  });

  /** A record that cannot be read is nothing to agree to: the page shows the reason and sends nothing. */
  it("does not promote when the dry-run record cannot be read", async () => {
    getRules.mockResolvedValue([STORED]);
    getRuleFirings.mockRejectedValue(
      new GatewayError(502, "GET /gateway/rules/{id}/firings returned no firings field, so nothing can be said about it."),
    );
    render(<RulesView />);

    await waitFor(() => expect(screen.getByRole("button", { name: "Make it live" })).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Make it live" }));

    await waitFor(() => expect(screen.getByText(/nothing to show you and nothing to agree to/)).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "Make it live, I have read the record" }));

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
        ...AN_ACT,
        screenText: "the docs mention the usage limit notice",
        understanding: "The session is reading documentation that mentions a limit.",
        decision: "decline",
        reason: "the words are in something the session is reading, not in its report of its own state.",
        outcome: "nothing was typed.",
      },
    ]);
    render(<RulesView />);

    await waitFor(() => expect(screen.getByRole("button", { name: "What it has done" })).toBeTruthy());
    fireEvent.click(screen.getByRole("button", { name: "What it has done" }));

    await waitFor(() => expect(screen.getByText("decline")).toBeTruthy());
    expect(screen.getByText(/not in its report of its own state/)).toBeTruthy();
  });
});
