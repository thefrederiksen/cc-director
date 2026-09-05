# Phase two, task 1 - the shape of the 594

**What this is.** The state note required the 594 terminal-typed turns to be MEASURED before defect
one is fixed, with an instruction to stop and raise if they turn out to be bare confirmations rather
than composed prompts. They are not. **The fix proceeds.**

**Written:** 2026-09-05. Changes no code. Script: `evidence/shape594.py`, re-runnable.

---

## The verdict in one line

**They are composed prompts.** The median terminal-typed turn is 29 characters and 6 words;
92.7 per cent carry two or more words; 3.3 per cent are under five characters; and even those are
imperative instructions, not acknowledgements. Counting them as turns is the right fix.

---

## The population, and how it was identified

`activity_events` for 2026-W35, America/Toronto, `soren@centerconsulting.com`: **594**
`turn-submitted` events with `SendSource` null and a non-null `InputOrigin` - 591 `typed/desktop`
and 3 `typed/phone`. That is the `Session.SendInput` path, and it is the same 594 phase one named.

To get their TEXT, the mentor harness's own classifier was re-run over the week with
`origin.Ledger.nearest_unclaimed` instrumented to record which ledger event each prompt-log record
claimed. Of the week's 1,581 human prompts:

| The claimed event | Human prompts | What it is |
|---|---:|---|
| `SendSource` null, origin present | **493** | terminal typing - `Session.SendInput` |
| `SendSource = UserInput`, origin present | 908 | the message composer and desktop dictation |
| something else, or no claim | 180 | |

Of the 493, **491 are typed** (489 `typed/desktop`, 2 `typed/phone`) and are the population measured
below. The other 2 are voice-stamped records that claimed a typed event under the classifier's
pass 3, which takes the nearest unclaimed event of any kind; they are excluded rather than smoothed
over.

**This also sharpens a figure the state note carried loosely.** The report's 583 `typed/desktop`
human prompts are not all terminal typing: **489 are terminal typing and 93 came through the
composer.** The remaining 594 - 493 = 101 `SendInput` events have no prompt-log record within
tolerance, which is the same coverage loss phase one already disclosed (the report attributes 1,581
of 1,786 origin-carrying submissions).

---

## The shape

Over the 491 typed terminal turns that carry text:

| | characters | words |
|---|---:|---:|
| minimum | 2 | 1 |
| tenth percentile | 8 | 2 |
| twenty-fifth percentile | 17 | |
| **median** | **29** | **6** |
| seventy-fifth percentile | 44 | |
| ninetieth percentile | 54 | 10 |
| ninety-ninth percentile | 189 | |
| maximum | 755 | 134 |
| mean | 35.5 | |
| total | 17,449 | 3,364 |

Distribution by characters:

| Band | Count | Share |
|---|---:|---:|
| 0 to 4 | 16 | 3.3% |
| 5 to 9 | 49 | 10.0% |
| 10 to 19 | 77 | 15.7% |
| 20 to 49 | 271 | 55.2% |
| 50 to 99 | 69 | 14.1% |
| 100 to 199 | 4 | 0.8% |
| 200 to 499 | 4 | 0.8% |
| 500 to 999 | 1 | 0.2% |

- **Under five characters: 16, or 3.3 per cent.**
- Twenty characters or more: 349, or 71.1 per cent.
- Two words or more: 455, or 92.7 per cent.
- Spread over 77 distinct sessions.

## The short ones are instructions, not confirmations

Ninety-six records are at most twelve characters. Read as exact tokens they are overwhelmingly
imperative commands carrying a decision - "commit this", "fix all ten", "review 2938", "deploy dev",
"run round 3", "now b1" - together with 36 continuations ("go" 13, "continue" 12, "keep going" 11).
**Pure acknowledgements total three of 491**: "yes" twice and "done" once.

A continuation is still the developer submitting a turn: the agent stops, he decides to proceed, and
his submission is what starts the next turn. It is the same act the composer records as a turn today.
Nothing in this population resembles a keystroke that is not a submission.

---

## What this does NOT establish

- **The 101 unmatched events.** 594 events, 493 matched to a prompt-log record. The 101 are assumed
  to be the same kind of thing on the strength of the 493; they were not read, because there is no
  text for them.
- **The claim map is keyed on (session, timestamp)** and 56 of 16,716 lookups collided on that key,
  last write wins. At most 56 of the 1,581 human prompts could be attributed to the wrong event. It
  cannot move a 3.3 per cent result, and it is named rather than hidden.
- **Any week other than 2026-W35.**
