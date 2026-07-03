# Phase 0 Report

Verdict: FAIL (8/9 expected company-term occurrences recovered in the final variant)

## Method

Generated 3 synthetic clips with OpenAI tts-1 (voice=alloy).
Each clip transcribed with gpt-4o-transcribe in three variants:

1. No prompt parameter (baseline).
2. With the prompt parameter packed with the company term glossary.
3. Variant 2 transcript run through Claude Haiku with the term list in the system prompt.

Pass criterion: every expected company term appears in the variant 3 transcript for every clip (case-insensitive substring match).

## Results

### Clip 1

Expected sentence: `I sent the cc-director patch to acmeflow before the CenCon review.`

Expected company terms: acmeflow, CenCon, cc-director

**no_prompt** [MISSING: acmeflow, CenCon, cc-director]

> I sent the CC Director patch to Akmeflow before the SENCON review.

**with_prompt** [OK]

> I sent the cc-director patch to acmeflow before the CenCon review.

**with_prompt_plus_cleanup** [OK]

> I sent the cc-director patch to acmeflow before the CenCon review.

### Clip 2

Expected sentence: `Example User needs the Avalonia changes for ConPTY tested by Friday.`

Expected company terms: ConPTY, Avalonia, Example User

**no_prompt** [MISSING: ConPTY, Example User]

> Example Usar needs the Avalonia changes for ContUI tested by Friday.

**with_prompt** [MISSING: ConPTY]

> Example User needs the Avalonia changes for Contui tested by Friday.

**with_prompt_plus_cleanup** [MISSING: ConPTY]

> Example User needs the Avalonia changes for Contui tested by Friday.

### Clip 3

Expected sentence: `Tell acmeflow that the CenCon report is ready and ping the cc-director gateway team.`

Expected company terms: acmeflow, CenCon, cc-director

**no_prompt** [MISSING: acmeflow, CenCon, cc-director]

> Tell Akmeflow that the SenCon report is ready and ping the CC Director Gateway team.

**with_prompt** [OK]

> Tell acmeflow that the CenCon report is ready and ping the cc-director gateway team.

**with_prompt_plus_cleanup** [OK]

> Tell acmeflow that the CenCon report is ready and ping the cc-director gateway team.

## Interpretation

Variant 3 did not recover every expected company term. Inspect transcripts.json. Possible follow-ups before committing to Phase 1:

- Refine the Haiku cleanup prompt with explicit positive and negative examples for the terms that slipped through.
- Try gpt-4o-mini-transcribe for comparison (cheaper and may behave differently with the prompt parameter).
- Note: TTS pronunciation may not match how a human says these terms. Real-voice Phase 2 testing could land closer to one side or the other.
- Reconsider AssemblyAI keyterm boosting if the gap is irreducible (out of scope per PLAN.md, but a known fallback).
