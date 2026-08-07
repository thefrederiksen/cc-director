# Transcription + Dictation Cleanup: Speed Research and Findings

Date: 2026-07-08. Status: RESEARCH ONLY (no production changes proposed here beyond
the already-deployed o4-mini cleanup fixes). Goal: make dictation as FAST as possible
(speed first, cost second).

This document synthesizes four parallel research threads plus a working proof-of-concept.
Supporting test app: `tools/harnesses/cleanup-prototype/phonetic_cleanup_prototype.py`.
Eval harness + fixtures: `tools/harnesses/transcription-eval-harness/`,
`artifacts/transcription-fixtures/`.

---

## 1. The measured baseline (why this matters)

From live Director logs and the eval harness (`artifacts/transcription-eval-runs/`),
the current path is `gpt-4o-transcribe` (transcription) + `o4-mini` (cleanup), both via
the `https://devthrottle.com/api/v1` proxy:

| Stage | Measured | Notes |
|-------|----------|-------|
| Transcription (gpt-4o-transcribe via proxy) | 2,000 - 18,000 ms | Highly variable; the SAME 14.7s clip took 4,163 ms in one run and 9,155 ms in another |
| Cleanup (o4-mini) | ~4,900 ms, or total failure | Was HTTP 400 on every fixture (temperature bug, now fixed); when working it "thinks" ~5s and often proposes 0 edits |
| End-to-end dictation | ~7 - 13 s typical | Too slow |

Two independent problems: (1) transcription itself is slow and inconsistent through the
proxy; (2) the cleanup is an LLM round-trip that adds ~5s and frequently contributes nothing.

Note: OpenAI direct is ~0.8s for these clips; the devthrottle.com proxy hop is a large,
variable part of the latency, not just the model.

---

## 2. Fastest transcription (thread: fast providers)

Speed-first ranking for short dictation clips (~2s-2min), .NET Windows host w/ optional GPU:

| Option | Type | ~15s clip | Cost/hr audio | Custom vocab | .NET fit |
|--------|------|-----------|---------------|--------------|----------|
| **Groq whisper-large-v3-turbo** | Hosted | **~0.2 s** | ~$0.02 | prompt hint | Excellent (OpenAI-compatible) |
| Fireworks whisper-v3-turbo | Hosted | ~0.2-0.4 s | ~$0.05 | prompt hint | Excellent (OpenAI-compatible) |
| Deepgram Nova-3 | Hosted | ~0.3 s (150ms interim) | ~$0.26 batch | strong keyterms | Good (REST/WS) |
| AssemblyAI Universal | Hosted | <0.3 s stream | ~$0.15 | strongest keyterms | Good (REST/WS) |
| gpt-4o-transcribe (CURRENT, via proxy) | Hosted | **7-8.5 s** | $0.36 | prompt hint | trivial |
| **Parakeet-TDT 0.6B (sherpa-onnx)** | Local | <0.05 s GPU / ~0.5 s CPU | $0 | weak | Good (C# NuGet, in-proc) |
| **Whisper.net (whisper.cpp)** | Local | ~0.1-0.5 s GPU / 1-5 s CPU | $0 | initial_prompt | Best (native C#, Vulkan/CUDA/CPU) |

> CORRECTION, 2026-08-07 (owner ruling, issue 2481): ignore the "Custom vocab" column when
> choosing an engine. DevThrottle does not use any provider's custom-vocabulary channel -
> no `prompt` hint, no keyterms, no `initial_prompt` - because priming the transcriber
> changes wording and sentence structure and corrupts the record of what was said. Terms
> are corrected on the finished transcript instead. Speed, cost and .NET fit are the real
> selection criteria here; that column is not one.

**Recommendation:**
- **Fastest win, minimal effort: switch the default to Groq `whisper-large-v3-turbo`.**
  It is an OpenAI-compatible endpoint, so the Gateway proxy changes only base URL + key +
  model name. ~20-40x faster than today and cheaper. Keep gpt-4o-transcribe as a MANUAL
  second provider (no silent fallback - honor the no-fallback rule).
- **Own-the-stack / offline path for GPU machines: NVIDIA Parakeet-TDT via sherpa-onnx**
  (C# in-process, sub-100ms GPU, ~30x realtime even CPU) or **Whisper.net** (single NuGet,
  cross-vendor GPU via Vulkan). Free, private, even faster. This aligns with the planned
  Phase 5 "Gateway-local Whisper.net backend" in `docs/new_architecture/transcription/`.
- **Streaming** (Deepgram/AssemblyAI interim, or local sherpa-onnx) can make dictation FEEL
  instant (words ~150ms after speech) - a later enhancement, not needed once Groq lands.

Avoid DeepInfra for the latency-critical path (the 4-45s variability already experienced).

---

## 3. Dictionary cleanup WITHOUT an LLM (threads: non-LLM cleanup + ASR biasing)

Both research threads converge on the SAME architecture: **no LLM in the hot path.**

### 3a. Bias the transcriber (nearly free)

> CORRECTION, 2026-08-07 (owner ruling, issue 2481): this recommendation was REJECTED and the
> code written for it has been deleted. The vocabulary is never passed to the transcriber in
> any form, and the engine-comparison table above must not be read as making keyword-biasing
> strength a selection criterion. Priming the transcriber makes it steer toward the suggested
> words, changing wording and sentence structure and corrupting the record of what was said.
> Section 3b - the deterministic matcher on the finished transcript - is the whole approach.
> The research is left as written, as the dated record of what was considered.

Pass the known vocabulary as a short glossary in the transcription `prompt` parameter so most
terms come out right at the source. Caveats (OpenAI cookbook): the Whisper/gpt-4o prompt hint
fixes SOME rare terms but not all, and does not reliably enforce casing (lowercase `mindzie`).
Stronger engines if we ever switch: Deepgram Keyterm / AssemblyAI keyterms_prompt (~90% recall,
casing preserved) / Speechmatics `sounds_like` (encode the exact mishearings).

> CORRECTION, 2026-08-07 (owner ruling, issue 2481): none of these engine features will be
> used, however strong. Switching engine is a speed and cost decision only; the keyword
> channel is not a reason to pick one, because we never send words to the transcriber.

### 3b. Replace the o4-mini cleanup with an in-process deterministic matcher
The cleanup's only job is fixing a known, finite custom vocabulary - a deterministic
string-matching problem, not a generative one. The mishearings are phonetic
("Mindsey"->mindzie, "Conty"->ConPTY, "Akmeflow"->acmeflow), so the right tool is a
**phonetic index + edit-distance rescore + confidence threshold + common-word stop-list.**

Recommended .NET libraries (all MIT): `F23.StringSimilarity` (Jaro-Winkler, Damerau-Levenshtein,
n-gram) + `Fastenshtein` (fastest Levenshtein) + a vendored Double Metaphone. Shortcut with risk:
`Microsoft.PhoneticMatching` does the whole thing but is unmaintained since 2021.

This slots in behind the EXISTING exact/alias map pass (`CleanupOrchestrator.TryApplyKnownMistranscriptions`)
and must emit `TranscriptEdit`s through the existing `TranscriptEditEngine.Validate/Apply`, so the
safety invariant (only canonical swaps, boundary-aware) is unchanged. It replaces only the LLM
proposal mechanism (stage b), catching the NEW variants that currently escape to o4-mini.

**Can biasing alone drop cleanup?** No - no engine guarantees rare-proper-noun spelling/casing.
Keep a cleanup step, but make it the deterministic matcher above, not an LLM.

> CORRECTION, 2026-08-07 (owner ruling, issue 2481): the question is moot - there is no
> biasing at all. Nothing is sent to the transcriber but audio, so the cleanup pass is not
> merely kept, it is the ONLY correction stage there is.

---

## 4. Proof: the deterministic cleanup prototype

`tools/harnesses/cleanup-prototype/phonetic_cleanup_prototype.py` implements the two-stage
pipeline (exact/alias map -> phonetic-fuzzy) over the REAL transcripts gpt-4o-transcribe produced
on the fixtures, plus realistic mishearings NOT hand-listed in the dictionary. Uses `jellyfish`
(Metaphone, Jaro-Winkler) + `rapidfuzz` (Levenshtein), both already in the repo's Python env.

Measured result:

| Metric | exact/alias map only (today's fast pass) | exact + phonetic-fuzzy (proposed) |
|--------|------------------------------------------|-----------------------------------|
| Custom terms recovered | 7 / 11 | **11 / 11** |
| False edits on control sentence | 0 | 0 |
| Avg latency | ~0 (regex) | **~158 microseconds** |
| vs o4-mini (~4,881 ms live) | - | **~30,000x faster** |

It caught `Mindsey->mindzie`, `Akmeflow->acmeflow`, `Terascale->Tailscale`, `Acme Flow->acmeflow`,
`CONPTY->ConPTY` - none of which are hand-listed as wrong-forms - while leaving the no-jargon
control sentence untouched (no dropped words, no false rewrites). This is the case that forces
the ~5s o4-mini call today, handled in microseconds.

Precision guards proven necessary during iteration (both were real bugs caught by the control/observation):
- A multi-word window must not swallow an already-correct canonical term ("the cc-director" -> drop "the").
- A multi-word window must not glue a term to a stop word ("Akmeflow and" -> drop "and").
Both are enforced by skipping multi-token windows containing a canonical term or a common word.

---

## 5. Recommended direction (phased, research-backed - NOT yet implemented)

1. **Transcription: adopt Groq `whisper-large-v3-turbo` as the default provider.** Biggest,
   cheapest, lowest-risk latency win (~20-40x). OpenAI-compatible; minimal Gateway change.
2. **Cleanup: replace the o4-mini LLM stage with the in-process phonetic+edit-distance matcher**
   behind the existing exact map, validated by the existing `TranscriptEditEngine`. Removes ~5s
   and an entire class of network failures; deterministic and unit-testable.
3. **Bias transcription** with the vocabulary glossary via the `prompt` parameter (nearly free,
   reduces how often cleanup is even needed).
   > CORRECTION, 2026-08-07 (owner ruling, issue 2481): step 3 is REJECTED and will not be
   > built. The vocabulary is never passed to the transcriber in any form. Priming it makes it
   > steer toward the suggested words, changing wording and sentence structure and corrupting
   > the record of what was said. Steps 1, 2 and 4 are unaffected. Left in place as the dated
   > record of what was recommended.
4. **Later / optional:** local Parakeet/Whisper.net backend for GPU machines (offline, free,
   fastest); streaming for live on-screen dictation.

Each step is independently shippable and independently measurable on the existing fixtures.

---

## 6. What still needs a key or a download to benchmark

- **Groq / Deepgram / AssemblyAI comparison on the fixtures:** needs the respective API key
  (none currently on this machine). The eval harness can run it by adding a matrix entry with the
  provider base URL + key env; `post_transcription` already speaks the OpenAI-compatible wire.
- **Local Parakeet / Whisper.net benchmark:** needs a one-time model download (~150MB-2.4GB) and,
  for GPU, the CUDA/Vulkan runtime. Provable on this machine but heavier setup.

The cleanup replacement (section 4) needs neither - it is fully proven offline today.
