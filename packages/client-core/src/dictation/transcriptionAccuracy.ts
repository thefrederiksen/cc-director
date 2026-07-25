// Scoring for the "Test transcription" check: how much of a KNOWN passage the transcriber actually
// got right.
//
// The microphone check (micQuality.ts) measures the audio going in. This measures the text coming
// out, against a passage we chose, so the answer is a number rather than an impression. That matters
// for the same reason as the microphone check: "dictation is rubbish" is not actionable, but "we got
// 82% of the words, and here are the ones we missed" is.
//
// The measure is the standard one for speech recognition - word error rate, computed by aligning the
// transcript against the expected text and counting substitutions, deletions and insertions. It is
// reported as accuracy (1 - error rate) because that is the direction people read, and the alignment
// is kept so the screen can SHOW which words were missed rather than only scoring them.
//
// NOTE ON NON-ASCII TEXT: this file and languages.ts necessarily carry non-ASCII characters, because
// the passages are in Danish, German, French and Spanish. That is the feature. Every console line,
// identifier and comment stays ASCII.

/** How a language's text is split for comparison. */
export type TokenMode =
  /** Words separated by whitespace - every language written with spaces. */
  | "words"
  /** Individual characters - Chinese and Japanese do not put spaces between words, so a
   *  whitespace split would score a whole sentence as one enormous token. */
  | "characters";

/** One aligned step between the expected passage and what came back. */
export interface DiffStep {
  op: "equal" | "substitute" | "delete" | "insert";
  /** The expected token. Empty for an insertion (the transcriber invented a word). */
  expected: string;
  /** The transcribed token. Empty for a deletion (the transcriber dropped a word). */
  actual: string;
}

export interface AccuracyResult {
  /** 0..1, the share of the passage transcribed correctly. */
  accuracy: number;
  /** 0..1, the standard word error rate. Can exceed 1 when the transcriber invents more than it hears. */
  errorRate: number;
  correct: number;
  substitutions: number;
  deletions: number;
  insertions: number;
  /** Tokens in the expected passage. */
  expectedCount: number;
  /** The full alignment, for rendering the passage with its mistakes marked. */
  diff: DiffStep[];
}

export type AccuracyRating = "excellent" | "good" | "poor";

export interface AccuracyVerdict {
  rating: AccuracyRating;
  headline: string;
  /** Plain-English reading of the number, including what to try next. */
  detail: string;
  result: AccuracyResult;
}

/**
 * Reduce text to comparable tokens: case-folded, stripped of punctuation and of the marks that only
 * exist in writing. Without this a transcriber would be marked wrong for writing "Yesterday," with a
 * comma, or for capitalising a sentence - neither of which is a transcription error.
 */
export function tokenize(text: string, mode: TokenMode): string[] {
  const cleaned = text
    .toLowerCase()
    // Unicode punctuation and symbols, which covers the Latin comma, the Arabic comma, the Devanagari
    // danda and the Chinese full stop in one rule rather than a list that would always be incomplete.
    .replace(/[\p{P}\p{S}]/gu, " ")
    .trim();

  if (mode === "characters") {
    // Drop whitespace entirely: whether a transcriber puts spaces between Chinese words is a
    // formatting choice, not a transcription error.
    return Array.from(cleaned.replace(/\s+/gu, ""));
  }
  if (cleaned === "") return [];
  return cleaned.split(/\s+/u);
}

/**
 * Align two token sequences with Levenshtein and return the edit path. Standard dynamic programming:
 * the table holds the cost of turning the first i expected tokens into the first j actual ones, and
 * the backtrace turns that into the list of operations the screen renders.
 */
export function alignTokens(expected: string[], actual: string[]): DiffStep[] {
  const n = expected.length;
  const m = actual.length;

  // cost[i][j] = edits needed to turn expected[0..i) into actual[0..j).
  const cost: number[][] = Array.from({ length: n + 1 }, () => new Array<number>(m + 1).fill(0));
  for (let i = 0; i <= n; i++) cost[i][0] = i;
  for (let j = 0; j <= m; j++) cost[0][j] = j;

  for (let i = 1; i <= n; i++) {
    for (let j = 1; j <= m; j++) {
      const same = expected[i - 1] === actual[j - 1];
      cost[i][j] = Math.min(
        cost[i - 1][j - 1] + (same ? 0 : 1), // match or substitute
        cost[i - 1][j] + 1, // delete (the transcriber missed this word)
        cost[i][j - 1] + 1, // insert (the transcriber added a word)
      );
    }
  }

  const steps: DiffStep[] = [];
  let i = n;
  let j = m;
  while (i > 0 || j > 0) {
    if (i > 0 && j > 0) {
      const same = expected[i - 1] === actual[j - 1];
      if (cost[i][j] === cost[i - 1][j - 1] + (same ? 0 : 1)) {
        steps.push({
          op: same ? "equal" : "substitute",
          expected: expected[i - 1],
          actual: actual[j - 1],
        });
        i--;
        j--;
        continue;
      }
    }
    if (i > 0 && cost[i][j] === cost[i - 1][j] + 1) {
      steps.push({ op: "delete", expected: expected[i - 1], actual: "" });
      i--;
      continue;
    }
    steps.push({ op: "insert", expected: "", actual: actual[j - 1] });
    j--;
  }

  steps.reverse();
  return steps;
}

/** Score a transcript against the passage the user was asked to read. */
export function scoreTranscription(expectedText: string, actualText: string, mode: TokenMode): AccuracyResult {
  const expected = tokenize(expectedText, mode);
  const actual = tokenize(actualText, mode);
  const diff = alignTokens(expected, actual);

  let correct = 0;
  let substitutions = 0;
  let deletions = 0;
  let insertions = 0;
  for (const step of diff) {
    if (step.op === "equal") correct++;
    else if (step.op === "substitute") substitutions++;
    else if (step.op === "delete") deletions++;
    else insertions++;
  }

  const expectedCount = expected.length;
  // The standard definition divides every error by the length of the REFERENCE, so inventing words is
  // penalised as heavily as dropping them.
  const errorRate = expectedCount === 0 ? 0 : (substitutions + deletions + insertions) / expectedCount;
  const accuracy = expectedCount === 0 ? 0 : Math.max(0, 1 - errorRate);

  return { accuracy, errorRate, correct, substitutions, deletions, insertions, expectedCount, diff };
}

// Where the reading changes meaning. Speech recognition research treats a word error rate under about
// 10% as a transcript you can use as-is, and above about 25% as one you spend longer fixing than you
// would have spent typing.
const EXCELLENT_ACCURACY = 0.9;
const GOOD_ACCURACY = 0.75;

/** Fold the score into the verdict both clients render, worded for someone who is not an engineer. */
export function judgeAccuracy(result: AccuracyResult, languageName: string): AccuracyVerdict {
  const percent = Math.round(result.accuracy * 100);

  if (result.expectedCount === 0) {
    return {
      rating: "poor",
      headline: "There was nothing to compare against.",
      detail: "The passage was empty, so no score could be worked out.",
      result,
    };
  }

  if (result.correct === 0) {
    return {
      rating: "poor",
      headline: "None of the passage came back correctly.",
      detail:
        `Nothing matched the ${languageName} passage. Check that the transcription language matches ` +
        "what you actually spoke, and run the microphone test - this is what a badly band-limited " +
        "headset looks like from the text side.",
      result,
    };
  }

  if (result.accuracy >= EXCELLENT_ACCURACY) {
    return {
      rating: "excellent",
      headline: `${percent}% of the passage came back correctly.`,
      detail: `Transcription is working well in ${languageName}. You can dictate and trust what you get.`,
      result,
    };
  }

  if (result.accuracy >= GOOD_ACCURACY) {
    return {
      rating: "good",
      headline: `${percent}% of the passage came back correctly.`,
      detail:
        `Transcription mostly works in ${languageName}, but you will be correcting the odd word. If ` +
        "the microphone test also flagged a problem, fix that first - it is the usual cause.",
      result,
    };
  }

  return {
    rating: "poor",
    headline: `Only ${percent}% of the passage came back correctly.`,
    detail:
      `Transcription is struggling with ${languageName}. Run the microphone test first: a Bluetooth ` +
      "hands-free headset or a noisy room will produce exactly this. If the microphone is good, the " +
      "words below show what is being misheard, and those are worth adding to your dictionary.",
    result,
  };
}
