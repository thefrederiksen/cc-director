"""
Sweep the repository for claims that DevThrottle sends vocabulary or steering hints to the
speech-to-text provider - the approach the owner rejected in issue 2481.

WHY THIS EXISTS. Deleting the rejected code (DictionaryLoader.BuildSttPrompt) did not delete
the DESIGN: it stayed asserted in comments, user-visible strings, agent-facing help, a model
prompt, live architecture recommendations, dated records, and experiment RESULTS that argue
FOR it. A claim left standing is how a rejected design gets helpfully rebuilt later.

SIX METHOD FAILURES THIS SCRIPT EXISTS TO NOT REPEAT. Every one was found by a reviewer AFTER
an earlier version of this sweep reported "clean". They are listed because a checker's history
of being wrong is the most useful thing in its header.

1. SCOPE. The first version listed files from `git diff --name-only origin/main` - only files
   already touched. It could not find a file never opened, so it reported clean for exactly
   the files nobody had looked at. Seeds from `git grep` over the WHOLE tree now.

2. REACH. The second version treated an annotation within N lines as covering a claim. That
   let an annotation in one section vouch for a claim in the NEXT - which is how a standalone
   "Acceptance" criterion requiring "vocabulary bias" passed. Coverage is per SECTION now,
   heading to heading, because a reader enters at a heading rather than reading top to bottom.
   Acceptance criteria, checklists and tables are entry points more often than prose is.

3. SELF-CERTIFYING MARKER. The marker list accepted the bare word "correction". The defect
   read "confirm the new correction/vocabulary bias is applied" - so the CLAIM contained the
   marker, was read as its own annotation, and passed its whole section. A marker must be
   something only a deliberate annotation would write: the issue number.

4. FAILED OPEN - THE WORST ONE. Run by absolute path from outside a repository, `git grep`
   errored, the return code was ignored, and it printed "files searched: 0 / UNANNOTATED
   CLAIMS: 0" and exited 0. It certified the repository from any directory while searching
   NOTHING. Everything is anchored to the repository root now, every git call's return code is
   checked, and searching zero files is an ERROR, not a pass.

5. DETECTOR HOLE. CLAIM omitted the verb "steer", so "terms that steer speech-to-text toward
   your vocabulary" evaluated false - if someone deleted the annotations from the v2 mockup
   this sweep would not have noticed. The vocabulary is wider now and is tested (see
   --self-test).

6. RED ON ITS OWN HEAD. This file's comments read as headings and its regex prose reads as
   claims, so it flagged itself - and only "passed" because the issue number in this docstring
   masked its own hits. It is excluded by path, deliberately and visibly: the exclusion is
   printed on every run. It is the only file excluded for this reason.

Usage:
    python scripts/sweep-bias-claims.py              sweep the tree
    python scripts/sweep-bias-claims.py --self-test  prove the detector catches known-bad input

Exit codes:  0 = clean, 1 = unannotated claims found, 2 = the sweep could not run properly.
"""

import os
import re
import subprocess
import sys

# Two tiers, because one wide pattern cannot serve both jobs.
#
# STRONG - phrases that can only mean steering a speech-to-text engine. No further context
# needed; "nudged toward" and "biased into speech-to-text" are real lines from this repository.
STRONG = re.compile(
    r"prompt parameter|initial_prompt|word_boost|keyterms?\b|speech adaptation"
    r"|packed into the (openai|stt)|nudged toward|biased into speech",
    re.I,
)
# WEAK - ordinary verbs that mean steering ONLY next to transcription words. Bare "prompt" is
# deliberately NOT a context word: in this repository a prompt is usually the agent's terminal
# prompt, and including it made every submit-watchdog "nudge" a hit (15 false positives in one
# run). Bare "speech" is out for the same reason - Car Mode speaks.
# "phrase list/set" and "hotword" are provider steering features, but they collide with
# ordinary strings ("car mode end phrase set: length=..."), so they need context too.
WEAK = re.compile(
    r"bias(es|ed|ing)?\b|steer\w*|prim(e|es|ed|ing)\b|nudg(e|es|ed|ing)\b"
    r"|boost(s|ed|ing)?\b|custom vocab\w*|phrase (list|set)s?\b|hot ?words?\b",
    re.I,
)
STRICT_CONTEXT = re.compile(
    r"transcri|speech.to.text|speech model|\bstt\b|\basr\b|whisper"
    r"|vocabular|glossar|dictionar|dictation",
    re.I,
)


def is_claim(line):
    """A claim is an unmistakable phrase, or an ordinary verb in transcription context."""
    return bool(STRONG.search(line) or (WEAK.search(line) and STRICT_CONTEXT.search(line)))


# Kept so --self-test and callers can seed a file search from one pattern.
CLAIM = re.compile("%s|%s" % (STRONG.pattern, WEAK.pattern), re.I)
# An annotation that discharges a claim. NARROW ON PURPOSE - see failure 3. Every note written
# for this ruling names the issue; nothing else in ordinary prose does.
CORRECTION = re.compile(r"\b2481\b|RULED OUT|TOMBSTONE", re.I)
# Uses of these words that have nothing to do with steering a transcriber. EVERY entry was read
# in place first - this list is where a false positive goes to be retired with a reason, never a
# way to quieten a hit that has not been understood.
UNRELATED = re.compile(
    # statistical / visual senses of "bias"
    r"unbiased covariance|amber-biased|tail-biased|The bias is deliberate|bias the share"
    r"|bias this prevents|would bias exactly"
    # "steer"/"nudge" about terminal prompts and browser tabs, not audio
    r"|steers callers|steer it to the target"
    # priming a shared AudioContext for Car Mode cues, not priming a model
    r"|Dictation does not prime"
    # nudging a WAV cut point to a quiet spot so a word is not sliced
    r"|bounded parts, nudging"
    # naming the cleanup task the deterministic matcher replaces - correction, not biasing
    r"|fix a known custom vocabulary",
    re.I,
)
# A heading is a reader's entry point: markdown headings, HTML h1-h4, and setext rules.
HEADING = re.compile(r"^\s{0,3}#{1,6}\s|<h[1-4][\s>]|^\s*={3,}\s*$|^\s*-{3,}\s*$", re.I)

SKIP_EXT = (".png", ".jpg", ".jpeg", ".gif", ".mp3", ".m4a", ".trx", ".pdf", ".ico",
            ".svg", ".woff", ".woff2", ".zip", ".dll", ".exe", ".pyc")

SELF = "scripts/sweep-bias-claims.py"

# Stated exclusions, each with a reason, PRINTED ON EVERY RUN. An exclusion nobody sees is an
# exclusion nobody can challenge.
EXCLUDE = {
    SELF:
        "this sweep itself - its regex prose reads as claims and its comments as headings "
        "(failure 6); excluded by path rather than by an accident of its own text",
    "docs/features/dictation/phase0/run_phase0.py":
        "tombstoned - unreachable behind a hard exit at the top, its header carries the ruling",
}
# Test fixtures holding this work's own session NAME ("fix: 2481 delete bias path"), not a
# claim about behaviour.
EXCLUDE_DIRS = ("apps/cockpit/src/missions/",)


class SweepError(RuntimeError):
    """The sweep could not run properly. Never reported as a clean result."""


def repo_root():
    """The repository this script lives in - NOT the caller's working directory (failure 4)."""
    here = os.path.dirname(os.path.abspath(__file__))
    done = subprocess.run(["git", "-C", here, "rev-parse", "--show-toplevel"],
                          capture_output=True, text=True)
    if done.returncode != 0:
        raise SweepError(
            "cannot find the repository root from %s: %s"
            % (here, done.stderr.strip() or "git rev-parse failed"))
    root = done.stdout.strip()
    if not root or not os.path.isdir(root):
        raise SweepError("git reported a repository root that is not a directory: %r" % root)
    return root


def candidate_files(root):
    """Files anywhere in the tree that mention any claim word. Errors are raised, not ignored."""
    done = subprocess.run(["git", "-C", root, "grep", "-l", "-i", "-E", CLAIM.pattern],
                          capture_output=True, text=True)
    # git grep: 0 = matches found, 1 = no matches, anything else = a real failure.
    if done.returncode > 1:
        raise SweepError("git grep failed (exit %d): %s"
                         % (done.returncode, done.stderr.strip() or "no message"))
    return sorted({f.strip() for f in done.stdout.split("\n") if f.strip()})


def sections(lines):
    """(start, end) line indices per heading-delimited section - the units a reader enters at."""
    starts = [i for i, l in enumerate(lines) if HEADING.search(l)]
    if not starts or starts[0] != 0:
        starts = [0] + starts
    return [(s, starts[n + 1] if n + 1 < len(starts) else len(lines))
            for n, s in enumerate(starts)]


def unannotated(path):
    """Claims in `path` with no annotation in the section a reader would enter at."""
    with open(path, encoding="utf-8", errors="replace") as fh:
        lines = fh.read().split("\n")
    out = []
    for start, end in sections(lines):
        block = lines[start:end]
        if any(CORRECTION.search(l) for l in block):
            continue
        for i, line in enumerate(block):
            if not is_claim(line):
                continue
            if CORRECTION.search(line) or UNRELATED.search(line):
                continue
            out.append((start + i + 1, line.strip()[:100]))
    return out


def sweep(root):
    files = candidate_files(root)
    if not files:
        # This repository contains this very script, which is full of claim words, so a match
        # count of zero means the search did not happen - not that the tree is clean.
        raise SweepError(
            "searched 0 files. The claim pattern matches nothing in %s, which cannot be true "
            "for this repository - the search did not run. Refusing to report clean." % root)

    total, examined = 0, 0
    for rel in files:
        if rel in EXCLUDE or rel.startswith(EXCLUDE_DIRS) or rel.endswith(SKIP_EXT):
            continue
        path = os.path.join(root, rel)
        if not os.path.exists(path):
            continue
        examined += 1
        hits = unannotated(path)
        if hits:
            total += len(hits)
            print("####", rel)
            for n, text in hits:
                print("    %d: %s" % (n, text))

    print("\nrepository:     %s" % root)
    print("files matched:  %d   examined: %d" % (len(files), examined))
    for rel, why in sorted(EXCLUDE.items()):
        print("excluded:       %s (%s)" % (rel, why))
    print("UNANNOTATED CLAIMS: %d" % total)
    return 1 if total else 0


def self_test(root):
    """Prove the detector catches known-bad input. A check that cannot fail is not a check.

    Takes a real annotated file, strips its annotations, and asserts the claims reappear.
    """
    print("SELF-TEST - the detector must FAIL on known-bad input.\n")
    targets = [
        "docs/reviews/dictation-dictionary-suggestions-mockup-v2-2026-07-24.html",
        "docs/problems/voice-dictionary-not-applied-on-mobile.md",
        "docs/research/transcription-speed/FINDINGS.md",
    ]
    tmp = os.path.join(root, ".sweep-selftest.tmp")
    failures = 0
    for rel in targets:
        path = os.path.join(root, rel)
        if not os.path.exists(path):
            print("  SKIP %s (missing)" % rel)
            continue
        clean = unannotated(path)
        with open(path, encoding="utf-8", errors="replace") as fh:
            stripped = [l for l in fh.read().split("\n") if not CORRECTION.search(l)]
        with open(tmp, "w", encoding="utf-8", newline="") as fh:
            fh.write("\n".join(stripped))
        try:
            broken = unannotated(tmp)
        finally:
            os.remove(tmp)
        ok = (not clean) and bool(broken)
        failures += 0 if ok else 1
        print("  %s %s" % ("PASS" if ok else "FAIL", rel))
        print("      as committed: %d unannotated | annotations removed: %d unannotated"
              % (len(clean), len(broken)))
        if broken:
            print("      example caught: line %d: %s" % (broken[0][0], broken[0][1][:70]))
    print("\nSELF-TEST: %s" % ("PASS" if not failures else "FAIL (%d)" % failures))
    return 0 if not failures else 2


def main(argv):
    try:
        root = repo_root()
        if "--self-test" in argv:
            return self_test(root)
        return sweep(root)
    except SweepError as exc:
        print("SWEEP ERROR: %s" % exc, file=sys.stderr)
        print("This is NOT a clean result.", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
