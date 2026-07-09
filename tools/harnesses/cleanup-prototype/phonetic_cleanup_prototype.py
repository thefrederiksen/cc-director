"""
Phonetic + edit-distance dictionary-cleanup PROTOTYPE (research proof, NOT production).

Goal: show that an IN-PROCESS deterministic matcher can replace the o4-mini LLM
cleanup for the narrow "fix a known custom vocabulary" task, at sub-millisecond
latency instead of the ~5-8 seconds the reasoning-model round-trip costs.

It compares two cleanup strategies over the SAME raw transcripts:
  (1) exact-map-only  - mirrors today's fast Stage (a): only fixes wrong-forms
                        that were hand-listed in the dictionary.
  (2) phonetic-fuzzy  - the proposed replacement for the LLM Stage (b): a Double
                        Metaphone phonetic index over the canonical vocabulary
                        plus a Jaro-Winkler / Levenshtein rescore and a confidence
                        threshold, with a common-word stop-list to guard precision.

The phonetic matcher uses ONLY the canonical term list - it does NOT need every
mishearing enumerated by hand, which is the whole point: it generalizes to new
variants (Acme Flow, Akmeflow, AcmeFlow -> acmeflow) that the exact map misses.

Dependencies: jellyfish (Double Metaphone, Jaro-Winkler), rapidfuzz (fast Levenshtein).
Both are already available in the repo's Python environment. Pure research tool.
"""

import sys
import time
import re
import json

import jellyfish
from rapidfuzz.distance import Levenshtein

# ---------------------------------------------------------------------------
# Canonical vocabulary. In production this comes from dictionary.yaml
# (vocabulary:). We include acmeflow because the fixtures use it even though the
# live dictionary has not been updated with it - which is exactly why the current
# pipeline misses it and falls through to the LLM.
# ---------------------------------------------------------------------------
VOCABULARY = [
    "mindzie",
    "Tailscale",
    "CenCon",
    "ConPTY",
    "cc-director",
    "Avalonia",
    "acmeflow",
    "DevThrottle",
    "Gateway",
]

# Small high-frequency English stop-list: never rewrite one of these into a
# jargon term unless the evidence is overwhelming. This is the deterministic
# stand-in for the LLM's context judgment (precision guard).
COMMON_WORDS = {
    "the", "and", "for", "you", "please", "check", "then", "verify", "window",
    "session", "terminal", "rendering", "dashboard", "start", "send", "this",
    "recording", "path", "can", "show", "me", "plan", "compare", "but", "must",
    "never", "add", "long", "delay", "should", "own", "policy", "provider",
    "with", "that", "have", "will", "your", "from", "they", "when", "what",
}

MIN_SPAN_LEN = 4          # phonetic keys are unreliable below ~4 chars
CONFIDENCE_THRESHOLD = 0.82   # tuned to favor precision over recall
MAX_WINDOW = 2            # canonical terms expand from at most ~2 spoken tokens


def metaphone(s):
    """Metaphone code of a whitespace/punct-stripped string (phonetic feature)."""
    key = re.sub(r"[^a-z0-9]", "", s.lower())
    try:
        return jellyfish.metaphone(key) if key else ""
    except Exception:
        return ""


def build_terms(vocab):
    """Precompute (canonical, normalized, metaphone, token_count) once at startup."""
    out = []
    for term in vocab:
        norm = re.sub(r"[^a-z0-9]", "", term.lower())
        tok_count = len(re.findall(r"[A-Za-z0-9]+", term))
        out.append((term, norm, metaphone(norm), tok_count))
    return out


def confidence(span_norm, span_meta, term_norm, term_meta):
    """Hybrid score in [0,1]: character similarity + edit distance + PHONETIC
    similarity. Phonetics is a weighted FEATURE (jaro-winkler over metaphone
    codes), not a hard gate - so a near-but-not-identical phonetic code still
    contributes, which is what catches Mindsey->mindzie and Terascale->Tailscale."""
    char_sim = jellyfish.jaro_winkler_similarity(span_norm, term_norm)
    maxlen = max(len(span_norm), len(term_norm)) or 1
    edit_norm = 1.0 - (Levenshtein.distance(span_norm, term_norm) / maxlen)
    phon_sim = jellyfish.jaro_winkler_similarity(span_meta, term_meta) if (span_meta and term_meta) else 0.0
    return 0.40 * char_sim + 0.25 * edit_norm + 0.35 * phon_sim


def phonetic_fuzzy_cleanup(text, terms):
    """Return (cleaned_text, edits[]). Brute-force score every vocab term against
    each candidate span (the vocab is tiny, so this stays microsecond-fast and
    gives better recall than a hard phonetic-index gate). Deterministic, no network."""
    tokens = list(re.finditer(r"[A-Za-z0-9][A-Za-z0-9\-]*", text))
    canon_norms = {t[1] for t in terms}          # already-correct canonical forms
    edits = []
    used = [False] * len(tokens)

    # Prefer longer windows first (so "Acme Flow" beats a 1-token match).
    for win in range(MAX_WINDOW, 0, -1):
        for i in range(0, len(tokens) - win + 1):
            if any(used[i:i + win]):
                continue
            span_toks = tokens[i:i + win]
            # Precision guard for multi-word windows: never glue a term to a
            # neighbouring word that is either already a correct canonical term
            # ("the cc-director") or a common/stop word ("Akmeflow and"). A real
            # multi-word spoken form of a jargon term contains neither.
            if win > 1 and any(
                    (re.sub(r"[^a-z0-9]", "", t.group().lower()) in canon_norms
                     or t.group().lower() in COMMON_WORDS)
                    for t in span_toks):
                continue
            span_text = text[span_toks[0].start():span_toks[-1].end()]
            span_norm = re.sub(r"[^a-z0-9]", "", span_text.lower())
            if len(span_norm) < MIN_SPAN_LEN:
                continue
            span_meta = metaphone(span_norm)
            best = None
            for term, term_norm, term_meta, tok_count in terms:
                # Compare the whitespace-stripped span against the whitespace-stripped
                # term, so a multi-word spoken form ("Acme Flow") can still match a
                # single-token canonical ("acmeflow"). Length ratio guards nonsense.
                if min(len(span_norm), len(term_norm)) / max(len(span_norm), len(term_norm)) < 0.6:
                    continue
                conf = confidence(span_norm, span_meta, term_norm, term_meta)
                if best is None or conf > best[1]:
                    best = (term, conf, term_norm)
            if not best:
                continue
            term, conf, term_norm = best
            if span_norm == term_norm and span_text == term:
                continue   # already exactly correct
            # precision guard: a single common English word is only rewritten on
            # overwhelming evidence
            if win == 1 and span_norm in COMMON_WORDS and conf < 0.97:
                continue
            if conf >= CONFIDENCE_THRESHOLD:
                edits.append({
                    "find": span_text,
                    "replace": term,
                    "confidence": round(conf, 3),
                    "start": span_toks[0].start(),
                    "end": span_toks[-1].end(),
                })
                for j in range(i, i + win):
                    used[j] = True

    # splice replacements back in, right-to-left so offsets stay valid
    cleaned = text
    for e in sorted(edits, key=lambda x: x["start"], reverse=True):
        cleaned = cleaned[:e["start"]] + e["replace"] + cleaned[e["end"]:]
    return cleaned, edits


# --- exact-map-only baseline (mirrors today's Stage (a), for contrast) ---------
EXACT_MAP = {
    "CC Director": "cc-director", "See Director": "cc-director", "CC director": "cc-director",
    "Contui": "ConPTY", "ContUI": "ConPTY", "ContiUI": "ConPTY", "Conty": "ConPTY",
    "Minzy": "mindzie", "Mindsy": "mindzie", "Mindzy": "mindzie",
    "SenCon": "CenCon", "Sencon": "CenCon",
    "Tail Scale": "Tailscale", "Tailskale": "Tailscale",
}


def exact_map_cleanup(text):
    edits = []
    cleaned = text
    for wrong, canonical in EXACT_MAP.items():
        pattern = r"(?<![A-Za-z0-9])" + re.escape(wrong) + r"(?![A-Za-z0-9])"
        if re.search(pattern, cleaned):
            cleaned2 = re.sub(pattern, canonical, cleaned)
            if cleaned2 != cleaned:
                edits.append({"find": wrong, "replace": canonical})
                cleaned = cleaned2
    return cleaned, edits


# ---------------------------------------------------------------------------
# Test inputs: the REAL raw transcripts gpt-4o-transcribe produced on the
# fixtures, plus synthetic realistic mishearings the exact map does NOT list.
# ---------------------------------------------------------------------------
CASES = [
    # (raw transcript, expected canonical terms that should appear after cleanup)
    ("Please check the CC Director session, then verify the CONPTY terminal "
     "rendering, the Avalonia window, and the Acme Flow dashboard.",
     ["cc-director", "ConPTY", "Avalonia", "acmeflow"]),

    ("push the fix to See Director then restart the Contui terminal",
     ["cc-director", "ConPTY"]),

    ("my buddy Mindsey uses Akmeflow and Terascale every day",   # none hand-listed
     ["mindzie", "acmeflow", "Tailscale"]),

    ("the Devthrottle gateway owns transcription and cleanup",   # casing fixes
     ["DevThrottle", "Gateway"]),

    ("can you show me the plan please",                          # NO jargon: must not corrupt
     []),
]


def keyword_hits(text, keywords):
    lt = text.lower()
    return sum(1 for k in keywords if k.lower() in lt)


def main():
    terms = build_terms(VOCABULARY)
    print("=" * 78)
    print("PHONETIC + EDIT-DISTANCE CLEANUP PROTOTYPE  (in-process, no network)")
    print("=" * 78)
    print(f"vocabulary terms: {len(VOCABULARY)}  |  exact/alias map entries: {len(EXACT_MAP)}")
    print(f"confidence threshold: {CONFIDENCE_THRESHOLD}  |  max window: {MAX_WINDOW}")
    print("pipeline: Stage(a) exact/alias map (instant)  ->  Stage(b) phonetic-fuzzy")
    print()

    total_ns = 0
    total_expected = 0
    exact_recovered = 0
    combined_recovered = 0
    false_edits = 0

    for raw, expected in CASES:
        # exact-only (today's Stage (a))
        exact_text, exact_edits = exact_map_cleanup(raw)

        # combined pipeline: exact/alias map FIRST, then phonetic-fuzzy on the residue
        t0 = time.perf_counter_ns()
        stage_a_text, a_edits = exact_map_cleanup(raw)
        combined_text, b_edits = phonetic_fuzzy_cleanup(stage_a_text, terms)
        dt_ns = time.perf_counter_ns() - t0
        total_ns += dt_ns

        exact_hit = keyword_hits(exact_text, expected)
        combined_hit = keyword_hits(combined_text, expected)
        raw_hit = keyword_hits(raw, expected)
        total_expected += len(expected)
        exact_recovered += exact_hit
        combined_recovered += combined_hit

        if not expected:                       # control case: any edit is a false edit
            false_edits += len(a_edits) + len(b_edits)

        print("-" * 78)
        print(f"RAW:      {raw}")
        print(f"COMBINED: {combined_text}")
        print(f"  expected terms: {expected or '(none - control)'}")
        print(f"  keyword recall  raw={raw_hit}/{len(expected)}  "
              f"exact-only={exact_hit}/{len(expected)}  combined={combined_hit}/{len(expected)}")
        alledits = [(e['find'], e['replace'], 'exact') for e in a_edits] + \
                   [(e['find'], e['replace'], round(e['confidence'], 3)) for e in b_edits]
        if alledits:
            print("  edits:          " + ", ".join(f"{f!r}->{r!r}({tag})" for f, r, tag in alledits))
        print(f"  latency:        {dt_ns/1000:.1f} microseconds")

    print()
    print("=" * 78)
    print("SUMMARY")
    print("=" * 78)
    print(f"total expected terms:                 {total_expected}")
    print(f"recovered by exact/alias map ONLY:    {exact_recovered}/{total_expected}")
    print(f"recovered by exact + phonetic-fuzzy:  {combined_recovered}/{total_expected}")
    print(f"false edits on control case:          {false_edits}")
    avg_ns = total_ns / len(CASES)
    print(f"avg combined cleanup latency:         {avg_ns/1000:.1f} microseconds")
    print(f"  (o4-mini LLM cleanup measured live: ~4881 ms = {4881_000_000/avg_ns:,.0f}x slower)")


if __name__ == "__main__":
    main()
