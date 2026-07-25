import type { TokenMode } from "./transcriptionAccuracy";

// The reading passages for the "Test transcription" check, one per language.
//
// WHICH LANGUAGES: exactly the languages DevThrottle officially supports - English, Danish, German,
// French and Spanish. This list is deliberately the SUPPORTED set and nothing wider. Offering a test
// in a language the product does not support invites someone to measure something we never promised
// and then read a poor score as a defect rather than as an unsupported language. A check that reports
// on what we do not ship is worse than no check, because the number looks official.
//
// WHY EACH PASSAGE IS THE SAME STORY: every language says roughly the same thing, so a score in one
// language can be compared against a score in another. If the Danish passage were a tongue-twister
// and the Spanish one a nursery rhyme, the two numbers would not mean the same thing, and comparing
// languages - the entire reason for testing more than one - would be meaningless.
//
// WHAT MAKES A GOOD PASSAGE: 30-40 words, so it takes 15-25 seconds to read - long enough for a
// stable score, short enough that people finish it. Ordinary prose rather than a pangram, because
// people read familiar sentence shapes at a natural pace and it is natural speech we want to measure.
// No proper nouns, numbers written as words, no jargon: those are dictionary problems, not
// transcription problems, and they would blame the transcriber for the wrong thing.
//
// ADDING A LANGUAGE is one entry here and nothing else, PROVIDED it is written left to right with
// spaces between words - which every currently supported language is. Two things need attention
// otherwise, and both are easy to miss:
//   - A language not delimited by spaces (Chinese, Japanese, Thai) must set tokenMode "characters",
//     or a whole sentence scores as a single token and every speaker of it reads as near-zero. That
//     scoring path is built and directly tested (transcriptionAccuracy.ts); it simply has no user in
//     the supported set today, and the repository's own evaluation methodology requires it for
//     zh/ja/th, so it stays.
//   - A right-to-left language (Arabic, Hebrew) needs dir="rtl" on the passage and on the diff. That
//     is NOT built. It was removed along with the unsupported languages rather than left behind as
//     configuration nothing exercises - an untested rendering path rots quietly.
//
// NON-ASCII TEXT IS DELIBERATE HERE: Danish, German, French and Spanish cannot be written in ASCII.
// Console output, identifiers, log lines and comments stay ASCII.

export interface TestLanguage {
  /** BCP 47 tag, sent to the transcriber as the language hint and stored with the clip. */
  code: string;
  /** Name in English, for our own screens and reports. */
  name: string;
  /** Name as a speaker of the language would write it, for the picker. */
  nativeName: string;
  /** The passage to read aloud. */
  passage: string;
  /** How this language is split for scoring. */
  tokenMode: TokenMode;
}

export const TEST_LANGUAGES: readonly TestLanguage[] = [
  {
    code: "en",
    name: "English",
    nativeName: "English",
    tokenMode: "words",
    passage:
      "Yesterday I finished six small tasks before lunch, then walked through the park to clear my " +
      "head. The weather was cold but bright, and the streets were surprisingly quiet for a " +
      "Thursday afternoon.",
  },
  {
    code: "da",
    name: "Danish",
    nativeName: "Dansk",
    tokenMode: "words",
    passage:
      "I går afsluttede jeg seks små opgaver før frokost og gik derefter en tur i parken for at få " +
      "luft. Vejret var koldt, men klart, og gaderne var overraskende stille en torsdag eftermiddag.",
  },
  {
    code: "de",
    name: "German",
    nativeName: "Deutsch",
    tokenMode: "words",
    passage:
      "Gestern habe ich vor dem Mittagessen sechs kleine Aufgaben erledigt und bin dann durch den " +
      "Park gelaufen, um den Kopf frei zu bekommen. Das Wetter war kalt aber klar, und die Straßen " +
      "waren an einem Donnerstagnachmittag überraschend still.",
  },
  {
    code: "fr",
    name: "French",
    nativeName: "Français",
    tokenMode: "words",
    passage:
      "Hier, j'ai terminé six petites tâches avant le déjeuner, puis j'ai marché dans le parc pour me " +
      "changer les idées. Il faisait froid mais lumineux, et les rues étaient étonnamment calmes pour " +
      "un jeudi après-midi.",
  },
  {
    code: "es",
    name: "Spanish",
    nativeName: "Español",
    tokenMode: "words",
    passage:
      "Ayer terminé seis tareas pequeñas antes del almuerzo y luego caminé por el parque para " +
      "despejarme. Hacía frío pero el cielo estaba despejado, y las calles estaban sorprendentemente " +
      "tranquilas para ser un jueves por la tarde.",
  },
];

/** The language a first-time visitor is offered, matched to the browser when we support it. */
export const DEFAULT_LANGUAGE_CODE = "en";

/** Look a language up by code, falling back to English rather than returning nothing. */
export function languageByCode(code: string): TestLanguage {
  const found = TEST_LANGUAGES.find((l) => l.code === code);
  return found ?? TEST_LANGUAGES[0];
}

/**
 * The best supported language for a browser's stated preferences. Matches on the PRIMARY subtag, so
 * a browser set to Austrian German or Mexican Spanish still lands on German or Spanish rather than
 * falling back to English. A browser set to a language we do not support gets English, which is the
 * honest answer - there is no passage to offer it.
 */
export function preferredLanguage(browserLanguages: readonly string[]): TestLanguage {
  for (const tag of browserLanguages) {
    const primary = tag.toLowerCase().split("-")[0];
    const match = TEST_LANGUAGES.find((l) => l.code === primary);
    if (match) return match;
  }
  return languageByCode(DEFAULT_LANGUAGE_CODE);
}
