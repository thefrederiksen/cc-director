# Transcription Quality Loop

Goal: keep making dictation transcription + dictionary cleanup better over time, measure it rigorously
(multilingually), let any agent analyze it locally, and always have the best version deployed on the
live Gateway.

**Where transcripts go, precisely.** Everything in sections 1-3 - history, analysis, the Cockpit
screen - is LOCAL: no transcription content leaves the machine. That is NOT true of the correction
step itself since the judge landed (devthrottle_internal#1554). When the matcher nominates a
candidate, the utterance and that bounded candidate list are sent to the hosted judge over the
DevThrottle inference route, and the judge answers with candidate ids. It is skipped entirely when
nothing is nominated, which is most utterances, but "most" is not "never" and this document said
never. Nothing is ever sent to the speech-to-text provider (issue 2481); that constraint is
unchanged.

Status legend: DONE (built + deployed), NEXT (planned, ready to build), RESEARCH (informing design).

---

## 1. Local history - DONE

`TranscriptionHistoryLog` (`src/CcDirector.Gateway/Transcription/`) writes one minimized JSON line per turn to
`%LOCALAPPDATA%\cc-director\transcription-history\transcription-YYYYMMDD.jsonl`. Wired into
`GatewayTranscriptionService.TranscribeAsync` (the single owner) with Stopwatch timing. Records:
timestamp, turn id, outcome, transcription and cleanup duration, cleanup-applied status, exact
find-to-replace correction terms, and aggregate character/word counts. It never records raw or cleaned
transcript text, model names, audio sizes/content, or provider error bodies. Files older than 30 days are
removed. Associated troubleshooting audio has a 24-hour/500-clip ceiling, and the owner can clear both
the history and audio through one control.

## 2. Local analysis API - DONE

`TranscriptionAnalysisEndpoint` exposes owner queries over the history so any agent can ask the Gateway
how fast and how good transcription is:

- `GET /transcription/stats [?days=N|?since=ISO]` - counts by outcome, latency percentiles
  (transcription + cleanup), cleanup-applied rate, and word/character totals.
- `GET /transcription/turns [?days|since] [?limit=N]` - minimized turn records, newest first.
- `GET /transcription/terms [?days|since] [?top=N]` - most frequent find->replace corrections.
- `DELETE /transcription/history` - clears the locally retained history and troubleshooting audio.

First live numbers already proved the deterministic cleanup: cleanupMs p50 = 3 ms (vs the old o4-mini
~5000 ms), transcription p50 ~4.2 s (third-party). This is the data source for everything below.

## 3. Cockpit transcription status - DONE

A small Cockpit screen (`apps/cockpit`, React) that reads the analysis API and shows: recent latency
(transcription vs cleanup), success/failure mix (surfacing e.g. out-of-credits), turns/day, top
corrected terms, and a plain-language "your transcription is healthy / slow / failing" summary. It also
provides a Clear local history control and tells the user they can point an agent at `/transcription/*`
to ask timing and correction questions. It is purely a consumer of section 2.

## 4. Scientific multilingual eval harness - NEXT (design finalized from research)

We already have `tools/harnesses/transcription-eval-harness/` (audio -> transcription + cleanup, keyword
recall) and 5 recorded English fixtures. Transcription is now third-party, so the new harness focuses on
the DICTIONARY CLEANUP step, holds transcription constant, and is multilingual. The design is lifted from
established ASR-customization / contextual-biasing methodology (GenSEC/HyPoradise, LibriSpeech biasing),
NOT invented. To be clear about what is borrowed: the MEASUREMENT method (the B-WER / U-WER split
below), not the technique. We do not do contextual biasing - nothing is sent to the transcriber but
audio (issue 2481). Transcription is held constant and only the cleanup step is measured.

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
- **RULED OUT - the provider's own biasing channel.** This bullet used to propose exactly that
  (Whisper `initial_prompt`, Deepgram keywords, AssemblyAI word_boost, Speechmatics `sounds_like`,
  plus a per-term pronunciation field) so fewer errors would reach cleanup. The owner ruled against
  it on 2026-08-07 (issue 2481) and the dead code for it has been deleted. **Do not propose it
  again.** Nothing - no vocabulary, no keyword list, no pronunciation hint - is ever sent to the
  speech-to-text provider; it gets audio only. Priming the transcriber makes it steer toward the
  suggested words, changing wording and sentence structure rather than just spelling, and that
  corrupts the record of what was actually said. Meaning preservation outranks the error rate: a
  faithful transcript with a wrong spelling beats a fluent one with altered meaning, and the wrong
  spelling is what the cleanup pass exists to fix. This constrains the whole section - every recall
  improvement has to come from the cleanup matcher, which is why that is where the work goes.

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
- The bounded local history (sections 1-2) is an owner-controlled feedback loop: real-world latency and
  correction rates can reveal where the harness is missing cases; those can become new fixtures only
  through an explicit user-directed workflow. The history is never uploaded automatically.
