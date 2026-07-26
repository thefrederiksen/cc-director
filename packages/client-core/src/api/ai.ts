// AI settings client for the mobile Settings screen. Same-origin against the Gateway front door with
// the injected Bearer (authHeaders), exactly like the rest of client.ts. These are the SAME endpoints
// the desktop Settings "AI" tab uses - the mobile screen is just a phone-styled front end over them.
import { authHeaders, GatewayError, creditsErrorFrom } from "./client";

export type AiProviderId = "devthrottle";

/** The current AI-provider snapshot (GET/PUT /gateway/ai-provider). */
export interface AiProviderSnapshot {
  provider: AiProviderId;
  wingmanModel: string;
  wingmanFastModel: string;
  /** The model Car Mode's fleet brain runs on - its OWN setting, separate from the Wingman (Car Mode
   *  runs a fast tier + tool_choice=required). The user's saved choice, or the Qwen2.5-72B default. */
  carModeModel: string;
  /** Car Mode's hands-free sign-off phrase (default "over and out"). A Gateway setting so the Cockpit can
   *  set it and the phone, where Car Mode runs, picks it up. */
  carModeEndPhrase: string;
  transcriptionModel: string;
  ttsModel: string;
  ttsVoice: string;
  /** Fallback voice set when the selected speech model does not advertise voices. */
  voices: string[];
  /**
   * Whether the live model CATALOG (GET /gateway/ai/models) and the Test button are available (issue #2022).
   * Gateway-owned, never guessed from the surface: false on the hosted Gateway, where the catalog and
   * test-chat routes stay denied because they spend the shared deployment credential with no per-caller
   * scoping. The AI and Car Mode tabs read this to disable browsing/Test and show a concise explanation
   * instead of offering a control that would fail. True on self-host.
   */
  catalogAvailable: boolean;
}

/** One model in the provider's catalog (GET /gateway/ai/models). */
export interface AiModel {
  id: string;
  description: string;
  /** For speech models: the model's own voice list (empty for chat models). */
  voices: string[];
  defaultVoice: string | null;
  /**
   * For speech models: the languages this model can actually SPEAK, as BCP-47 primary subtags.
   * The spoken-language picker filters on this. A model that publishes none is treated as
   * English-only rather than as speaking everything - offering a language a model cannot
   * pronounce produces confident gibberish, which is worse than not offering it.
   */
  languages: string[];
}

/** One language DevThrottle can speak back in (GET /gateway/ai/spoken-languages). */
export interface SpokenLanguageOption {
  code: string;
  /** The English name, e.g. "Danish" - what the wingman prompt uses. */
  name: string;
  /** The language's own name, e.g. "dansk" - people recognise this faster than the English one. */
  endonym: string;
}

/** The result of testing a chat model (POST /gateway/ai/test-chat). */
export interface ChatTestResult {
  ok: boolean;
  reply: string;
  seconds: number;
  error: string;
}

async function getJson<T>(path: string): Promise<T> {
  const res = await fetch(path, { method: "GET", headers: { Accept: "application/json", ...authHeaders() } });
  if (!res.ok) throw new GatewayError(res.status, `GET ${path} failed: ${res.status}`);
  return (await res.json()) as T;
}

async function putJson<T>(path: string, body: unknown): Promise<T> {
  const res = await fetch(path, {
    method: "PUT",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify(body),
  });
  if (!res.ok) {
    const err = (await res.json().catch(() => ({}))) as { error?: string };
    throw new GatewayError(res.status, err.error ?? `PUT ${path} failed: ${res.status}`);
  }
  return (await res.json()) as T;
}

export function getAiProvider(): Promise<AiProviderSnapshot> {
  return getJson<AiProviderSnapshot>("/gateway/ai-provider");
}

export function setAiProvider(provider: AiProviderId): Promise<AiProviderSnapshot> {
  return putJson<AiProviderSnapshot>("/gateway/ai-provider", { provider });
}

// GET /gateway/ai/models?kind=chat|speech - the selected provider's live catalog. Degrades to an empty
// list on any error (not signed in / provider down) so the screen still renders the saved value.
export async function getAiModels(kind: "chat" | "speech"): Promise<AiModel[]> {
  try {
    const d = await getJson<{ models?: AiModel[] }>(`/gateway/ai/models?kind=${kind}`);
    return Array.isArray(d.models) ? d.models : [];
  } catch {
    return [];
  }
}

export async function testChat(model: string): Promise<ChatTestResult> {
  const res = await fetch("/gateway/ai/test-chat", {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json", ...authHeaders() },
    body: JSON.stringify({ model }),
  });
  const d = (await res.json().catch(() => ({}))) as Partial<ChatTestResult>;
  return { ok: Boolean(d.ok), reply: d.reply ?? "", seconds: Number(d.seconds ?? 0), error: d.error ?? (res.ok ? "" : `HTTP ${res.status}`) };
}

export function setWingmanModel(model: string): Promise<{ model: string }> {
  return putJson<{ model: string }>("/gateway/ai/wingman-model", { model });
}

export function setWingmanFastModel(model: string): Promise<{ model: string }> {
  return putJson<{ model: string }>("/gateway/ai/wingman-fast-model", { model });
}

// PUT /gateway/ai/car-mode-model { model } - persist the model Car Mode's fleet brain runs on (its own
// setting, separate from the Wingman). The Gateway resolves the effective model at turn time as env
// override, then this saved setting, then the Qwen2.5-72B default.
export function setCarModeModel(model: string): Promise<{ model: string }> {
  return putJson<{ model: string }>("/gateway/ai/car-mode-model", { model });
}

// PUT /gateway/ai/car-mode-end-phrase { phrase } - persist Car Mode's hands-free sign-off phrase. A blank
// phrase resets to the "over and out" default (an empty phrase would end every turn). Returns the effective
// phrase the Gateway stored.
export function setCarModeEndPhrase(phrase: string): Promise<{ phrase: string }> {
  return putJson<{ phrase: string }>("/gateway/ai/car-mode-end-phrase", { phrase });
}

export function setTtsModel(model: string): Promise<{ model: string }> {
  return putJson<{ model: string }>("/gateway/ai/tts-model", { model });
}

export function setTtsVoice(voice: string): Promise<{ voice: string }> {
  return putJson<{ voice: string }>("/gateway/tts-voice", { voice });
}

// GET /gateway/ai/spoken-languages -> the languages on offer plus the one this account is on.
// Served by the Gateway rather than hardcoded per app so mobile and the Cockpit cannot drift, and
// so adding a language does not need two app releases.
export async function getSpokenLanguages(): Promise<{ current: string; languages: SpokenLanguageOption[] }> {
  const res = await fetch("/gateway/ai/spoken-languages", { headers: authHeaders() });
  if (!res.ok) throw new Error(`spoken languages: ${res.status}`);
  return res.json();
}

// PUT /gateway/ai/spoken-language { language } - the language DevThrottle SPEAKS BACK in. This does
// NOT affect dictation, which detects the spoken language on its own. A blank value means English.
// The Gateway moves the speech model with the language and reports what it ended up as - the client
// deliberately does not decide this. On the hosted Gateway the model catalog is refused (it spends the
// shared deployment credential), so a browser has no model list to reason about and would leave the
// account on an engine that cannot say the chosen language. Deciding server-side also keeps mobile and
// the Cockpit from drifting, because neither of them decides anything.
export function setSpokenLanguage(language: string): Promise<{
  language: string;
  ttsModel: string | null;
  ttsVoice: string | null;
  switched: boolean;
}> {
  return putJson("/gateway/ai/spoken-language", { language });
}

// POST /wingman/tts { text, model, voice } -> audio bytes to play (a short "Play sample").
//
// model and voice are OPTIONAL and should normally be omitted: the Gateway then resolves the
// account's own speech model and voice, which are authoritative. Passing what the page currently
// shows makes the sample play whatever the CLIENT believes, and a stale page (a cached PWA bundle,
// say) then auditions the wrong engine entirely - which reads as "the language setting did nothing".
export async function ttsSample(text: string, model?: string, voice?: string): Promise<Blob> {
  const res = await fetch("/wingman/tts", {
    method: "POST",
    headers: { "Content-Type": "application/json", ...authHeaders() },
    body: JSON.stringify({ text, model: model ?? "", voice: voice ?? "" }),
  });
  if (!res.ok) {
    const err = (await res.json().catch(() => ({}))) as { error?: string };
    // "Play sample" ran into out-of-credits (issue #942): the shared notice.
    if (res.status === 402) throw creditsErrorFrom(err);
    throw new GatewayError(res.status, err.error ?? `text-to-speech failed: ${res.status}`);
  }
  return res.blob();
}
