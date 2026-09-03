// The standing instructions an account gives about its sessions, and the record of every time one
// fired (the Session Rules mission). The typed, same-origin client the Rules page reads and writes.
//
// A rule is an INSTRUCTION, not a form. You say what you want in ordinary words; a model on the
// Gateway works out what the product has to hold - what the screen looks like when it applies, the
// cheap words that make a screen worth a closer look, any check the act depends on, which sessions,
// and the two ceilings - and hands the whole thing back for you to read before anything is stored.
//
// THE THREE CALLS ARE DELIBERATELY SEPARATE, and the separation is the safety rather than the
// plumbing. `draftRule` stores NOTHING. `createRule` stores what you confirmed, and the Gateway has
// no way to create anything but a dry-run rule, which types nothing and only records what it would
// have done. `promoteRule` is a person deciding it may act for real. Two people-shaped steps sit
// between a sentence and the first keystroke, and this client cannot skip either.
//
// The body `draftRule` returns under `rule` is EXACTLY the body `createRule` takes, so confirming a
// drafted rule is posting it back unchanged. Do not rebuild it field by field on the way through:
// the point of the shape is that what was read and what is stored cannot differ.
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
  cooldownSeconds: number;
  dailyCap: number;
  /** "dry_run" or "live". Every rule starts in dry run and only a person moves it. */
  state: string;
  /** Who made it live. Empty for exactly as long as it is in dry run. */
  promotedBy: string;
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
 * without being rebuilt - see the note at the top of this file.
 */
export interface RuleWriteBody {
  instruction: string;
  screenDescription: string;
  triggerWords: string[];
  checks: unknown[];
  scope: unknown;
  cooldownSeconds: number;
  dailyCap: number;
}

/**
 * What one authoring turn answered. Exactly one of the three is set, and a QUESTION is a first-class
 * answer rather than a failure: a model that does not know which sessions a rule is for has to be
 * able to ask, or it will pick the widest scope it can and hand back a rule nobody asked for.
 */
export interface RuleDraftAnswer {
  /** A rule to read and confirm, with the read-back and the screen it was made from. */
  proposal?: { readBack: string; rule: RuleWriteBody; exampleScreen: string };
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

/** GET /gateway/rules - every standing instruction this account has. */
export async function getRules(signal?: AbortSignal): Promise<SessionRule[]> {
  const res = await fetch("/gateway/rules", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  const body = await readJson<{ rules?: SessionRule[] }>(res, "read your rules");
  if (body.rules === undefined) throw new GatewayError(res.status, "GET /gateway/rules returned no rules field");
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
  return body.firings ?? [];
}

/**
 * GET /sessions/{id}/buffer - the terminal text a session is showing right now.
 *
 * THIS IS WHAT MAKES A RULE REAL. Written without one, a rule's trigger words are the model's guess at
 * what a screen SAYS - and a guess is plausible and wrong: asked about a provider outage with no screen,
 * a live model proposed ECONNREFUSED and 429, strings a coding agent may never print. A rule watching for
 * a word that never appears never fires, and looks entirely correct sitting in the list. Capture the
 * screen you are actually looking at and the words come from the text instead.
 */
export async function captureSessionScreen(
  sessionId: string,
  lines = 60,
  signal?: AbortSignal,
): Promise<string> {
  const res = await fetch(`/sessions/${encodeURIComponent(sessionId)}/buffer?lines=${lines}`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  const body = await readJson<{ text?: string }>(res, "read this session's screen");
  const text = (body.text ?? "").trim();
  if (text.length === 0) {
    // An empty screen is not a capture. Returning it would send the model away to guess, which is the
    // exact thing capturing exists to stop - and the person would never know that is what happened.
    throw new GatewayError(502, "That session's screen came back empty, so there is nothing to capture.");
  }
  return text;
}

/**
 * POST /gateway/rules/draft - say what you want; get a rule to read, or one question, back.
 *
 * THIS STORES NOTHING. Pass the whole conversation so far, including anything the Gateway asked and
 * what you answered, so the model can see the question its answer belongs to. Pass the captured screen
 * whenever there is one: the Gateway then REFUSES any trigger word that is not on it, so a rule that
 * would never have fired is caught while you are still looking at it.
 */
export async function draftRule(
  turns: RuleDraftTurn[],
  screen?: string,
  signal?: AbortSignal,
): Promise<RuleDraftAnswer> {
  const res = await fetch("/gateway/rules/draft", {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ turns, screen: screen ?? "" }),
    signal,
  });
  const body = await readJson<{
    readBack?: string; rule?: RuleWriteBody; question?: string; exampleScreen?: string;
  }>(res, "work out a rule from what you said");
  if (body.rule !== undefined && body.readBack !== undefined) {
    return {
      proposal: { readBack: body.readBack, rule: body.rule, exampleScreen: body.exampleScreen ?? "" },
    };
  }
  if (body.question !== undefined) return { question: body.question };
  throw new GatewayError(res.status, "The Gateway answered without a rule, a question or a reason.");
}

/**
 * POST /gateway/rules - store the rule you confirmed. It is ALWAYS stored in dry run: there is no
 * argument here that could make it live, because there is none on the Gateway either.
 */
export async function createRule(rule: RuleWriteBody, signal?: AbortSignal): Promise<SessionRule> {
  const res = await fetch("/gateway/rules", {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify(rule),
    signal,
  });
  const body = await readJson<{ rule?: SessionRule }>(res, "store this rule");
  if (!body.rule) throw new GatewayError(res.status, "POST /gateway/rules stored nothing it could return.");
  return body.rule;
}

/**
 * POST /gateway/rules/{id}/promote - a person takes the rule out of dry run.
 *
 * The acknowledgement is REQUIRED by the Gateway and is not a formality: it is what the record shows
 * the person agreed to, beside their name, for as long as the rule is live.
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
  if (!body.rule) throw new GatewayError(res.status, "The Gateway promoted nothing it could return.");
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
  return body.deleted === true;
}

/** How a rule's scope reads on a page. Every session is the honest answer when nothing is named. */
export function describeScope(scope: RuleScope | undefined): string {
  if (!scope) return "every session";
  const parts: string[] = [];
  if (scope.agent) parts.push(`agent ${scope.agent}`);
  if (scope.repository) parts.push(`repository ${scope.repository}`);
  if (scope.machine) parts.push(`machine ${scope.machine}`);
  if (scope.mission) parts.push(`mission ${scope.mission}`);
  return parts.length === 0 ? "every session" : parts.join(", ");
}

/** A ceiling in the words a person uses for it. */
export function describeWait(seconds: number): string {
  if (seconds >= 3600 && seconds % 3600 === 0) return `${seconds / 3600} hour${seconds === 3600 ? "" : "s"}`;
  if (seconds >= 60 && seconds % 60 === 0) return `${seconds / 60} minute${seconds === 60 ? "" : "s"}`;
  return `${seconds} second${seconds === 1 ? "" : "s"}`;
}
