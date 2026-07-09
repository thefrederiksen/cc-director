# Transcription Quality Loop

Goal: keep making dictation transcription + dictionary cleanup better over time, measure it rigorously
(multilingually), let any agent analyze it locally, and always have the best version deployed on the
live Gateway. Everything here is LOCAL - no transcription content leaves the machine.

Status legend: DONE (built + deployed), NEXT (planned, ready to build), RESEARCH (informing design).

---

## 1. Local telemetry logging - DONE

`TranscriptionTelemetryLog` (`src/CcDirector.Gateway/Transcription/`) writes one JSON line per turn to
`%LOCALAPPDATA%\cc-director\transcription-log\transcription-YYYYMMDD.jsonl`. Wired into
`GatewayTranscriptionService.TranscribeAsync` (the single owner) with added Stopwatch timing. Records:
timestamp, turn id, outcome, mode, models, audio bytes, transcriptionMs, cleanupMs, cleanup applied +
the exact find->replace changes, char/word counts, and the raw + cleaned text. Fail-safe; `TextEnabled`
can omit text.

## 2. Local analysis API - DONE

`TranscriptionAnalysisEndpoint` exposes read-only queries over the log so any agent can ask the Gateway
how fast and how good transcription is:

- `GET /transcription/stats [?days=N|?since=ISO]` - counts by outcome, latency percentiles
  (transcription + cleanup), cleanup-applied rate, word/char/byte totals.
- `GET /transcription/turns [?days|since] [?limit=N]` - raw recorded turns, newest first.
- `GET /transcription/terms [?days|since] [?top=N]` - most frequent find->replace corrections.
- `GET /transcription/words [?days|since] [?top=N]` - most frequent spoken words.

First live numbers already proved the deterministic cleanup: cleanupMs p50 = 3 ms (vs the old o4-mini
~5000 ms), transcription p50 ~4.2 s (third-party). This is the data source for everything below.

## 3. Cockpit transcription status - NEXT

A small Cockpit screen (`apps/cockpit`, React) that reads the analysis API and shows: recent latency
(transcription vs cleanup), success/failure mix (surfacing e.g. out-of-credits), turns/day, top
corrected terms, and a plain-language "your transcription is healthy / slow / failing" summary. It also
tells the user they can point an agent at `/transcription/*` to ask their own questions ("how much do I
swear", "which term do I mis-say most"). Purely a consumer of section 2; no new backend needed. Ship
behind the existing Cockpit nav.

## 4. Scientific multilingual eval harness - NEXT (design finalized from research)

We already have `tools/harnesses/transcription-eval-harness/` (audio -> transcription + cleanup, keyword
recall) and 5 recorded English fixtures. Transcription is now third-party, so the new harness focuses on
the DICTIONARY CLEANUP step, holds transcription constant, and is multilingual. The design is lifted from
established ASR-customization / contextual-biasing methodology (GenSEC/HyPoradise, LibriSpeech biasing),
NOT invented.

Central design decision - hold transcription constant (text-in / text-out). Each fixture is:
`{ language (BCP-47), raw_transcript (frozen mishearing), term_list (targets + distractors),
   target_terms[] (gold spans), reference_corrected, gold_edits[] }`. The component gets
`(raw_transcript, term_list)` and must produce the corrected text. Any score change is attributable to
cleanup alone - the third-party ASR is removed as a variable.

Metric core (the important part - report per language, then MACRO-average; never let English dominate):
- **B-WER** (Biased WER) - error rate over the target-term words only = "is the dictionary working".
  Lower is better; this is the headline.
- **U-WER** (Unbiased WER) - error rate over all OTHER words = collateral damage. **Must NOT rise** when
  cleanup is on. This is the over-correction guard, and it is the field-standard expression of our
  "never corrupt correct words" priority.
- **Edit precision / edit recall** and a **do-no-harm regression rate** (correct-token -> wrong-token
  flips per 1000 tokens).
- Secondary: whole-term precision/recall/F1 (SemEval-2013 partial-match via `nervaluate`); overall WER.
- **CER (character-level) for non-space-delimited languages** (zh/ja/th) selected automatically from the
  language tag; WER for space-delimited.

Methodology details:
- **Distractor sweep** (LibriSpeech recipe): run each fixture with N = {0, 100, 1000} distractor terms on
  the list; plotting B-WER and U-WER vs N per language is the definitive over-correction diagnostic.
- Corpus shape per language: ~40% single-term, ~20% multi-term, ~30% no-op / distractor-trap (nothing to
  correct, or a common word a trigger-happy corrector would wrongly replace), ~10% hard (multi-token
  split / homophone).
- Fixture generation tiers: (1) synthetic mishearings (cheap, text-only, scales; homophone/G2P/word-split
  perturbations, LLM-assisted per language then sampled for review), (2) TTS -> real third-party ASR to
  capture authentic mishearings, (3) a small native-speaker-reviewed real-recording set as the acceptance
  gate. Adding a language is a checklist (term inventory + normalizer config + generate + native review),
  no code change - the language tag drives WER-vs-CER and scoring.
- Reuse tooling, do not reinvent metrics: `jiwer` (WER/CER + alignments), `whisper-normalizer`
  (Basic/multilingual normalizer, pinned + versioned), `nervaluate` (entity F1). All Apache-2.0 / MIT.
- Versioned immutable fixtures + pinned tool versions so scores are comparable over time.

## 5. Industry validation + upgrade paths - RESEARCH (done)

Findings (full report saved separately). Our approach is the standard one; we are not reinventing:
- The de-facto post-correction pipeline is: normalize -> slide n-gram windows -> retrieve candidates from
  a fuzzy index over the term list -> score by edit + phonetic distance -> thresholded replace. Our
  matcher already mirrors this. NVIDIA NeMo **SpellMapper** is the open reference (north star).
- **Multilingual precision confirmed:** Unicode edit-distance + Jaro-Winkler is inherently
  language-neutral and "gets you most of the way with zero language resources"; English-only
  Metaphone/Soundex/Microsoft.PhoneticMatching must NOT be the primary signal. This validates the
  language-agnostic rewrite (plain Jaro, no word list) we just shipped.
- **Upgrade paths when we want more recall:**
  - Candidate retrieval: **SymSpell** (C#/.NET, MIT, language-independent) instead of a linear scan;
    handles compound split/merge ("cc director" -> "cc-director"). Metrics: **F23.StringSimilarity** /
    **Fastenshtein** (both .NET, MIT, Unicode).
  - Multilingual phonetics ONLY where sound-alike errors dominate: IPA via grapheme-to-phoneme
    (**epitran**, 100+ languages) + featural distance (**panphon**), compared in IPA space - not English
    Metaphone. Add per-term precomputed IPA rather than a runtime dependency if possible.
  - Borrow Microsoft.PhoneticMatching's `EnHybridDistance` design (phonetic blended with edit distance)
    as the scoring template, swapping its English engine for the multilingual one.
- **Orthogonal wins:** use the transcription provider's own biasing channel (Whisper `initial_prompt`,
  Deepgram keywords, AssemblyAI word_boost, Speechmatics `sounds_like`) so fewer errors reach cleanup;
  and consider an optional per-term `sounds_like`/pronunciation field (every serious product offers it).

## 6. Continuous improvement + always-deploy-best - PROCESS

- Every change to the cleanup matcher is scored on the section-4 harness across all languages before it
  is considered. Track the scoreboard over time (keyed by cleanup-version + fixture-corpus-version).
- **Regression gates, not just goals:** block a release if **U-WER rises OR the do-no-harm rate rises in
  ANY language**, even when B-WER improves. The asymmetry (never corrupt correct words) becomes an
  automated gate. "Best version" = lowest macro B-WER subject to those gates passing everywhere.
- Keep a held-out real-recording set out of the iteration loop as the final acceptance set, so we do not
  overfit the synthetic fixture tiers.
- The best-scoring gated version is what gets built from the working tree and deployed to the live Gateway
  (the same working-tree publish + graceful-swap flow we use now).
- The local telemetry (sections 1-2) is the field feedback loop: real-world latency and correction rates
  tell us where the harness is missing cases; those become new fixtures. Harness (lab) + telemetry
  (field) together drive the iteration.
