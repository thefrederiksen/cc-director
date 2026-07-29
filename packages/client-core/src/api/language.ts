// The spoken-language client for the Language tab, shared by the Cockpit and the phone (issue #1010).
// Same-origin against the Gateway front door with the injected Bearer, exactly like ai.ts.
//
// EVERY CALL RETURNS THE WHOLE DOCUMENT, and the tab replaces its state with it. That is deliberate: the
// Gateway owns every verdict on this screen - which voices a language offers, what each one is called,
// which voice the account will actually be spoken with after a language change, and the sample sentence -
// so there is nothing for this module to compute and nothing for the tab to re-derive. The reverted
// multilingual build decided those consequences in the browser, from a model catalog the hosted Gateway
// refuses to serve, and shipped with an empty list and a guard that never fired
// (devthrottle_internal#547).
//
// Note what is absent: there is no speech MODEL anywhere in these types. A language chooses a voice inside
// the one engine that already serves English. If a future change adds a model to this file, that is the
// reverted failure returning.
import { authHeaders, GatewayError } from "./client";

/** One voice offered for a language - the id to send back, and the line to show. Both come from the
 *  Gateway; the label is never assembled here. */
export interface VoiceOption {
  id: string;
  label: string;
}

/** One language the product speaks, with everything the screen needs to draw its row. */
export interface SpokenLanguageOption {
  /** The short code sent back on a write: "en", "fr", "es". */
  code: string;
  /** The language's name, as the choice's own label. */
  label: string;
  /** The second line under the choice: "Default" for English, otherwise the language's own name for
   *  itself. A Gateway-folded display string, not a flag this client interprets. */
  note: string;
  /** The sentence Play sample speaks, in THIS language, and shown beside the button so you can see what
   *  you are about to hear. */
  sample: string;
  /** The voices for this language, already filtered - one entry for French, three for Spanish,
   *  twenty-eight for English. A single-entry list is normal and still gets a visible control. */
  voices: VoiceOption[];
}

/** The whole Language tab in one document (GET/PUT /gateway/spoken-language). */
export interface SpokenLanguageSnapshot {
  /** The language this account is spoken to in. */
  language: string;
  /** The voice this account will ACTUALLY be spoken with - read through the same resolver the wingman
   *  reads, so the screen cannot show a voice the sessions are not using. */
  voice: string;
  /** Every language offered, English first. */
  languages: SpokenLanguageOption[];
}

async function readJson(res: Response, path: string): Promise<SpokenLanguageSnapshot> {
  if (!res.ok) {
    const err = (await res.json().catch(() => ({}))) as { error?: string };
    throw new GatewayError(res.status, err.error ?? `${path} failed: ${res.status}`);
  }
  return (await res.json()) as SpokenLanguageSnapshot;
}

export async function getSpokenLanguage(signal?: AbortSignal): Promise<SpokenLanguageSnapshot> {
  const path = "/gateway/spoken-language";
  const res = await fetch(path, {
    method: "GET",
    headers: { Accept: "application/json", ...authHeaders() },
    signal,
  });
  return readJson(res, `GET ${path}`);
}

/** Choose the language this account is spoken to in. Returns the whole document, including the voice the
 *  account is now spoken with - which is why switching languages needs no second call and no guessing. */
export async function setSpokenLanguage(language: string): Promise<SpokenLanguageSnapshot> {
  const path = "/gateway/spoken-language";
  const res = await fetch(path, {
    method: "PUT",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ language }),
  });
  return readJson(res, `PUT ${path}`);
}

/** Choose the voice for ONE language. The language is sent explicitly rather than implied by what is
 *  selected, so a voice can never be recorded against the wrong language mid-switch. */
export async function setSpokenVoice(language: string, voice: string): Promise<SpokenLanguageSnapshot> {
  const path = "/gateway/spoken-language/voice";
  const res = await fetch(path, {
    method: "PUT",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ language, voice }),
  });
  return readJson(res, `PUT ${path}`);
}
