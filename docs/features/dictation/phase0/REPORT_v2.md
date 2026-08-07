# Phase 0 Report

Verdict: FAIL (8/9 expected company-term occurrences recovered in the final variant)

## Method

> **CORRECTION, 2026-08-07 (owner ruling, issue 2481).** The variants below that use the
> transcription **prompt parameter** describe an approach that was REJECTED, and the code for it
> has been deleted. Read the `with_prompt` results as a record of an experiment, NOT as a reason
> to build it: vocabulary and steering hints are never sent to the speech-to-text provider,
> because priming it makes the model steer toward the suggested words, changing wording and
> sentence structure and corrupting the record of what was actually said. Meaning preservation
> outranks term recall. What ships is the `no_prompt` transcript plus a correction pass over the
> finished text.

Generated 3 synthetic clips with OpenAI tts-1 (voice=alloy).
Each clip transcribed with gpt-4o-transcribe in three variants:

1. No prompt parameter (baseline).
2. With the prompt parameter packed with the company term glossary.
3. Variant 2 transcript run through Claude Haiku with the term list in the system prompt.

Pass criterion: every expected company term appears in the variant 3 transcript for every clip (case-insensitive substring match).

## Results

> **CORRECTION (issue 2481):** the `with_prompt` rows below are the REJECTED approach. A better
> score there is not an argument for it - priming the transcriber changes wording and sentence
> structure, which these clip-level term checks do not measure. See the note at the top.

### Clip 1

Expected sentence: `I sent the cc-director patch to acmeflow before the CenCon review.`

Expected company terms: acmeflow, CenCon, cc-director

**no_prompt** [MISSING: acmeflow, CenCon, cc-director]

> I sent the CC director patch to Akmeflow before the SENCON review.

**with_prompt** [OK]

> I sent the cc-director patch to AcmeFlow before the CenCon review.

**with_prompt_plus_cleanup** [OK]

> I sent the cc-director patch to acmeflow before the CenCon review.

### Clip 2

Expected sentence: `Example User needs the Avalonia changes for ConPTY tested by Friday.`

Expected company terms: ConPTY, Avalonia, Example User

**no_prompt** [MISSING: ConPTY, Example User]

> Example User needs the Avalonia changes for ContiUI tested by Friday.

**with_prompt** [MISSING: ConPTY]

> Example User needs the Avalonia changes for Contui tested by Friday.

**with_prompt_plus_cleanup** [MISSING: ConPTY]

> Example User needs the Avalonia changes for CenCon tested by Friday.

### Clip 3

Expected sentence: `Tell acmeflow that the CenCon report is ready and ping the cc-director gateway team.`

Expected company terms: acmeflow, CenCon, cc-director

**no_prompt** [MISSING: acmeflow, CenCon, cc-director]

> Tell Minzi that the Sencon report is ready and ping the CC Director Gateway team.

**with_prompt** [OK]

> Tell acmeflow that the CenCon report is ready and ping the cc-director gateway team.

**with_prompt_plus_cleanup** [OK]

> Tell acmeflow that the CenCon report is ready and ping the cc-director gateway team.

## Interpretation

> **CORRECTION (issue 2481):** the conclusion below rests on the transcription **prompt
> parameter**, which the owner REJECTED on 2026-08-07; the code for it is deleted. Nothing is
> sent to the transcriber but audio, and the listed words are substituted afterwards on the
> finished transcript - priming it changes wording and sentence structure and corrupts the
> record of what was said. Any follow-up here that tunes a transcription prompt, or that reaches
> for a provider's keyterm boosting, is closed. Only the cleanup pass is open.

Variant 3 did not recover every expected company term. Inspect transcripts.json. Possible follow-ups before committing to Phase 1:

- Refine the Haiku cleanup prompt with explicit positive and negative examples for the terms that slipped through.
- Try gpt-4o-mini-transcribe for comparison (cheaper and may behave differently with the prompt parameter).
- Note: TTS pronunciation may not match how a human says these terms. Real-voice Phase 2 testing could land closer to one side or the other.
- Reconsider AssemblyAI keyterm boosting if the gap is irreducible (out of scope per PLAN.md, but a known fallback).
