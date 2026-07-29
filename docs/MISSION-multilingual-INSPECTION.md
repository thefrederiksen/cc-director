# Inspection brief - Multilingual mission

For the **Inspector** only. A different agent family from the builder (builder is Claude Code, so
the Inspector is Codex). You do not build. You did not write this. That is the point of you.

Write your review to `docs/MISSION-multilingual-REVIEW.md` in this worktree and reply with **one
single line** - fleet messages truncate at the first newline.

## What to inspect

Everything on `mission/multilingual` that is not on `origin/main`:

```
git fetch origin && git diff origin/main...mission/multilingual
```

## Stance

**Be adversarial. Do not trust the mission's own report.** The Manager's summary is self-testimony:
written by the party that did the work, about its own work, and exactly as persuasive as it is
unreliable. The suite being green is not evidence that the feature works - it is evidence that the
tests that exist pass.

Precedent from this repo: an independent Codex inspection of pull request 1598 found **seven real
defects across five passes, none of them false alarms** - including that the mission's headline
feature did not work at all, and that the fix for that was itself half-done. The author had verified
the code, revert-tested the fixes, and watched the tests fail on purpose. All green. All of it would
have shipped.

## The sharp questions

1. **What does this claim that the code does not support?** Compare the commit messages and
   `docs/MISSION-multilingual.md` against what the diff actually does.
2. **Where could a constant be substituted and the suite stay green?** Any assertion that would pass
   against a hard-coded value is not testing behaviour.
3. **What is unguarded?** Which spoken path could ship in English without any test going red?

## The specific failure this mission exists to prevent

Read `gh issue view 547 --repo thefrederiksen/devthrottle_internal` first.

The previous attempt shipped and was reverted because **the language reached one generator out of
four** - an account set to another language got translated narration and was then answered in
English the moment it was spoken to. Four separate defects were each found and shipped as "working"
before the real blocker surfaced.

So the questions that matter most:

- **Enumerate every spoken path yourself, from the code - do not use `SpokenPaths` as your source of
  truth, since that is the thing under test.** For each one, does a configured language actually
  reach it? Find the fifth path the registry does not know about.
- Does the "no method both reads a language and touches the TTS model" guard actually hold, or can
  it be walked around by splitting the two halves across two methods?
- `BuildMenuSpoken` glues fixed English around a model-extracted question. Is it genuinely
  translated now, or does English survive somewhere in the composition?
- Is `CleanupForSpeech` applied at every exit, Car Mode included?

## Encoding - a silent-failure class, check it specifically

Ruling 1 (`docs/MISSION-multilingual-RULINGS.md`) allows accents in spoken content. Verify:

- Accented strings survive the **real loading path** byte-for-byte. This machine defaults to cp1252;
  a resource file read with the wrong encoding turns `é` into mojibake and **fails silently**,
  because the audio still plays.
- No spoken content is written raw to a log or console anywhere.
- ASCII still holds for identifiers, resource keys, comments, log text, and test names.

## What NOT to do

- Do not fix anything. An inspector who picks up a hammer is no longer an inspector. Report it; the
  Architect hands it back to the builder.
- Do not merge, do not push to `main`, do not open a pull request.
- Do not soften a finding because the suite is green or the write-up is confident.

## Output

`docs/MISSION-multilingual-REVIEW.md`, with each finding as: what it is, where (file:line), how it
fails in practice, and how you verified it. Say plainly which claims you checked and which you could
not. If you find nothing real, say that too - a clean review that names what it looked at is worth
more than a manufactured finding.
