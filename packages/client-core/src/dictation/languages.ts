import type { TokenMode } from "./transcriptionAccuracy";

// The reading passages for the "Test transcription" check, one per language.
//
// WHY THESE LANGUAGES: English, then the six most widely spoken languages in the world, plus Danish.
// The six are the most-spoken reading of "top six" - the aim is to find out how DevThrottle behaves
// for people all over the world, not to mirror any one country's developer population. Adding or
// swapping a language is one entry in this list and nothing else: the panel, the scoring and the
// storage are all driven from it.
//
// WHY EACH PASSAGE IS THE SAME STORY: every language says roughly the same thing, so a score in one
// language can be compared against a score in another. If the Danish passage were a tongue-twister
// and the Spanish one were a nursery rhyme, the two numbers would not mean the same thing and the
// whole point of testing many languages would be lost.
//
// WHAT MAKES A GOOD PASSAGE: 30-40 words, so it takes 15-25 seconds to read - long enough for a
// stable score, short enough that people finish it. Ordinary prose rather than a pangram, because
// people read familiar sentence shapes at a natural pace and it is natural speech we want to measure.
// No proper nouns, numbers written as words, no jargon: those are dictionary problems, not
// transcription problems, and they would blame the transcriber for the wrong thing.
//
// NON-ASCII TEXT IS DELIBERATE HERE. These passages cannot be written in ASCII; that is the feature.
// Console output, identifiers and comments elsewhere stay ASCII.

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
  /** True for right-to-left scripts, so the passage is laid out correctly. */
  rightToLeft?: boolean;
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
    code: "zh",
    name: "Chinese (Mandarin)",
    nativeName: "中文",
    tokenMode: "characters",
    passage:
      "昨天午饭前我完成了六项小任务，然后去公园散步透透气。天气很冷，但是天空很晴朗，街道在星期四的下午安静得出奇。",
  },
  {
    code: "hi",
    name: "Hindi",
    nativeName: "हिन्दी",
    tokenMode: "words",
    passage:
      "कल मैंने दोपहर के भोजन से पहले छह छोटे काम पूरे किए, फिर ताज़ी हवा के लिए पार्क में टहलने गया। मौसम ठंडा लेकिन साफ़ था, " +
      "और गुरुवार की दोपहर सड़कें बेहद शांत थीं।",
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
    code: "ar",
    name: "Arabic",
    nativeName: "العربية",
    tokenMode: "words",
    rightToLeft: true,
    passage:
      "أنهيت أمس ست مهام صغيرة قبل الغداء، ثم مشيت في الحديقة لأستنشق بعض الهواء. كان الطقس باردا لكنه " +
      "صافيا، وكانت الشوارع هادئة بشكل مدهش في عصر يوم الخميس.",
  },
  {
    code: "pt",
    name: "Portuguese",
    nativeName: "Português",
    tokenMode: "words",
    passage:
      "Ontem terminei seis pequenas tarefas antes do almoço e depois caminhei pelo parque para clarear " +
      "as ideias. Estava frio mas o céu estava limpo, e as ruas estavam surpreendentemente tranquilas " +
      "para uma quinta-feira à tarde.",
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
 * a browser set to Brazilian Portuguese or Swiss French still lands on Portuguese or French rather
 * than falling back to English.
 */
export function preferredLanguage(browserLanguages: readonly string[]): TestLanguage {
  for (const tag of browserLanguages) {
    const primary = tag.toLowerCase().split("-")[0];
    const match = TEST_LANGUAGES.find((l) => l.code === primary);
    if (match) return match;
  }
  return languageByCode(DEFAULT_LANGUAGE_CODE);
}
