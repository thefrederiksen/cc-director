// The dictation-glossary surface of the Gateway (issue #977, epic #967): the typed, same-origin
// client the React Cockpit's Dictionary page reads and writes. It is the shared-library port of the
// Blazor Cockpit's GatewayClient GetDictionaryAsync / SaveDictionaryAsync, so the desktop React shell
// keeps exactly one copy of the /ingest/dictionary contract.
//
// The Gateway is the single source of truth for this glossary: it is used by phone-recording
// transcription and by live dictation/Speak on every Director connected to this Gateway. The whole
// glossary is PUT back on save and the Gateway returns the re-read dictionary, which the page
// re-renders.
//
// Every request is root-relative to the Gateway front door (never a Director address) and carries the
// same Bearer via authHeaders(). A save throws GatewayError carrying the Gateway's own message on a
// non-2xx so the page shows the real reason - no fallback that hides the problem.
import { authHeaders, GatewayError } from "../api/client";

/** One named cleanup profile in the glossary; only its cleanup-enabled flag is meaningful today. */
export interface DictionaryProfile {
  cleanupEnabled: boolean;
}

/** The dictation glossary, mirroring the C# DictionaryDto: the canonical vocabulary, the
 *  correct-term -> wrong-spellings map, and named profiles. Nothing in here reaches the
 *  speech-to-text provider - the transcriber is given audio only, and both the vocabulary and the
 *  wrong-spellings map are applied by the cleanup pass on the finished transcript (issue 2481). */
export interface Dictionary {
  vocabulary: string[];
  commonMistranscriptions: Record<string, string[]>;
  profiles: Record<string, DictionaryProfile>;
}

// Pull the Gateway's own error text out of a non-2xx body so a save failure shows the real reason.
async function gatewayErrorFrom(res: Response, label: string): Promise<GatewayError> {
  let detail = `${res.status}`;
  try {
    const text = await res.text();
    if (text.length > 0) {
      try {
        const body = JSON.parse(text) as { error?: string; detail?: string };
        detail = body.error ?? body.detail ?? text;
      } catch {
        detail = text;
      }
    }
  } catch {
    /* body unreadable - keep the status code */
  }
  return new GatewayError(res.status, `${label} failed: ${detail}`);
}

// Normalize a possibly-partial body into a full Dictionary so the editor always has the three
// collections to bind to (the Gateway always returns them, but a defensive read keeps the page from
// crashing on an unexpected shape).
function normalize(body: Partial<Dictionary> | null | undefined): Dictionary {
  return {
    vocabulary: body?.vocabulary ?? [],
    commonMistranscriptions: body?.commonMistranscriptions ?? {},
    profiles: body?.profiles ?? {},
  };
}

// GET /ingest/dictionary - the current glossary. Throws on transport failure so the Dictionary page
// surfaces it (no fallback to an empty glossary that could be saved back over the real one).
export async function getDictionary(signal?: AbortSignal): Promise<Dictionary> {
  const res = await fetch("/ingest/dictionary", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "GET /ingest/dictionary");
  return normalize((await res.json()) as Partial<Dictionary>);
}

// PUT /ingest/dictionary (whole glossary) -> the re-read dictionary. Throws on failure with the
// Gateway's message so the Save button can show it.
export async function saveDictionary(dict: Dictionary, signal?: AbortSignal): Promise<Dictionary> {
  const res = await fetch("/ingest/dictionary", {
    method: "PUT",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify(dict),
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "PUT /ingest/dictionary");
  return normalize((await res.json()) as Partial<Dictionary>);
}

// ===== Dictionary suggestions (devthrottle #2075, redesigned in #2115) ====================
// The server SCANS this tenant's stored dictation transcripts - daily just after midnight in the
// tenant's own time zone, or on the page's explicit "Scan now" - mines candidate terms, has a
// language model screen out ordinary vocabulary (in any language), and stores the approved
// result. Reads serve that STORED result; they never mine and never call the model. The client
// is DUMB here: it renders the list, the evidence, the count, the scan time, and the screening
// state exactly as the Gateway computed them, and never re-derives any of them.

/** One wrong spelling and how many times it was seen - the "heard as X (n)" evidence. */
export interface SuggestionVariant {
  heard: string;
  count: number;
}

/** A pending suggestion: the canonical term to add, its wrong spellings, and the counts behind
 *  "wrong 53 of 97 times". */
export interface DictionarySuggestion {
  term: string;
  variants: SuggestionVariant[];
  wrongCount: number;
  totalCount: number;
}

/** GET /ingest/dictionary/suggestions and POST .../scan - the stored scan's approved suggestions,
 *  their count, when the scan ran (null = never scanned), and the Gateway-ruled screening state
 *  (screeningOk false = the screening model was unreachable; screeningError says why). */
export interface SuggestionsResult {
  suggestions: DictionarySuggestion[];
  count: number;
  scannedAtUtc: string | null;
  screeningOk: boolean;
  screeningError: string;
}

/** A dismissed term with its snapshotted evidence and when it was dismissed. */
export interface DismissedTerm {
  term: string;
  variants: SuggestionVariant[];
  wrongCount: number;
  totalCount: number;
  dismissedAtUtc: string;
}

function normalizeSuggestions(body: Partial<SuggestionsResult> | null | undefined): SuggestionsResult {
  const suggestions = body?.suggestions ?? [];
  return {
    suggestions,
    count: body?.count ?? suggestions.length,
    scannedAtUtc: body?.scannedAtUtc ?? null,
    screeningOk: body?.screeningOk ?? true,
    screeningError: body?.screeningError ?? "",
  };
}

// GET /ingest/dictionary/suggestions - the stored scan's suggestions with evidence.
export async function getSuggestions(signal?: AbortSignal): Promise<SuggestionsResult> {
  const res = await fetch("/ingest/dictionary/suggestions", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "GET /ingest/dictionary/suggestions");
  return normalizeSuggestions((await res.json()) as Partial<SuggestionsResult>);
}

// POST /ingest/dictionary/suggestions/scan - run a scan NOW (the page's "Scan now" button). May take
// seconds when there are new candidates to screen (one model call); instant when there is nothing
// new. Throws on failure so the button can show the real reason.
export async function scanSuggestions(signal?: AbortSignal): Promise<SuggestionsResult> {
  const res = await fetch("/ingest/dictionary/suggestions/scan", {
    method: "POST",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "POST /ingest/dictionary/suggestions/scan");
  return normalizeSuggestions((await res.json()) as Partial<SuggestionsResult>);
}

// GET /ingest/dictionary/suggestions/count - just the pending count (the nav-badge poll). Returns 0
// rather than throwing on a transport failure, so a badge poll never breaks the shell.
export async function getSuggestionCount(signal?: AbortSignal): Promise<number> {
  try {
    const res = await fetch("/ingest/dictionary/suggestions/count", {
      method: "GET",
      headers: { Accept: "application/json", ...authHeaders() },
      signal,
    });
    if (!res.ok) return 0;
    const body = (await res.json()) as { count?: number };
    return body.count ?? 0;
  } catch {
    return 0;
  }
}

// POST /ingest/dictionary/suggestions/apply - add the chosen terms (each term + its wrong spellings)
// to the glossary in one press. Returns the updated glossary, which terms were applied, and the
// suggestions that remain.
export interface ApplyResult {
  dictionary: Dictionary;
  applied: string[];
  suggestions: DictionarySuggestion[];
  count: number;
}
export async function applySuggestions(terms: string[], signal?: AbortSignal): Promise<ApplyResult> {
  const res = await fetch("/ingest/dictionary/suggestions/apply", {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ terms }),
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "POST /ingest/dictionary/suggestions/apply");
  const body = (await res.json()) as Partial<ApplyResult>;
  return {
    dictionary: normalize(body.dictionary),
    applied: body.applied ?? [],
    suggestions: body.suggestions ?? [],
    count: body.count ?? 0,
  };
}

// POST /ingest/dictionary/suggestions/dismiss - stop suggesting a term (remembered, until restored).
export async function dismissSuggestion(term: string, signal?: AbortSignal): Promise<number> {
  const res = await fetch("/ingest/dictionary/suggestions/dismiss", {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ term }),
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "POST /ingest/dictionary/suggestions/dismiss");
  const body = (await res.json()) as { count?: number };
  return body.count ?? 0;
}

// GET /ingest/dictionary/dismissed - the dismissed terms with their evidence, for the Restore screen.
export async function getDismissed(signal?: AbortSignal): Promise<DismissedTerm[]> {
  const res = await fetch("/ingest/dictionary/dismissed", {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "GET /ingest/dictionary/dismissed");
  const body = (await res.json()) as { dismissed?: DismissedTerm[] };
  return body.dismissed ?? [];
}

// POST /ingest/dictionary/dismissed/restore - make a dismissed term eligible again.
export async function restoreDismissed(term: string, signal?: AbortSignal): Promise<void> {
  const res = await fetch("/ingest/dictionary/dismissed/restore", {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ term }),
    signal,
  });
  if (!res.ok) throw await gatewayErrorFrom(res, "POST /ingest/dictionary/dismissed/restore");
}
