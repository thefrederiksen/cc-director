# Dictionary-Cleanup Eval Harness (multilingual)

Scientific evaluation of the deterministic dictionary-cleanup step, using established
ASR-customization / contextual-biasing methodology (not invented metrics). See
`docs/architecture/transcription-quality-loop.md` for the design rationale.

Only the MEASUREMENT is borrowed from that literature, not the technique: DevThrottle does no
contextual biasing and sends nothing but audio to the transcriber (issue 2481). The "biased"
tokens named below are the standard B-WER term for the target terms being scored - nothing is
being biased.

## What it measures

Transcription is held CONSTANT. Each fixture is text-in / text-out: a frozen `raw_transcript`, a
`term_list` (targets + distractors), the gold `reference_corrected`, and the gold `gold_edits`. The
harness POSTs each fixture to the Gateway's `POST /transcription/cleanup` (the REAL production cleanup)
and scores the returned text + edits. Any score change is attributable to the cleanup alone.

Metrics, per language then MACRO-averaged (never let one language dominate):

- **B-WER** - error rate over TARGET-TERM tokens only. "Is the dictionary working." Lower is better.
- **U-WER** - error rate over all OTHER tokens. Collateral damage. **Must not rise = over-correction gate.**
- **edit precision / recall** - of the corrections made vs. the gold corrections.
- **false edits** - corrections made that were NOT gold (do-no-harm signal; on no-op fixtures every edit
  is a false edit). **This is the primary release gate: it must be 0.**
- **CER** (character-level) is used automatically for non-space-delimited languages (ja/zh/th).

## Run

The Gateway must be running (serves `/transcription/cleanup`). Auth token is read from
`%LOCALAPPDATA%\cc-director\config\config.json`.

```
python tools\harnesses\dictionary-cleanup-eval\eval_cleanup.py
```

Pure standard library (no pip installs), like the sibling harnesses. `jiwer` + `whisper-normalizer`
are the trusted cross-check to add later if desired.

## Fixture schema (`fixtures/<lang>.json`, an array)

```jsonc
{
  "id": "de-001-correct",
  "language": "de",                       // BCP-47; ja/zh/th auto-switch to CER
  "raw_transcript": "ich benutze Akmeflow jeden Tag",   // frozen ASR mishearing
  "term_list": ["acmeflow", "ConPTY"],    // targets + distractors given to cleanup
  "reference_corrected": "ich benutze acmeflow jeden Tag",
  "target_terms": ["acmeflow"],           // defines the 'biased' tokens for B-WER
  "gold_edits": [{"from": "Akmeflow", "to": "acmeflow"}]
}
```

## Adding a language (checklist, not a code change)

1. Add `fixtures/<lang>.json` with a balanced mix: ~40% single-term, ~20% multi-term, ~30% no-op /
   distractor-trap (nothing to correct, or a common word a trigger-happy corrector would wrongly
   replace), ~10% hard (multi-token split / homophone).
2. Have a native speaker sanity-check the gold corrections.
3. Run. The language tag drives WER-vs-CER and scoring automatically.

## Current findings (first run, 2026-07-09)

Precision is perfect everywhere (**U-WER 0, false edits 0 = do-no-harm PASS**); recall has two real gaps
the harness surfaced, which are the iteration backlog:

- **es**: `Teraskale -> Tailscale` missed - a phonetic variant that falls below the conservative
  single-word threshold. (Recall gap, not a false positive.)
- **ja**: a Latin term glued to CJK characters (`今日はAcme Flow`) is not isolated by the matcher's
  word tokenizer, so it is not corrected. CJK needs script-boundary tokenization (split Latin runs out
  of CJK runs). Also note: a CJK fixture whose only difference is Latin casing/spacing collapses to
  identical under CER normalization - CJK fixtures should use genuinely different characters.

These are exactly what the harness is for: measure the gaps, fix, re-run, and only ship a version that
does not regress U-WER / do-no-harm in ANY language.
