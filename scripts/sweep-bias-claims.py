"""
Sweep the repository for claims that DevThrottle sends vocabulary or steering hints to the
speech-to-text provider - the approach the owner rejected in issue 2481.

WHY THIS EXISTS. Deleting the rejected code (DictionaryLoader.BuildSttPrompt) did not delete
the DESIGN: it stayed asserted in comments, user-visible strings, agent-facing help, a model
prompt, live architecture recommendations, dated records, and experiment RESULTS that argue
FOR it. A claim left standing is how a rejected design gets helpfully rebuilt later.

TWO METHOD FAILURES THIS SCRIPT EXISTS TO NOT REPEAT. Both were found by reviewers after an
earlier version of this sweep reported "clean":

1. SCOPE. The first version listed files from `git diff --name-only origin/main` - only files
   already touched. It could not find a file never opened, so it reported clean for exactly
   the files nobody had looked at. It now seeds from `git grep` over the WHOLE tree.

2. REACH. The second version treated a correction within N lines as covering a claim. That
   let a correction in one section vouch for a claim in the NEXT section - which is how a
   standalone "Acceptance" criterion requiring "vocabulary bias" passed. A reader does not
   arrive at the top of a file and read down; they enter at a HEADING. So coverage now means
   SAME SECTION, where a section runs from one heading to the next. Acceptance criteria,
   checklists and tables are entry points more often than prose is.

Usage:  python scripts/sweep-bias-claims.py
Exit 0 if nothing is unannotated, 1 otherwise (so it can gate a build if wanted).
"""

import os
import re
import subprocess
import sys

# A claim that the transcriber is being steered.
CLAIM = re.compile(
    r"bias(es|ed|ing)?\b|prompt parameter|initial_prompt|word_boost|keyterms?\b"
    r"|nudged toward|packed into the (openai|stt)",
    re.I,
)
# ...but only when it is about transcription, not statistics or CSS.
CONTEXT = re.compile(r"transcri|speech|stt|vocab|glossar|dictionar|asr|whisper|nudge|prompt", re.I)
# A correction/annotation that discharges a claim.
#
# THIS MUST BE NARROW, AND HERE IS WHY. An earlier version accepted the bare word
# "correction", any of "never sent", "REJECTED", and so on. That let a CLAIM certify ITSELF:
# the acceptance criterion "confirm the new correction/vocabulary bias is applied" contains
# the word "correction", so the sweep read the defect as its own annotation and passed the
# whole section. A marker has to be something only a deliberate annotation would say, so it
# is the issue number - every note written for this ruling names it - plus the two headers
# that stand in for one.
CORRECTION = re.compile(r"\b2481\b|RULED OUT|TOMBSTONE", re.I)
# Uses of the word "bias" that have nothing to do with transcription. Verified individually.
UNRELATED = re.compile(
    r"unbiased covariance|amber-biased|tail-biased|The bias is deliberate|bias the share"
    r"|bias this prevents|would bias exactly",
    re.I,
)
# A heading is a reader's entry point: markdown headings, HTML h1-h4, and the ==== / ---- rules.
HEADING = re.compile(r"^\s{0,3}#{1,6}\s|<h[1-4][\s>]|^\s*={3,}\s*$|^\s*-{3,}\s*$", re.I)

SKIP_EXT = (".png", ".jpg", ".jpeg", ".gif", ".mp3", ".m4a", ".trx", ".pdf", ".ico",
            ".svg", ".woff", ".woff2", ".zip", ".dll", ".exe", ".pyc")

# Stated exclusions, each with a reason. Anything here is a decision, not an oversight.
EXCLUDE = {
    # Tombstoned: hard sys.exit(2) before any import, so the body is unreachable. Its header
    # carries the ruling; the text below the exit is the retained record of the experiment.
    "docs/features/dictation/phase0/run_phase0.py":
        "tombstoned - unreachable behind a hard exit, header carries the ruling",
}
# Test fixtures that contain this work's own session NAME ("fix: 2481 delete bias path"),
# which is not a claim about behaviour.
EXCLUDE_DIRS = ("apps/cockpit/src/missions/",)


def sections(lines):
    """Yield (start, end) line indices for each heading-delimited section.

    A reader enters at a heading, so a correction only reaches claims under the SAME heading.
    """
    starts = [i for i, l in enumerate(lines) if HEADING.search(l)]
    if not starts or starts[0] != 0:
        starts = [0] + starts
    bounds = []
    for n, s in enumerate(starts):
        e = starts[n + 1] if n + 1 < len(starts) else len(lines)
        bounds.append((s, e))
    return bounds


def unannotated(path):
    """Claims in `path` with no correction in the section a reader would enter at."""
    with open(path, encoding="utf-8", errors="replace") as fh:
        lines = fh.read().split("\n")
    out = []
    for start, end in sections(lines):
        block = lines[start:end]
        # Coverage is per SECTION, not per line-distance. A reader enters at the heading and
        # reads that section, so a correction anywhere within it is seen - before the claim or
        # after it. What must NOT count is a correction in a neighbouring section: that is the
        # leak that let an "Acceptance" criterion requiring "vocabulary bias" pass.
        if any(CORRECTION.search(l) for l in block):
            continue
        for i, line in enumerate(block):
            if not (CLAIM.search(line) and CONTEXT.search(line)):
                continue
            if CORRECTION.search(line) or UNRELATED.search(line):
                continue
            out.append((start + i + 1, line.strip()[:100]))
    return out


def main():
    listed = subprocess.run(
        ["git", "grep", "-l", "-i", "-E", CLAIM.pattern],
        capture_output=True, text=True,
    ).stdout
    files = sorted({f.strip() for f in listed.split("\n") if f.strip()})

    total, skipped = 0, []
    for f in files:
        if f in EXCLUDE:
            skipped.append((f, EXCLUDE[f]))
            continue
        if f.startswith(EXCLUDE_DIRS) or f.endswith(SKIP_EXT) or not os.path.exists(f):
            continue
        hits = unannotated(f)
        if hits:
            total += len(hits)
            print("####", f)
            for n, t in hits:
                print("    %d: %s" % (n, t))

    print("\nfiles searched: %d" % len(files))
    for f, why in skipped:
        print("excluded: %s (%s)" % (f, why))
    print("UNANNOTATED CLAIMS: %d" % total)
    return 1 if total else 0


if __name__ == "__main__":
    sys.exit(main())
