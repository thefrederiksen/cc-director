import { type SpokenUtterance } from "./spokenUtterance";

// THE ONE PLACE THE BROWSER SPEAKS FOR ITSELF (issue #1031).
//
// Every other spoken word in the product is synthesized by the Gateway in the account's voice. This sink exists
// for the speech that must NOT go through the Gateway: the product telling you it will not act. When a menu owns
// a session's screen and a spoken reply is refused, saying so has to cost nothing, need no network, and never be
// the thing that fails - so it uses the device's own voice.
//
// IT IS A DUMB SINK. It takes an utterance and plays it. It does not read a setting, resolve a voice, or pick a
// language, and it cannot: its parameter is a SpokenUtterance, so a bare string does not type-check. That is the
// property, and it is the one that was missing - this speech used to be handed a plain string and therefore set
// no language, so correct French came out in the device's default English voice.

/** What the sink needs from the platform. Narrowed so this file states exactly how much of the Web Speech API is
 *  in use - cancel, speak, and the voice list - and so a test can stub it. */
interface LocalSpeechEngine {
  cancel: () => void;
  speak: (utterance: SpeechSynthesisUtterance) => void;
  getVoices?: () => SpeechSynthesisVoice[];
}

/** The bit of a voice this matching needs. Narrowed deliberately: a real SpeechSynthesisVoice cannot be
 *  constructed in a test, and nothing here needs more than the language tag. */
export interface VoiceLike {
  lang: string;
}

/**
 * The first voice that speaks `language`, or null when the list has none.
 *
 * NULL IS A NORMAL ANSWER, not a failure. `getVoices()` is empty until the browser has loaded its list, and some
 * engines never expose one. The caller still sets `lang` on the utterance, which is what the platform selects by
 * when no voice is named - so a null means "let the platform choose", never "give up on speaking". A voice that
 * could not be found must never be the reason a notice goes unsaid.
 *
 * MATCHED ON THE PRIMARY SUBTAG, deliberately. The Gateway decides a LANGUAGE - "fr" - and a device carries
 * region-tagged voices like "fr-FR" or "fr_CA". Comparing whole tags would find nothing on most devices;
 * comparing the primary subtag finds a French voice, which is the question being asked. Underscores are
 * normalized because some Android engines report "es_ES".
 */
export function pickVoiceFor<T extends VoiceLike>(voices: readonly T[], language: string): T | null {
  const wanted = primarySubtag(language);
  if (wanted.length === 0) return null;
  for (const voice of voices) {
    if (primarySubtag(voice.lang) === wanted) return voice;
  }
  return null;
}

/** The primary language subtag, lower-cased: "fr-FR" -> "fr", "es_ES" -> "es", "en" -> "en". */
function primarySubtag(tag: string): string {
  return (tag ?? "").trim().toLowerCase().replace(/_/g, "-").split("-")[0] ?? "";
}

/**
 * Say it, with the device's own voice, in the utterance's language.
 *
 * IT TAKES NO ENGINE, and that is deliberate: this function reaching for `speechSynthesis` itself is what makes
 * it the ONLY place in the browser that touches the platform's speech engine. A caller that had to fetch the
 * engine would be a caller that could speak with it directly - which is exactly what the code used to do, with
 * no language, and is what a guard test now fails the build over.
 *
 * BOTH HALVES ARE SET ON PURPOSE. `lang` is what the platform selects a voice by when none is named; an
 * explicitly named voice is what makes it certain on engines that ignore `lang`. Neither is a fallback for the
 * other being broken - the voice list is simply empty until the browser has loaded it, and on that first call
 * `lang` is the whole answer.
 *
 * Failures are swallowed BY DESIGN, and this is the one place in the mission where that is right: this speech
 * rides on top of an on-screen notice that has already been shown, never instead of it, so a speech engine that
 * throws must not take down the send it was reporting on. It returns whether it spoke, so a caller that wants to
 * know can ask.
 */
export function speakLocally(utterance: SpokenUtterance): boolean {
  const engine = platformEngine();
  if (!engine) return false;
  try {
    engine.cancel();
    const spoken = new SpeechSynthesisUtterance(utterance.text);
    spoken.lang = utterance.language;
    const match = pickVoiceFor(availableVoices(engine), utterance.language);
    if (match) spoken.voice = match;
    engine.speak(spoken);
    return true;
  } catch {
    return false;
  }
}

/** The platform's speech engine, or undefined in a browser that has none - a normal state, not an error. */
function platformEngine(): LocalSpeechEngine | undefined {
  return (globalThis as unknown as { speechSynthesis?: LocalSpeechEngine }).speechSynthesis;
}

/**
 * The device's installed voices, or an empty list when this engine has none to offer.
 *
 * Guarded on the METHOD, not by the catch above. That distinction is why this is a function: an engine with no
 * getVoices - or one that throws from it - must cost us the VOICE, never the UTTERANCE. Relying on the outer
 * catch would have dropped the whole notice instead, which is the failure the notice exists to prevent.
 */
function availableVoices(engine: LocalSpeechEngine): readonly SpeechSynthesisVoice[] {
  if (typeof engine.getVoices !== "function") return [];
  try {
    return engine.getVoices() ?? [];
  } catch {
    return [];
  }
}
