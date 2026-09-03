// The standing instructions an account gives about its sessions, and the record of every time one
// fired (the Session Rules mission). The typed, same-origin client the Rules page reads and writes.
//
// A rule is an INSTRUCTION, not a form. You say what you want in ordinary words and name the session it
// is happening on; a model on the Gateway reads that session's screen and works out what the product
// has to hold - what the screen looks like when it applies, the cheap words that make a screen worth a
// closer look, any check the act depends on, which sessions, and the two ceilings - and hands the whole
// thing back for you to read before anything is stored.
//
// THE SCREEN IS NEVER SENT FROM HERE (fix round D, ruling D2). This client names a session; the Gateway
// reads the screen itself, takes the agent and the machine from its own roster, and REFUSES a request
// that names no session. There is no way to write a rule from memory, because that was the path on
// which the trigger words were a guess.
//
// THE THREE CALLS ARE DELIBERATELY SEPARATE, and the separation is the safety rather than the
// plumbing. `draftRule` stores NOTHING. `createRule` stores what you confirmed - and the Gateway reads
// the session's screen AGAIN and checks every trigger word before it stores anything - and it has no
// way to create anything but a dry-run rule, which types nothing and only records what it would have
// done. `promoteRule` is a person deciding it may act for real. Two people-shaped steps sit between a
// sentence and the first keystroke, and this client cannot skip either.
//
// The body `draftRule` returns under `rule` is EXACTLY the body `createRule` takes, and it carries the
// session it was grounded in and whether you said every agent, so confirming a drafted rule is posting
// it back unchanged. Do not rebuild it field by field on the way through: the point of the shape is
// that what was read and what is stored cannot differ.
//
// THIS CLIENT DECIDES NOTHING (repository rule 7, and fix round D, ruling D8). The words a person reads
// for a rule's scope and its wait - "every session", "10 minutes" - are stamped onto the rule by the
// Gateway as `scopeLabel` and `waitLabel`, and the page renders them verbatim. It used to compose them
// here, and again in the page, and the command line composed its own, so two clients could disagree
// about one stored state.
import { authHeaders, GatewayError } from "../api/client";

/** Which sessions a rule may act on. A null part means "any". All four null is every session. */
export interface RuleScope {
  agent: string | null;
  repository: string | null;
  machine: string | null;
  mission: string | null;
}

/** One standing instruction, as the account reads it. */
export interface SessionRule {
  id: string;
  /** THE AUTHORITY: the sentence you said, stored in your own words. */
  instruction: string;
  /** What it is watching for, in plain words, as the model understood you. */
  screenDescription: string;
  /**
   * THE EXACT TEXT IT TYPES when it acts (phase 1) - decided when the rule was written, confirmed by
   * the person, and served with the rule so every client shows it verbatim. Empty on a rule stored
   * before rules carried it; the Gateway refuses to fire or promote such a rule until it is re-authored.
   */
  textToType: string;
  /** The cheap first filter: unless one of these is on the screen, nothing further happens. */
  triggerWords: string[];
  /** The verified checks this rule runs, each already rendered for reading by the Gateway. */
  checks: string[];
  scope: RuleScope;
  /** THE FINISHED WORDS for the scope, stamped by the Gateway. Render verbatim. */
  scopeLabel: string;
  cooldownSeconds: number;
  /** THE FINISHED WORDS for the wait, stamped by the Gateway ("10 minutes"). Render verbatim. */
  waitLabel: string;
  dailyCap: number;
  /** "dry_run" or "live". Every rule starts in dry run and only a person moves it. */
  state: string;
  /** Who made it live. Empty for exactly as long as it is in dry run. */
  promotedBy: string;
  /** What that person said they were agreeing to, verbatim. Empty while in dry run. */
  acknowledgement: string;
  createdUtc: string;
  updatedUtc: string;
}

/** One check that ran during a firing: which one, with what arguments, what it answered. */
export interface RuleCheckRun {
  name: string;
  arguments: string;
  answer: string;
}

/**
 * One firing of one rule. THE RECORD IS THE PRODUCT: a rule that acts while you are asleep is only
 * worth having if the morning says exactly what happened - so a DECLINE is a firing too, and so is a
 * rule that gave up because the screen moved underneath it.
 */
export interface RuleFiring {
  id: string;
  ruleId: string;
  sessionId: string;
  occurredUtc: string;
  screenText: string;
  understanding: string;
  /** "act", "decline", "abandoned" or "refused". */
  decision: string;
  reason: string;
  checksRun: RuleCheckRun[];
  /** What was typed. Always empty for a dry-run firing. */
  typedText: string;
  outcome: string;
  /** What checking the stated reason against the screen found. Never blank. */
  grounding: string;
}

/** One thing said while a rule was being worked out. */
export interface RuleDraftTurn {
  who: "person" | "devthrottle";
  text: string;
}

/**
 * The rule the Gateway drafted, in the exact shape `createRule` takes. Passed straight back through
 * without being rebuilt - see the note at the top of this file. It names the session it was grounded
 * in and whether every agent was chosen, because the write gate runs the same grounding again.
 */
export interface RuleWriteBody {
  instruction: string;
  sessionId: string;
  allAgents: boolean;
  screenDescription: string;
  /** The text the rule will type, decided at authoring and shown for the person to confirm. */
  textToType: string;
  triggerWords: string[];
  checks: unknown[];
  scope: unknown;
  cooldownSeconds: number;
  dailyCap: number;
}

/** A rule to read and confirm, with the read-back, the labels, and the exact screen excerpt it was
 *  checked against. */
export interface RuleProposal {
  readBack: string;
  rule: RuleWriteBody;
  /** The exact excerpt of the session's screen the model was shown and every trigger word was checked
   *  against - the Gateway's own reading, never something this client sent. */
  exampleScreen: string;
  /** The finished words for the unstored rule's scope, stamped by the Gateway. */
  scopeLabel: string;
  /** The finished words for the unstored rule's wait, stamped by the Gateway. */
  waitLabel: string;
}

/**
 * What one authoring turn answered. Exactly one of the two is set, and a QUESTION is a first-class
 * answer rather than a failure: a model that does not know which sessions a rule is for has to be
 * able to ask, or it will pick the widest scope it can and hand back a rule nobody asked for.
 */
export interface RuleDraftAnswer {
  proposal?: RuleProposal;
  /** The one thing that has to be answered before a rule can be written. */
  question?: string;
}

// A 2XX IS NOT PROOF THE GATEWAY UNDERSTOOD THE REQUEST. The Gateway serves this app at "/" and
// falls UNKNOWN page paths back to index.html, so a Gateway from before Session Rules answers
// /gateway/rules with 200 and this app's own HTML shell rather than a 404. Believed at face value,
// the page would render an empty rule list - which reads as "you have no rules" and is a different
// statement from "this Gateway cannot hold rules at all".
function notTheRuleSurface(saw: string): GatewayError {
  return new GatewayError(
    502,
    `This Gateway does not serve session rules yet - it answered with ${saw} instead of rule data, ` +
      "which happens when it is running a build from before rules existed. Upgrade or redeploy the " +
      "Gateway.",
  );
}

function contentType(res: Response): string {
  return (res.headers.get("Content-Type") ?? "").split(";")[0].trim().toLowerCase();
}

// A REFUSAL COMES BACK AS A REFUSAL. The Gateway states in plain English why it would not store a
// rule - which check does not exist, which value was missing - and that sentence is what the page
// shows. Every failure here goes through GatewayError.from, which is the ONE place that reads the
// server's reason out of the body and puts it on `serverReason`, where gatewayErrorMessage finds it.
// Building the error by hand instead is exactly the defect issue #2189 was about: the reason is one
// hop from being shown and gets dropped, and the person is left with a status code. A page test
// caught this file doing it.
//
// `what` names the action in the words a person would use for it, never the method and the path.
async function readJson<T>(res: Response, what: string): Promise<T> {
  if (!res.ok) throw await GatewayError.from(res, what);
  if (contentType(res) !== "application/json") throw notTheRuleSurface(contentType(res) || "an unlabelled body");
  return (await res.json()) as T;
}

// A MISSING FIELD IS A BROKEN INSTRUMENT, NOT AN EMPTY RESULT (fix round D, ruling D8). An answer
// without the field this client asked for is reported as exactly that - never read as an empty list
// that then becomes "No rules yet" or "It has not fired yet" on the page, which would be an absence-
// shaped check reporting a positive fact when the data never arrived.
function missing(res: Response, what: string, field: string): GatewayError {
  return new GatewayError(res.status, `${what} returned no ${field} field, so nothing can be said about it.`);
}

/** GET /gateway/rules - every standing instruction this account has. */
export async function getRules(signal?: AbortSignal): Promise<SessionRule[]> {
  const res = await fetch("/gateway/rules", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  const body = await readJson<{ rules?: SessionRule[] }>(res, "read your rules");
  if (body.rules === undefined) throw missing(res, "GET /gateway/rules", "rules");
  return body.rules;
}

/** GET /gateway/rules/{id}/firings - everything one rule has ever done, newest first. */
export async function getRuleFirings(id: string, signal?: AbortSignal): Promise<RuleFiring[]> {
  const res = await fetch(`/gateway/rules/${encodeURIComponent(id)}/firings`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  const body = await readJson<{ firings?: RuleFiring[] }>(res, "read what this rule has done");
  if (body.firings === undefined) throw missing(res, "GET /gateway/rules/{id}/firings", "firings");
  return body.firings;
}

/**
 * POST /gateway/rules/draft - say what you want, name the session it is happening on; get a rule to
 * read, or one question, back.
 *
 * THIS STORES NOTHING. Pass the whole conversation so far, including anything the Gateway asked and
 * what you answered, so the model can see the question its answer belongs to. The Gateway reads the
 * named session's screen itself and REFUSES any trigger word that is not on it, so a rule that would
 * never have fired is caught while you are still looking at it. `allAgents` is the star: you saying
 * this rule is for every agent rather than the named session's agent, which is the default.
 */
export async function draftRule(
  turns: RuleDraftTurn[],
  sessionId: string,
  allAgents: boolean,
  signal?: AbortSignal,
): Promise<RuleDraftAnswer> {
  const res = await fetch("/gateway/rules/draft", {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ turns, sessionId, allAgents }),
    signal,
  });
  const body = await readJson<{
    readBack?: string;
    rule?: RuleWriteBody;
    question?: string;
    exampleScreen?: string;
    scopeLabel?: string;
    waitLabel?: string;
  }>(res, "work out a rule from what you said");
  if (body.rule !== undefined && body.readBack !== undefined) {
    if (body.exampleScreen === undefined) throw missing(res, "POST /gateway/rules/draft", "exampleScreen");
    if (body.scopeLabel === undefined) throw missing(res, "POST /gateway/rules/draft", "scopeLabel");
    if (body.waitLabel === undefined) throw missing(res, "POST /gateway/rules/draft", "waitLabel");
    return {
      proposal: {
        readBack: body.readBack,
        rule: body.rule,
        exampleScreen: body.exampleScreen,
        scopeLabel: body.scopeLabel,
        waitLabel: body.waitLabel,
      },
    };
  }
  if (body.question !== undefined) return { question: body.question };
  throw new GatewayError(res.status, "The Gateway answered without a rule, a question or a reason.");
}

/**
 * POST /gateway/rules - store the rule you confirmed. It is ALWAYS stored in dry run: there is no
 * argument here that could make it live, because there is none on the Gateway either. The body
 * carries the session it was grounded in, and the Gateway reads that screen again before storing.
 */
export async function createRule(rule: RuleWriteBody, signal?: AbortSignal): Promise<SessionRule> {
  const res = await fetch("/gateway/rules", {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify(rule),
    signal,
  });
  const body = await readJson<{ rule?: SessionRule }>(res, "store this rule");
  if (!body.rule) throw missing(res, "POST /gateway/rules", "rule");
  return body.rule;
}

/**
 * POST /gateway/rules/{id}/promote - a person takes the rule out of dry run.
 *
 * The acknowledgement is REQUIRED by the Gateway and is not a formality: it is persisted on the rule
 * and served back as `acknowledgement`, beside the person's name, for as long as the rule is live. So
 * what is sent here has to describe what the person was actually shown - the page builds it from the
 * dry-run record it put in front of them, never from a constant.
 */
export async function promoteRule(
  id: string,
  acknowledgement: string,
  signal?: AbortSignal,
): Promise<SessionRule> {
  const res = await fetch(`/gateway/rules/${encodeURIComponent(id)}/promote`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ acknowledgement }),
    signal,
  });
  const body = await readJson<{ rule?: SessionRule }>(res, "make this rule live");
  if (!body.rule) throw missing(res, "POST /gateway/rules/{id}/promote", "rule");
  return body.rule;
}

/** DELETE /gateway/rules/{id} - the rule goes; its firings stay. The record outlives the rule. */
export async function deleteRule(id: string, signal?: AbortSignal): Promise<boolean> {
  const res = await fetch(`/gateway/rules/${encodeURIComponent(id)}`, {
    method: "DELETE",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  const body = await readJson<{ deleted?: boolean }>(res, "delete this rule");
  if (body.deleted === undefined) throw missing(res, "DELETE /gateway/rules/{id}", "deleted");
  return body.deleted === true;
}
