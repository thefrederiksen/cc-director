# Dictionary suggestions: model-screened daily scan - validation record (issue #2115)

Date: 2026-07-24. Corpus: the owner's real dictation logs, 4,107 utterances
(`dictation/sessions/*.jsonl`, RawTranscript field). Method: the SHIPPED miner
(`MistranscriptionMiner`, unchanged policy) and the SHIPPED screening pass
(`DictionarySuggestionScreen`, the exact prompt in the code) against the REAL hosted inference
endpoint, run exactly as `DictionarySuggestionService.RunScanAsync` runs them (the mine-screen-
exclude-re-mine loop). Harness: a throwaway console program referencing the repository projects;
nothing was mocked on the model side.

## Before: what the heuristic alone suggests (the shipped #2075 behavior)

50 candidates, effectively all garbage. The top of the list, verbatim:

```
this      wrong 14462 of 18499  heard as: that, then, think, them, there, they, these, tell
should    wrong  8780 of 10566  heard as: session, start, some, sure, Soren, show, something, screen
create    wrong  6576 of  7309  heard as: changes, code, could, can't, cockpit, change, commit, clean
want      wrong  6268 of  8192  heard as: what, with, we're, where, when, What, what's, we've
from      wrong  2943 of  3404  heard as: first, figure, find, fuck, files, feature, full, fixed
...
```

These are DISTINCT ordinary words chained together by phonetic nearness - not mistranscriptions.
This is the screen the owner rejected outright (25 suggestions, zero acceptable).

The real terms never even reached the list: "Frederiksen" sat at rank 35 and "mindzie"-class
terms below the 50-candidate cap, crowded out by garbage. That finding produced the scan's
mine-screen LOOP (screen a round, exclude the rejected, re-mine).

## Findings the validation forced back into the design

1. **Crowding-out**: the miner's 50-candidate cap was filled entirely by garbage, so screening a
   single batch could never surface the real terms. Fix: the scan loops (bounded by
   `MaxScreeningRoundsPerScan`), excluding rejected terms from the next mine.
2. **Single-batch timeout**: one 50-candidate prompt blew the inference call's 60-second
   deadline. Fix: `DictionarySuggestionScreen.ChunkSize` = 20 candidates per call, and the
   screening brain gets a 3-minute per-call deadline.
3. **Fast-model leniency**: the fast model approved inflection clusters ("issue heard as
   issues", "kill heard as killed") and mixed clusters ("open" approved because its variants
   contained OpenAI/OpenCode - a mapping that would CORRUPT text). Fix: screening uses the
   THINKING model (nightly background work; judgment quality is the product), and the prompt
   names both failure modes explicitly.

## After: the shipped design, converged

Four screening rounds, 169 candidates judged, every verdict persisted. Result:

```
APPROVE  Frederiksen  wrong 116 of 364  heard as: Fredriksson, Fredriksen, Fredrickson, Fredrikson, Fredericksen, fredriksen
APPROVE  DuxRevo      wrong   3 of   6  heard as: duxurivo, ducksrevo
```

Two suggestions, both genuinely distinctive proper names with genuine near-phonetic
misspellings - exactly what the dictionary exists to hold. Every ordinary-word cluster from the
"before" list was rejected with a reason, and each of those verdicts is stored so it is never
paid for again: the steady-state daily scan makes zero model calls until new vocabulary appears
in the dictations.
