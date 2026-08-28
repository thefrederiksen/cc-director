// Scoring a transcript against the words that were actually said.
//
// The benchmark plays clips whose text we wrote, so for once there is a right answer to compare
// against. Word error rate is the standard measure: the number of words you would have to insert,
// delete or change to turn the transcript into the truth, divided by how many words the truth has.
//
// Zero is perfect. 0.1 means one word in ten is wrong. Above about 0.3 the transcript is not usable
// for anything, and it is worth knowing that a model reached that state quickly rather than
// discovering it in a kitchen three weeks later.

import { normaliseForMatching } from "../wakeWord/wakeWordMatcher";

// Speech models write numbers as digits; people write them as words. "ten minutes" and
// "10 minutes" are the same sentence heard correctly, and counting that as an error made the
// first benchmark run report a perfectly transcribed clip as 14 percent wrong. Both sides are put
// into digits before they are compared.
const NUMBER_WORDS = new Map<string, string>([
  ["zero", "0"], ["one", "1"], ["two", "2"], ["three", "3"], ["four", "4"],
  ["five", "5"], ["six", "6"], ["seven", "7"], ["eight", "8"], ["nine", "9"],
  ["ten", "10"], ["eleven", "11"], ["twelve", "12"], ["thirteen", "13"], ["fourteen", "14"],
  ["fifteen", "15"], ["sixteen", "16"], ["seventeen", "17"], ["eighteen", "18"], ["nineteen", "19"],
  ["twenty", "20"], ["thirty", "30"], ["forty", "40"], ["fifty", "50"], ["sixty", "60"],
  ["seventy", "70"], ["eighty", "80"], ["ninety", "90"], ["hundred", "100"], ["thousand", "1000"],
]);


export interface WordErrorRate {
  /** Errors divided by the number of words in the truth. Can exceed 1 when a model invents words. */
  readonly rate: number;
  readonly substitutions: number;
  readonly deletions: number;
  readonly insertions: number;
  readonly referenceWords: number;
}

/**
 * Compare a transcript against what was actually said.
 *
 * Both sides are normalised the same way the wake word matcher normalises, so capitalisation and
 * punctuation are not counted as mistakes. A model that writes "ten minutes." instead of
 * "ten minutes" has not misheard anything.
 */
export function wordErrorRate(reference: string, heard: string): WordErrorRate {
  const truth = toWords(reference);
  const guess = toWords(heard);

  if (truth.length === 0) {
    return {
      rate: guess.length === 0 ? 0 : 1,
      substitutions: 0,
      deletions: 0,
      insertions: guess.length,
      referenceWords: 0,
    };
  }

  // Standard edit distance, carrying which kind of error each step was so the three counts can be
  // reported separately. A model that drops half the sentence and one that invents half a sentence
  // both score badly, and they are not the same problem.
  interface Cell {
    cost: number;
    substitutions: number;
    deletions: number;
    insertions: number;
  }

  const start = (): Cell => ({ cost: 0, substitutions: 0, deletions: 0, insertions: 0 });
  let previous: Cell[] = new Array(guess.length + 1);
  previous[0] = start();
  for (let j = 1; j <= guess.length; j += 1) {
    previous[j] = { cost: j, substitutions: 0, deletions: 0, insertions: j };
  }

  for (let i = 1; i <= truth.length; i += 1) {
    const current: Cell[] = new Array(guess.length + 1);
    current[0] = { cost: i, substitutions: 0, deletions: i, insertions: 0 };

    for (let j = 1; j <= guess.length; j += 1) {
      if (truth[i - 1] === guess[j - 1]) {
        current[j] = { ...previous[j - 1] };
        continue;
      }
      const substitute = previous[j - 1];
      const deleteWord = previous[j];
      const insertWord = current[j - 1];
      const best = Math.min(substitute.cost, deleteWord.cost, insertWord.cost);

      if (best === substitute.cost) {
        current[j] = { ...substitute, cost: best + 1, substitutions: substitute.substitutions + 1 };
      } else if (best === deleteWord.cost) {
        current[j] = { ...deleteWord, cost: best + 1, deletions: deleteWord.deletions + 1 };
      } else {
        current[j] = { ...insertWord, cost: best + 1, insertions: insertWord.insertions + 1 };
      }
    }
    previous = current;
  }

  const final = previous[guess.length];
  return {
    rate: Number((final.cost / truth.length).toFixed(4)),
    substitutions: final.substitutions,
    deletions: final.deletions,
    insertions: final.insertions,
    referenceWords: truth.length,
  };
}

function toWords(text: string): string[] {
  const normalised = normaliseForMatching(text);
  if (normalised.length === 0) {
    return [];
  }
  return normalised.split(" ").map((word) => NUMBER_WORDS.get(word) ?? word);
}
