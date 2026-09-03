import { afterEach, describe, expect, it, vi } from "vitest";
import { createRule, deleteRule, draftRule, getRuleFirings, getRules } from "./rulesClient";

// THE INSTRUMENT MUST NOT FAIL OPEN (fix round D, ruling D8). A Gateway answer that is missing the
// field the client asked for is a broken instrument, not an empty result. Read as an empty list it
// becomes "It has not fired yet" on the page - an absence-shaped check reporting a positive fact when
// the data never arrived. The rules list already refused a missing field; the firings read did not.
// Both are pinned here, against a fake fetch, so the two readers cannot drift apart again.

const realFetch = globalThis.fetch;

afterEach(() => {
  globalThis.fetch = realFetch;
});

function answering(body: unknown): void {
  globalThis.fetch = vi.fn(async () =>
    new Response(JSON.stringify(body), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    }),
  ) as unknown as typeof fetch;
}

describe("the rule readers refuse an answer that is missing the field they asked for", () => {
  it("a firings answer with no firings field is an error, never an empty history", async () => {
    answering({});

    await expect(getRuleFirings("11111111-1111-1111-1111-111111111111")).rejects.toThrow(/firings/);
  });

  it("a rules answer with no rules field is an error, never an empty list", async () => {
    answering({});

    await expect(getRules()).rejects.toThrow(/rules/);
  });

  it("a firings answer with the field is read as the history it carries", async () => {
    answering({ firings: [] });

    expect(await getRuleFirings("11111111-1111-1111-1111-111111111111")).toEqual([]);
  });
});

// THE TEXT A RULE TYPES TRAVELS WITH THE RULE (phase 1). It is decided when the rule is written and
// served on the proposal and on every stored rule, and this client reads it VERBATIM onto the typed
// shape - never trimmed, never rebuilt - so what the page shows is the string the Gateway will type.

const THE_TEXT = "carry on from where you stopped";

const A_DRAFTED_RULE = {
  instruction: "when the provider stops working, carry on",
  sessionId: "3f1a2b4c-1111-4000-8000-000000000001",
  allAgents: false,
  screenDescription: "The session has stopped on an error from the provider.",
  textToType: THE_TEXT,
  triggerWords: ["API Error"],
  checks: [],
  scope: { agent: "Codex" },
  cooldownSeconds: 900,
  dailyCap: 6,
};

describe("the text a rule types is read verbatim off the wire", () => {
  it("a drafted rule's text to type reaches the proposal exactly as served", async () => {
    answering({
      readBack: "When a session stops on a provider error I will tell it to carry on.",
      rule: A_DRAFTED_RULE,
      exampleScreen: "API Error: 529 overloaded",
      scopeLabel: "agent Codex",
      waitLabel: "15 minutes",
    });

    const answer = await draftRule([{ who: "person", text: "carry on" }], A_DRAFTED_RULE.sessionId, false);

    expect(answer.proposal?.rule.textToType).toBe(THE_TEXT);
  });

  it("a stored rule's text to type is read exactly as served", async () => {
    answering({
      rules: [
        {
          ...A_DRAFTED_RULE,
          id: "11111111-1111-1111-1111-111111111111",
          scope: { agent: "Codex", repository: null, machine: null, mission: null },
          scopeLabel: "agent Codex",
          waitLabel: "15 minutes",
          state: "dry_run",
          promotedBy: "",
          acknowledgement: "",
          createdUtc: "2026-09-03T09:00:00Z",
          updatedUtc: "2026-09-03T09:00:00Z",
        },
      ],
    });

    const [rule] = await getRules();

    expect(rule.textToType).toBe(THE_TEXT);
  });
});

// A PRESENT FIELD OF THE WRONG SHAPE IS AS BROKEN AS A MISSING ONE (fix round E, ruling E2). Inspection E
// posted {"rules": null} and watched the client resolve null; a page reading that prints "No rules
// yet" over an answer it could not read. Every reader validates the runtime shape - the container, and
// the required fields inside each record - and each case below sits beside a valid non-empty control.

const A_RULE = {
  id: "11111111-1111-1111-1111-111111111111",
  instruction: "when the limit hits, wait and carry on",
  screenDescription: "a limit notice",
  textToType: "carry on",
  triggerWords: ["usage limit"],
  checks: [],
  scope: { agent: "ClaudeCode", repository: null, machine: null, mission: null },
  scopeLabel: "agent ClaudeCode",
  cooldownSeconds: 600,
  waitLabel: "10 minutes",
  dailyCap: 5,
  state: "dry_run",
  promotedBy: "",
  acknowledgement: "",
  createdUtc: "2026-09-03T09:00:00Z",
  updatedUtc: "2026-09-03T09:00:00Z",
};

const A_FIRING = {
  id: "f1",
  ruleId: A_RULE.id,
  sessionId: "abc123",
  occurredUtc: "2026-09-03T09:30:00Z",
  screenText: "API Error",
  understanding: "a provider error",
  decision: "act",
  reason: "the screen shows the provider's own error.",
  checksRun: [],
  typedText: "",
  outcome: "dry run: nothing was typed.",
  grounding: "grounding: the quoted words are on the screen.",
};

describe("the rule readers refuse an answer whose field is present but not the shape asked for", () => {
  it("rules: null is an error, never an empty list", async () => {
    answering({ rules: null });
    await expect(getRules()).rejects.toThrow(/rules/);
  });

  it("rules: a string where the list should be is an error", async () => {
    answering({ rules: "none" });
    await expect(getRules()).rejects.toThrow(/rules/);
  });

  it("rules: a record missing a required field is an error naming the field", async () => {
    const { triggerWords: _dropped, ...withoutWords } = A_RULE;
    answering({ rules: [withoutWords] });
    await expect(getRules()).rejects.toThrow(/triggerWords/);
  });

  it("rules: a valid non-empty list is read as the rules it carries", async () => {
    answering({ rules: [A_RULE] });
    expect(await getRules()).toEqual([A_RULE]);
  });

  it("firings: null is an error, never an empty history", async () => {
    answering({ firings: null });
    await expect(getRuleFirings(A_RULE.id)).rejects.toThrow(/firings/);
  });

  it("firings: an object where the list should be is an error", async () => {
    answering({ firings: { count: 0 } });
    await expect(getRuleFirings(A_RULE.id)).rejects.toThrow(/firings/);
  });

  it("firings: a record whose decision is not a string is an error naming the field", async () => {
    answering({ firings: [{ ...A_FIRING, decision: 7 }] });
    await expect(getRuleFirings(A_RULE.id)).rejects.toThrow(/decision/);
  });

  it("firings: a valid non-empty history is read as what it carries", async () => {
    answering({ firings: [A_FIRING] });
    expect(await getRuleFirings(A_RULE.id)).toEqual([A_FIRING]);
  });

  it("delete: a deleted flag that is not a boolean is an error, never a client-authored outcome", async () => {
    answering({ deleted: "yes" });
    await expect(deleteRule(A_RULE.id)).rejects.toThrow(/deleted/);
  });

  it("delete: a boolean is read as itself", async () => {
    answering({ deleted: true });
    expect(await deleteRule(A_RULE.id)).toBe(true);
  });

  it("create: a rule that is not an object is an error", async () => {
    answering({ rule: "stored" });
    await expect(createRule({ ...A_RULE, sessionId: "s", allAgents: false })).rejects.toThrow(/rule/);
  });

  it("draft: a proposal whose rule lacks its instruction is an error naming the field", async () => {
    answering({
      readBack: "read back",
      rule: {
        sessionId: "s",
        allAgents: false,
        triggerWords: ["usage limit"],
        checks: [],
        scope: "all-sessions",
        cooldownSeconds: 600,
        dailyCap: 5,
        screenDescription: "x",
        textToType: "carry on",
      },
      exampleScreen: "usage limit",
      scopeLabel: "agent ClaudeCode",
      waitLabel: "10 minutes",
    });
    await expect(draftRule([{ who: "person", text: "wait" }], "s", false)).rejects.toThrow(/instruction/);
  });
});
