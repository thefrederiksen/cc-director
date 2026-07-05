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

/** The dictation glossary, mirroring the C# DictionaryDto: the vocabulary biased into speech-to-text,
 *  the correct-term -> wrong-spellings map fed to the cleanup pass, and named profiles. */
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
