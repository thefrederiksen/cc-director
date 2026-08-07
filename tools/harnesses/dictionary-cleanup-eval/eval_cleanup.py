"""
Multilingual evaluation harness for the deterministic dictionary-cleanup step.

Design (from established ASR-customization / contextual-biasing methodology): hold transcription
CONSTANT and score ONLY the cleanup. What is borrowed from that literature is the MEASUREMENT - the
B-WER / U-WER split below - and NOT the technique: DevThrottle does no contextual biasing and sends
nothing but audio to the transcriber (issue 2481). "Biased tokens" below is the standard B-WER name
for the target-term tokens being scored; nothing is being biased.

Each fixture is text-in / text-out - a frozen raw transcript, a
term list (targets + distractors), the gold corrected text, and the gold edits. The harness POSTs each
fixture to the Gateway's /transcription/cleanup endpoint (the REAL production cleanup) and scores the
result. No audio, no third-party ASR variance.

Metrics (the important ones):
  - B-WER  : error rate over TARGET-TERM tokens only ("is the dictionary working"). Lower is better.
  - U-WER  : error rate over all OTHER tokens ("collateral damage"). MUST NOT rise = over-correction gate.
  - edit precision / recall : of the corrections made vs. the gold corrections.
  - false edits : corrections the system made that were NOT gold (the do-no-harm signal; on no-op
                  fixtures every edit is a false edit). This is the primary release gate.
  - CER variants for non-space-delimited languages (ja/zh/th), selected from the language tag.
Scored per language, then MACRO-averaged (never let one language dominate).

Pure standard library (urllib) so it runs with no dependencies, like the sibling harnesses. jiwer +
whisper-normalizer are the trusted cross-check if you want to add them later.

Usage:
  python eval_cleanup.py [--url http://127.0.0.1:7878] [--fixtures <dir>] [--json out.jsonl]
"""

import argparse
import glob
import json
import os
import re
import sys
import urllib.request

DEFAULT_URL = "http://127.0.0.1:7878"


def is_char_unit(lang: str) -> bool:
    """Non-space-delimited languages score by CHARACTER (CER), not word (WER)."""
    return any(lang.startswith(p) for p in ("ja", "zh", "th"))


def normalize(text: str) -> str:
    text = text.lower()
    text = re.sub(r"[^\w\s]", " ", text, flags=re.UNICODE)  # drop punctuation, keep letters/digits/CJK
    return re.sub(r"\s+", " ", text).strip()


def tokenize(text: str, lang: str):
    norm = normalize(text)
    if is_char_unit(lang):
        return [c for c in norm if not c.isspace()]
    return norm.split()


def align(ref, hyp):
    """Levenshtein alignment -> list of ('equal'|'sub'|'del'|'ins', ref_tok, hyp_tok)."""
    n, m = len(ref), len(hyp)
    dp = [[0] * (m + 1) for _ in range(n + 1)]
    for i in range(n + 1):
        dp[i][0] = i
    for j in range(m + 1):
        dp[0][j] = j
    for i in range(1, n + 1):
        for j in range(1, m + 1):
            cost = 0 if ref[i - 1] == hyp[j - 1] else 1
            dp[i][j] = min(dp[i - 1][j] + 1, dp[i][j - 1] + 1, dp[i - 1][j - 1] + cost)
    ops = []
    i, j = n, m
    while i > 0 or j > 0:
        if i > 0 and j > 0 and dp[i][j] == dp[i - 1][j - 1] + (0 if ref[i - 1] == hyp[j - 1] else 1):
            ops.append(("equal" if ref[i - 1] == hyp[j - 1] else "sub", ref[i - 1], hyp[j - 1]))
            i, j = i - 1, j - 1
        elif i > 0 and dp[i][j] == dp[i - 1][j] + 1:
            ops.append(("del", ref[i - 1], None))
            i -= 1
        else:
            ops.append(("ins", None, hyp[j - 1]))
            j -= 1
    ops.reverse()
    return ops


def biased_surface_tokens(target_terms, lang):
    """The set of normalized tokens that belong to any target term (the 'biased' vocabulary).

    "Biased" is the standard B-WER term for the target-term tokens being scored. Nothing is
    biased: DevThrottle sends nothing but audio to the transcriber (issue 2481).
    """
    s = set()
    for term in target_terms:
        for tok in tokenize(term, lang):
            s.add(tok)
    return s


def score_fixture(fx, cleaned, changes):
    lang = fx["language"]
    ref = tokenize(fx["reference_corrected"], lang)
    hyp = tokenize(cleaned, lang)
    biased = biased_surface_tokens(fx.get("target_terms", []), lang)

    ops = align(ref, hyp)
    b_err = u_err = 0
    for kind, r_tok, h_tok in ops:
        if kind == "equal":
            continue
        tok = r_tok if kind in ("sub", "del") else h_tok
        if tok in biased:
            b_err += 1
        else:
            u_err += 1
    biased_ref = sum(1 for t in ref if t in biased)
    unbiased_ref = len(ref) - biased_ref

    # edit precision/recall from the edits the endpoint actually applied vs. the gold edits
    sys_edits = {(normalize(c["find"]), c["replace"]) for c in changes}
    gold_edits = {(normalize(g["from"]), g["to"]) for g in fx.get("gold_edits", [])}
    matched = len(sys_edits & gold_edits)
    false_edits = len(sys_edits - gold_edits)

    return {
        "id": fx["id"],
        "language": lang,
        "unit": "cer" if is_char_unit(lang) else "wer",
        "ref_tokens": len(ref),
        "errors": b_err + u_err,
        "b_err": b_err,
        "u_err": u_err,
        "biased_ref": biased_ref,
        "unbiased_ref": unbiased_ref,
        "sys_edits": len(sys_edits),
        "gold_edits": len(gold_edits),
        "matched_edits": matched,
        "false_edits": false_edits,
        "cleaned": cleaned,
    }


def cleanup_via_gateway(url, token, text, terms, language):
    body = json.dumps({"text": text, "terms": terms, "language": language}).encode("utf-8")
    req = urllib.request.Request(url.rstrip("/") + "/transcription/cleanup", data=body, method="POST")
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", "Bearer " + token)
    with urllib.request.urlopen(req, timeout=30) as resp:
        data = json.loads(resp.read().decode("utf-8"))
    return data.get("cleaned", text), data.get("changes", [])


def read_gateway_token():
    path = os.path.join(os.environ.get("LOCALAPPDATA", ""), "cc-director", "config", "config.json")
    try:
        with open(path, encoding="utf-8-sig") as f:
            return (json.load(f).get("gateway") or {}).get("token") or ""
    except (OSError, json.JSONDecodeError):
        return ""


def pct(n, d):
    return 100.0 * n / d if d else 0.0


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default=DEFAULT_URL)
    ap.add_argument("--fixtures", default=os.path.join(here, "fixtures"))
    ap.add_argument("--json", default=os.path.join(here, "results.jsonl"))
    args = ap.parse_args()

    token = read_gateway_token()
    fixtures = []
    for fp in sorted(glob.glob(os.path.join(args.fixtures, "*.json"))):
        with open(fp, encoding="utf-8") as f:
            fixtures.extend(json.load(f))
    if not fixtures:
        print("no fixtures found in", args.fixtures)
        return 1

    results = []
    for fx in fixtures:
        try:
            cleaned, changes = cleanup_via_gateway(args.url, token, fx["raw_transcript"], fx["term_list"], fx["language"])
        except Exception as ex:  # noqa: BLE001 - a harness must report, not crash
            print(f"[{fx['id']}] cleanup call FAILED: {ex}")
            return 2
        results.append(score_fixture(fx, cleaned, changes))

    with open(args.json, "w", encoding="utf-8") as f:
        for r in results:
            f.write(json.dumps(r, ensure_ascii=False) + "\n")

    # ---- per-language aggregation ----
    langs = sorted({r["language"] for r in results})
    print("=" * 92)
    print("DICTIONARY-CLEANUP EVAL  (text-in/text-out, cleanup held to production; transcription constant)")
    print("=" * 92)
    print(f"{'lang':<6}{'unit':<5}{'n':>3}  {'WER/CER':>8}  {'B-WER':>7}  {'U-WER':>7}  "
          f"{'editPrec':>9}  {'editRec':>8}  {'falseEdits':>11}")
    macro = {"wer": [], "bwer": [], "uwer": [], "prec": [], "rec": []}
    total_false = 0
    for lang in langs:
        rs = [r for r in results if r["language"] == lang]
        errs = sum(r["errors"] for r in rs)
        reftok = sum(r["ref_tokens"] for r in rs)
        berr = sum(r["b_err"] for r in rs)
        bref = sum(r["biased_ref"] for r in rs)
        uerr = sum(r["u_err"] for r in rs)
        uref = sum(r["unbiased_ref"] for r in rs)
        sys_e = sum(r["sys_edits"] for r in rs)
        gold_e = sum(r["gold_edits"] for r in rs)
        matched = sum(r["matched_edits"] for r in rs)
        false_e = sum(r["false_edits"] for r in rs)
        total_false += false_e
        wer = pct(errs, reftok)
        bwer = pct(berr, bref) if bref else None
        uwer = pct(uerr, uref)
        prec = pct(matched, sys_e) if sys_e else 100.0
        rec = pct(matched, gold_e) if gold_e else 100.0
        unit = rs[0]["unit"]
        macro["wer"].append(wer)
        if bwer is not None:
            macro["bwer"].append(bwer)
        macro["uwer"].append(uwer)
        macro["prec"].append(prec)
        macro["rec"].append(rec)
        bwer_s = f"{bwer:6.1f}%" if bwer is not None else "    n/a"
        print(f"{lang:<6}{unit:<5}{len(rs):>3}  {wer:7.1f}%  {bwer_s:>7}  {uwer:6.1f}%  "
              f"{prec:8.1f}%  {rec:7.1f}%  {false_e:>11}")

    def avg(xs):
        return sum(xs) / len(xs) if xs else 0.0

    print("-" * 92)
    print(f"{'MACRO':<6}{'':<5}{len(results):>3}  {avg(macro['wer']):7.1f}%  {avg(macro['bwer']):6.1f}%  "
          f"{avg(macro['uwer']):6.1f}%  {avg(macro['prec']):8.1f}%  {avg(macro['rec']):7.1f}%  {total_false:>11}")
    print()
    print(f"Release gate: total false edits (do-no-harm) = {total_false}   "
          f"[{'PASS' if total_false == 0 else 'REVIEW'}]")
    print(f"Wrote per-fixture results to {args.json}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
