// Typed Gateway reads for the session Brief (issue #973), the TypeScript twin of the Cockpit's
// DirectorClient.GetBriefAsync / GetSummaryAsync / GetScreenTailAsync degrade path
// (src/CcDirector.Cockpit/Services/DirectorClient.cs). The Brief endpoints are NOT declared in the
// Gateway's OpenAPI document - they ride the generic per-session catch-all proxy (/sessions/{sid}/
// {**rest}) exactly the way history and the prompt/queue verbs do - so, like those, they are read
// with narrow local shapes that mirror the C# DTOs (CcDirector.Gateway.Contracts.BriefResponse /
// SessionSummaryDto). Keep these fields in step with the C# DTOs.
//
// Every request is root-relative to the Gateway front door (Gateway-only-ingress, #967): a Director
// address never reaches the browser. The per-device Bearer rides through the shared authHeaders().
import { GatewayError, authHeaders } from "../api/client";

/**
 * GET /sessions/{sid}/brief - the condenser tier of the Brief. Three blocks sourced from the agent's
 * transcript (never the terminal screen): what the user asked, what the agent did (condensed
 * bullets), and what the agent needs from the user (a verbatim substring of the reply). Mirrors
 * CcDirector.Gateway.Contracts.BriefResponse.
 */
export interface BriefResponse {
  sessionId: string;
  /** "ok" | "no_session_id" | "no_jsonl" | "parse_error". */
  status: string;
  /** Free-text error message when status != "ok". */
  error?: string | null;
  /** Session activity state at response time (Working / WaitingForInput / Idle...). */
  activityState: string;
  /** Widget count in the transcript; the staleness key for the condensation cache. */
  turnCount: number;
  /** When the Director session was created (ISO 8601 UTC). */
  createdAt: string;
  /** The session's goal: the earliest available user prompt (truncated for display). */
  goal?: string | null;
  /** The most recent user prompt - the "YOU ASKED" block, truncated for display. */
  lastAsk?: string | null;
  /** True when the transcript has no assistant reply after the last user prompt: the agent is still
   *  replying, or is blocked in an interactive on-screen prompt the transcript cannot see. */
  replyPending: boolean;
  /** Condensed "CLAUDE DID" bullets for the latest reply. Empty when the condenser was unavailable -
   *  the client then shows fullReply directly. */
  didBullets: string[];
  /** The "NEEDS YOU" text - always a verbatim substring of fullReply (model-extracted and
   *  substring-validated server-side, or the reply's final paragraph). Null when nothing is asked. */
  needsYou?: string | null;
  /** "model" (extracted + validated) | "fallback" (final paragraph) | null. */
  needsYouSource?: string | null;
  /** The agent's latest full reply, verbatim markdown (the [full reply] expander). */
  fullReply?: string | null;
  /** Condenser identity ("openai:gpt-4.1-mini") or "unavailable" - an explicit degrade signal. */
  condenser: string;
  /** When the condensation was generated (ISO 8601 UTC); null when the condenser is unavailable. */
  generatedAt?: string | null;
}

/**
 * GET /sessions/{sid}/summary - the Brief's degrade target on old Directors (they ship
 * lastUserPrompt / lastAssistantText). Mirrors CcDirector.Gateway.Contracts.SessionSummaryDto (only
 * the fields the Brief degrade reads are typed here; the DTO carries more).
 */
export interface SessionSummaryDto {
  sessionId: string;
  activityState: string;
  turnCount: number;
  /** Most recent prompt the user typed into this session (truncated). */
  lastUserPrompt?: string | null;
  /** Most recent text reply the agent wrote (truncated). */
  lastAssistantText?: string | null;
  /** "ok" | "no_session_id" | "no_jsonl" | "parse_error". */
  status: string;
  /** Free-text error message when status != "ok". */
  error?: string | null;
}

/**
 * The session Brief (GET /sessions/{sid}/brief). Returns null on 404 - either an old Director build
 * without the endpoint or a session the Director no longer knows; the caller then degrades to
 * {@link getSummary}. The first call after a new turn runs the Director-side condensation (~1-2s);
 * subsequent calls hit its cache. The fetch is client-side and independent of the terminal stream,
 * so a slow condensation never blocks the live terminal.
 */
export async function getBrief(sessionId: string, signal?: AbortSignal): Promise<BriefResponse | null> {
  const sid = encodeURIComponent(sessionId);
  const res = await fetch(`/sessions/${sid}/brief`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (res.status === 404) return null;
  if (!res.ok) {
    throw new GatewayError(res.status, `GET brief failed: ${res.status}`);
  }
  return (await res.json()) as BriefResponse;
}

/**
 * The transcript summary (GET /sessions/{sid}/summary) - the Brief's degrade target on old
 * Directors. Returns null on 404 (an even older Director, or a session it no longer knows).
 */
export async function getSummary(
  sessionId: string,
  signal?: AbortSignal,
): Promise<SessionSummaryDto | null> {
  const sid = encodeURIComponent(sessionId);
  const res = await fetch(`/sessions/${sid}/summary`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (res.status === 404) return null;
  if (!res.ok) {
    throw new GatewayError(res.status, `GET summary failed: ${res.status}`);
  }
  return (await res.json()) as SessionSummaryDto;
}

// The named HTML entities the terminal buffer grid emits (WebUtility.HtmlEncode on the server side
// produces exactly these); numeric entities (&#39; / &#x27;) are decoded generically below. This is a
// closed, deterministic set - no DOM is needed, so the tail parser is unit-testable in plain Node.
const NAMED_ENTITIES: Record<string, string> = {
  amp: "&",
  lt: "<",
  gt: ">",
  quot: '"',
  apos: "'",
  nbsp: " ",
};

// Decode the HTML entities a server-encoded grid line can contain, the browser-independent twin of
// System.Net.WebUtility.HtmlDecode over this bounded input.
function decodeEntities(text: string): string {
  return text.replace(/&(#x?[0-9a-f]+|[a-z]+);/gi, (whole, body: string) => {
    if (body[0] === "#") {
      const codePoint =
        body[1] === "x" || body[1] === "X"
          ? Number.parseInt(body.slice(2), 16)
          : Number.parseInt(body.slice(1), 10);
      return Number.isNaN(codePoint) ? whole : String.fromCodePoint(codePoint);
    }
    const named = NAMED_ENTITIES[body.toLowerCase()];
    return named ?? whole;
  });
}

/**
 * The last `lines` rows of a session's CURRENT SCREEN grid HTML, as plain text - the Brief's live
 * "what is the agent doing right now" peek while a session works. A pure port of the Cockpit's
 * DirectorClient.GetScreenTailAsync grid-html strip: split on each grid line, strip the span markup,
 * decode entities, drop blank rows, and keep the last `lines`. Exported for unit testing.
 */
export function parseScreenTail(gridHtml: string | null | undefined, lines: number): string {
  if (!gridHtml) return "";
  const rows = gridHtml
    .split('<div class="line">')
    .filter((row) => row.length > 0)
    .map((row) => decodeEntities(row.replace(/<[^>]+>/g, "")).replace(/\s+$/, ""))
    .filter((t) => t.trim().length > 0);
  return rows.slice(Math.max(0, rows.length - lines)).join("\n");
}

/**
 * GET /sessions/{sid}/buffer/html - the parsed current-screen grid, reduced to the last `lines` of
 * plain text (see {@link parseScreenTail}). Reads the server-side parsed grid rather than the linear
 * cleaned byte stream because a TUI's constant repaints flatten into "spinner spinner spinner" noise
 * in the stream while the grid is always the coherent screen. Errors surface to the caller (the tail
 * is best-effort - the Brief keeps its structured content if this fails).
 */
export async function getScreenTail(
  sessionId: string,
  lines: number,
  signal?: AbortSignal,
): Promise<string> {
  const sid = encodeURIComponent(sessionId);
  const res = await fetch(`/sessions/${sid}/buffer/html`, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) {
    throw new GatewayError(res.status, `GET buffer/html failed: ${res.status}`);
  }
  const body = (await res.json().catch(() => ({}))) as { gridHtml?: string };
  return parseScreenTail(body.gridHtml, lines);
}
