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
// Gateway as `scopeLabel` and `waitLabel`, and the page renders them verbatim.
//
// AND IT READS NOTHING IT HAS NOT CHECKED THE SHAPE OF (fix round E, ruling E2). A missing field was
// already an error; a PRESENT field of the wrong shape - `{"rules": null}`, a record with no trigger
// words, a decision that is a number - was read as an empty result and printed as "No rules yet". Every
// answer is now validated at runtime: the container, and every required field inside every record. A
// malformed or version-skewed answer is reported as exactly that, never as the positive fact that
// nothing exists.
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
async function readJson(res: Response, what: string): Promise<unknown> {
  if (!res.ok) throw await GatewayError.from(res, what);
  if (contentType(res) !== "application/json") throw notTheRuleSurface(contentType(res) || "an unlabelled body");
  return (await res.json()) as unknown;
}

// ---- the shape of what came back, checked before anything is believed ------------------------------

/** What a value is, in the words an error names it by. */
function kindOf(value: unknown): string {
  if (value === undefined) return "nothing at all";
  if (value === null) return "null";
  if (Array.isArray(value)) return "a list";
  return typeof value === "object" ? "an object" : typeof value;
}

/** A broken answer: the field named, what was expected, what came. Never an empty result. */
function broken(status: number, what: string, field: string, expected: string, saw: unknown): GatewayError {
  return new GatewayError(
    status,
    `${what} answered with '${field}' as ${kindOf(saw)} where ${expected} was expected, so nothing can be ` +
      "said about it. That is not an empty result; it is an answer this client cannot read.",
  );
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** The field, present and of the kind asked for, or a broken-answer error naming it. */
function need(status: number, what: string, obj: Record<string, unknown>, field: string, kind: "string" | "number" | "boolean"): unknown {
  const value = obj[field];
  if (typeof value !== kind) throw broken(status, what, field, `a ${kind}`, value);
  return value;
}

function needString(status: number, what: string, obj: Record<string, unknown>, field: string): string {
  return need(status, what, obj, field, "string") as string;
}

function needNumber(status: number, what: string, obj: Record<string, unknown>, field: string): number {
  return need(status, what, obj, field, "number") as number;
}

function needBoolean(status: number, what: string, obj: Record<string, unknown>, field: string): boolean {
  return need(status, what, obj, field, "boolean") as boolean;
}

function needList(status: number, what: string, obj: Record<string, unknown>, field: string): unknown[] {
  const value = obj[field];
  if (!Array.isArray(value)) throw broken(status, what, field, "a list", value);
  return value;
}

function needStringList(status: number, what: string, obj: Record<string, unknown>, field: string): string[] {
  const list = needList(status, what, obj, field);
  for (const item of list) if (typeof item !== "string") throw broken(status, what, field, "a list of strings", item);
  return list as string[];
}

function needObject(status: number, what: string, obj: Record<string, unknown>, field: string): Record<string, unknown> {
  const value = obj[field];
  if (!isRecord(value)) throw broken(status, what, field, "an object", value);
  return value;
}

function needRoot(status: number, what: string, body: unknown): Record<string, unknown> {
  if (!isRecord(body)) throw broken(status, what, "the answer", "an object", body);
  return body;
}

/**
 * One part of a scope: a string, or an explicit null for "any".
 *
 * A REQUIRED CHILD THAT IS ABSENT IS A BROKEN ANSWER, AND IS NEVER FILLED IN (fix round F, ruling F2).
 * The served contract requires all four children and the Gateway projects all four, but this used to
 * read `undefined` exactly like a legitimate `null` - so a response omitting `scope.agent` came back
 * with `agent: null`, which is the WIDEST value that part can have. Inventing the permissive value for
 * a field that never arrived is the worst available guess, and it can contradict the Gateway's own
 * stamped `scopeLabel`, which would say the rule is narrow while the reconstructed scope said that part
 * is unrestricted. The key has to be there; then, and only then, null means "any".
 */
function scopePart(status: number, what: string, scope: Record<string, unknown>, field: string): string | null {
  const value = scope[field];
  if (value === null || value === undefined) return null;
  if (typeof value !== "string") throw broken(status, what, `scope.${field}`, "a string or null", value);
  return value;
}

/** A rule as the Gateway serves one, every required field checked. */
function readRule(status: number, what: string, value: unknown): SessionRule {
  if (!isRecord(value)) throw broken(status, what, "rule", "an object", value);
  const scope = needObject(status, what, value, "scope");
  return {
    id: needString(status, what, value, "id"),
    instruction: needString(status, what, value, "instruction"),
    screenDescription: needString(status, what, value, "screenDescription"),
    triggerWords: needStringList(status, what, value, "triggerWords"),
    checks: needStringList(status, what, value, "checks"),
    scope: {
      agent: scopePart(status, what, scope, "agent"),
      repository: scopePart(status, what, scope, "repository"),
      machine: scopePart(status, what, scope, "machine"),
      mission: scopePart(status, what, scope, "mission"),
    },
    scopeLabel: needString(status, what, value, "scopeLabel"),
    cooldownSeconds: needNumber(status, what, value, "cooldownSeconds"),
    waitLabel: needString(status, what, value, "waitLabel"),
    dailyCap: needNumber(status, what, value, "dailyCap"),
    state: needString(status, what, value, "state"),
    promotedBy: needString(status, what, value, "promotedBy"),
    acknowledgement: needString(status, what, value, "acknowledgement"),
    createdUtc: needString(status, what, value, "createdUtc"),
    updatedUtc: needString(status, what, value, "updatedUtc"),
  };
}

/** One firing as the Gateway serves one, every required field checked. */
function readFiring(status: number, what: string, value: unknown): RuleFiring {
  if (!isRecord(value)) throw broken(status, what, "firing", "an object", value);
  const checksRun = needList(status, what, value, "checksRun").map((run) => {
    if (!isRecord(run)) throw broken(status, what, "checksRun", "a list of objects", run);
    return {
      name: needString(status, what, run, "name"),
      arguments: needString(status, what, run, "arguments"),
      answer: needString(status, what, run, "answer"),
    };
  });
  return {
    id: needString(status, what, value, "id"),
    ruleId: needString(status, what, value, "ruleId"),
    sessionId: needString(status, what, value, "sessionId"),
    occurredUtc: needString(status, what, value, "occurredUtc"),
    screenText: needString(status, what, value, "screenText"),
    understanding: needString(status, what, value, "understanding"),
    decision: needString(status, what, value, "decision"),
    reason: needString(status, what, value, "reason"),
    checksRun,
    typedText: needString(status, what, value, "typedText"),
    outcome: needString(status, what, value, "outcome"),
    grounding: needString(status, what, value, "grounding"),
  };
}

/** The drafted rule, in the shape the write route takes, every required field checked. */
function readWriteBody(status: number, what: string, value: unknown): RuleWriteBody {
  if (!isRecord(value)) throw broken(status, what, "rule", "an object", value);
  const scope = value.scope;
  if (typeof scope !== "string" && !isRecord(scope)) throw broken(status, what, "scope", "a string or an object", scope);
  return {
    instruction: needString(status, what, value, "instruction"),
    sessionId: needString(status, what, value, "sessionId"),
    allAgents: needBoolean(status, what, value, "allAgents"),
    screenDescription: needString(status, what, value, "screenDescription"),
    triggerWords: needStringList(status, what, value, "triggerWords"),
    checks: needList(status, what, value, "checks"),
    scope,
    cooldownSeconds: needNumber(status, what, value, "cooldownSeconds"),
    dailyCap: needNumber(status, what, value, "dailyCap"),
  };
}

// ---- the calls --------------------------------------------------------------------------------------

/** GET /gateway/rules - every standing instruction this account has. */
export async function getRules(signal?: AbortSignal): Promise<SessionRule[]> {
  const res = await fetch("/gateway/rules", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  const what = "GET /gateway/rules";
  const body = needRoot(res.status, what, await readJson(res, "read your rules"));
  return needList(res.status, what, body, "rules").map((rule) => readRule(res.status, what, rule));
}

/** GET /gateway/rules/{id}/firings - everything one rule has ever done, newest first. */
export async function getRuleFirings(id: string, signal?: AbortSignal): Promise<RuleFiring[]> {
  const res = await fetch(`/gateway/rules/${encodeURIComponent(id)}/firings`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  const what = "GET /gateway/rules/{id}/firings";
  const body = needRoot(res.status, what, await readJson(res, "read what this rule has done"));
  return needList(res.status, what, body, "firings").map((firing) => readFiring(res.status, what, firing));
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
  const what = "POST /gateway/rules/draft";
  const body = needRoot(res.status, what, await readJson(res, "work out a rule from what you said"));
  if ("rule" in body) {
    return {
      proposal: {
        readBack: needString(res.status, what, body, "readBack"),
        rule: readWriteBody(res.status, what, body.rule),
        exampleScreen: needString(res.status, what, body, "exampleScreen"),
        scopeLabel: needString(res.status, what, body, "scopeLabel"),
        waitLabel: needString(res.status, what, body, "waitLabel"),
      },
    };
  }
  if ("question" in body) return { question: needString(res.status, what, body, "question") };
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
  const what = "POST /gateway/rules";
  const body = needRoot(res.status, what, await readJson(res, "store this rule"));
  return readRule(res.status, what, body.rule);
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
  const what = "POST /gateway/rules/{id}/promote";
  const body = needRoot(res.status, what, await readJson(res, "make this rule live"));
  return readRule(res.status, what, body.rule);
}

/** DELETE /gateway/rules/{id} - the rule goes; its firings stay. The record outlives the rule. */
export async function deleteRule(id: string, signal?: AbortSignal): Promise<boolean> {
  const res = await fetch(`/gateway/rules/${encodeURIComponent(id)}`, {
    method: "DELETE",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  const what = "DELETE /gateway/rules/{id}";
  const body = needRoot(res.status, what, await readJson(res, "delete this rule"));
  return needBoolean(res.status, what, body, "deleted");
}
