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

# TRANSFER - the banned behaviour stated plainly, with no bias jargon at all: an OBJECT (the
# dictionary) moving to a DESTINATION (the transcriber) via a VERB. "Include the dictionary in
# the speech-to-text request" is squarely the ruling and contains not one word the tiers above
# look for. All three parts must appear on the line, which is what keeps "sends the terms to the
# cleanup pass" out.
TRANSFER_VERB = re.compile(
    r"\b(includ(e|es|ing)|send(s|ing)?|sent|pass(es|ing|ed)?|giv(e|es|ing)|gave|given"
    r"|receiv(e|es|ing|ed)|attach(es|ing|ed)?|suppl(y|ies|ying|ied)|upload(s|ing|ed)?"
    r"|feed(s|ing)?|fed|provid(e|es|ing|ed)|add(s|ing|ed)?|inject(s|ing|ed)?)\b",
    re.I,
)
TRANSFER_OBJECT = re.compile(
    r"\b(dictionar\w*|glossar\w*|vocabular\w*|term list|word list|known terms|keyterms?"
    r"|spelling hints?|term set|our terms|the terms)\b",
    re.I,
)
NEGATION = re.compile(r"\b(never|nothing|not|n't|no|nor)\b", re.I)
# Where one clause ends and the next begins. A negation only reaches the verb in ITS OWN clause:
# in "Do not omit the dictionary; send it to the transcriber" the "not" governs "omit", and the
# instruction after the semicolon is the banned one, fully positive.
CLAUSE_BREAK = re.compile(r"[;:,]|\b(but|and|while|whereas|however|though|although|yet|then)\b",
                          re.I)


def negated_transfer(line):
    """True only when EVERY transfer verb on the line is itself negated.

    The guard used to be line-wide, which meant any "not" anywhere switched the whole detector
    off - so six different ways of writing the banned instruction with an unrelated or
    contrastive "not" sailed through. The negation has to govern the transfer PREDICATE, so it
    is looked for between the start of the verb's own clause and the verb itself. If even one
    transfer verb is positive, the line states the banned behaviour and is a claim.
    """
    verbs = list(TRANSFER_VERB.finditer(line))
    if not verbs:
        return False
    for verb in verbs:
        breaks = [b.end() for b in CLAUSE_BREAK.finditer(line) if b.end() <= verb.start()]
        clause = line[(max(breaks) if breaks else 0):verb.end()]
        if not NEGATION.search(clause):
            return False        # a positive transfer survives - this is a claim
    return True                 # every transfer on the line is explicitly negated
TRANSFER_DEST = re.compile(
    r"\b(speech.to.text|stt|transcriber|transcription request|\bprovider\b|audio upload"
    r"|prompt field|prompt parameter|\basr\b|whisper|speech model|speech engine"
    r"|transcription (call|api|endpoint|payload|body))\b",
    re.I,
)


def is_claim(line):
    """A claim is an unmistakable phrase, an ordinary verb in transcription context, or a
    plain statement that the dictionary is handed to the transcriber."""
    if STRONG.search(line):
        return True
    if WEAK.search(line) and STRICT_CONTEXT.search(line):
        return True
    # A line stating the CORRECT behaviour - "nothing in it is ever sent to the speech model" -
    # is the transfer shape negated, and must not be reported as the thing it forbids. The
    # exemption is scoped twice over: to this tier only (an outright "biased into speech-to-text"
    # still counts whatever else is on the line), and to the transfer PREDICATE (a "not"
    # governing some other verb does not excuse a positive transfer sitting beside it).
    if negated_transfer(line):
        return False
    return bool(TRANSFER_VERB.search(line)
                and TRANSFER_OBJECT.search(line)
                and TRANSFER_DEST.search(line))


# Seeds the file search. Wider than is_claim() on purpose: it decides which files to OPEN, and
# a file skipped here is never examined at all, so it errs toward including too many.
CLAIM = re.compile(
    "%s|%s|%s" % (STRONG.pattern, WEAK.pattern, TRANSFER_OBJECT.pattern), re.I)
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
    r"|fix a known custom vocabulary"
    # lazy construction of the dictation transcriber: "a dictation is sent" is the user
    # speaking, not the dictionary being handed over
    r"|built the first time a dictation is sent",
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


# Named lines the detector must and must not match, run by --self-test. Every "must match" here
# is a real line from this repository or a sentence a reviewer showed would sail through.
DETECTOR_CASES = [
    # --- transfer language: the ruling stated plainly, with no bias jargon at all ---
    ("include the dictionary in the speech-to-text request", True, "transfer: include/dictionary/stt"),
    ("the transcriber receives the glossary with every audio upload", True, "transfer: receives/glossary/transcriber"),
    ("give the provider the term list as spelling hints", True, "transfer: give/term list/provider"),
    ("pass known terms in the prompt field", True, "transfer: pass/known terms/prompt field"),
    # --- real lines that were found in this repository ---
    ("Terms that steer speech-to-text toward your vocabulary.", True, "v2 mockup subhead"),
    ("Terms biased into speech-to-text.", True, "shipped Cockpit and phone copy"),
    ("confirm the new correction/vocabulary bias is applied.", True, "the acceptance criterion"),
    ("Proper nouns the model is nudged toward. Safe - it only biases.", True, "the safety-argument line"),
    ("The dictionary biases a speech model toward distinctive terms", True, "the screening model prompt"),
    ("Bias transcription with the vocabulary glossary via the prompt parameter", True, "findings step 3"),
    # --- must NOT match: same words, nothing to do with steering a transcriber ---
    ("// Nudge with Enter. The nudge is safe while the prompt is", False, "terminal submit watchdog"),
    ('streamingBehavior: "steer"', False, "agent SDK option"),
    ("car mode end phrase set: length=", False, "log line"),
    ("norm = win * win / (win * win - 1.0)  # unbiased covariance", False, "statistics"),
    ("the cleanup pass receives the dictionary and fixes the transcript", False, "cleanup, the CORRECT path"),
    ("sends the terms to the cleanup pass", False, "cleanup destination, not the provider"),
    ("nothing in it is ever sent to the speech model", False, "the correct behaviour, stated"),
    ("the dictionary is never passed to the transcriber", False, "the correct behaviour, stated"),
    ("we do not send the glossary to the provider", False, "the correct behaviour, stated"),
    ("The dictionary is never sent to the provider, nor given to the ASR engine",
     False, "two transfers, both negated"),
    # --- the banned instruction wearing an unrelated or contrastive "not". A line-wide negation
    # --- guard missed every one of these; the guard is scoped to the transfer predicate now.
    ("Do not omit the dictionary; send it to the transcriber",
     True, "negation governs 'omit', the transfer is positive"),
    ("The transcriber receives the glossary, not the cleanup pass",
     True, "contrastive 'not' after a positive transfer"),
    ("Never leave the term list local; pass it to the ASR provider",
     True, "negation governs 'leave', the transfer is positive"),
    ("No restart is needed: the transcriber receives the glossary on each request",
     True, "negation belongs to an unrelated clause"),
    ("The provider must receive the dictionary, not just audio",
     True, "contrastive 'not just' after a positive transfer"),
    ("The dictionary is not only stored locally; it is sent to the provider",
     True, "'not only ... ; it is sent' - the transfer is positive"),
]


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


# The ORIGINAL claim text each file must yield once its annotations are removed. Asserting
# these exact strings is the point: an earlier self-test only asserted that SOME hit appeared,
# and passed on the leftover prose of the annotations it had half-stripped - a check passing on
# the wrong evidence, which is the very defect this sweep exists to find.
EXPECTED_CLAIMS = {
    "docs/reviews/dictation-dictionary-suggestions-mockup-v2-2026-07-24.html": [
        "Terms biased into speech-to-text.",
        "Terms that steer speech-to-text toward your vocabulary",
    ],
    "docs/reviews/dictation-dictionary-suggestions-mockup-2026-07-23.html": [
        "Terms that bias transcription",
        "Proper nouns the model is nudged toward",
    ],
    "docs/problems/voice-dictionary-not-applied-on-mobile.md": [
        "confirm the new correction/vocabulary bias is applied",
        "The speech-to-text bias prompt",
    ],
    "docs/research/transcription-speed/FINDINGS.md": [
        "with the vocabulary glossary via the `prompt` parameter",
        "Bias the transcriber",
    ],
}


def strip_annotation_blocks(text):
    """Remove WHOLE annotation blocks, not just the lines carrying the marker.

    Stripping only marker lines left the rest of each annotation's prose in place - and that
    prose is full of claim words, so the detector "caught" annotation remnants and the test
    passed without ever seeing the original claim. Blocks here are the shapes the annotations
    actually take: markdown blockquote runs, HTML comments, and the red correction paragraphs.
    """
    lines = text.split("\n")
    out, i = [], 0
    while i < len(lines):
        line = lines[i]
        block, end = None, i

        if line.lstrip().startswith(">"):                       # markdown blockquote run
            end = i
            while end < len(lines) and lines[end].lstrip().startswith(">"):
                end += 1
            block = lines[i:end]
        elif "<!--" in line:                                    # HTML comment, may span lines
            end = i
            while end < len(lines) and "-->" not in lines[end]:
                end += 1
            end = min(end + 1, len(lines))
            block = lines[i:end]
        elif re.search(r"<p[ >]", line):                        # HTML paragraph, may span lines
            end = i
            while end < len(lines) and "</p>" not in lines[end]:
                end += 1
            end = min(end + 1, len(lines))
            block = lines[i:end]

        if block is not None and any(CORRECTION.search(l) for l in block):
            i = end                                             # drop the whole annotation
            continue
        out.append(line)
        i += 1
    return "\n".join(out)


def self_test(root):
    """Prove the detector catches known-bad input. A check that cannot fail is not a check."""
    print("SELF-TEST - the detector must FAIL on known-bad input.\n")
    failures = 0

    print("  [1] named lines the detector must and must not match")
    for text, want, why in DETECTOR_CASES:
        got = bool(is_claim(text) and not UNRELATED.search(text))
        ok = got == want
        failures += 0 if ok else 1
        print("      %-5s want=%-5s got=%-5s  %s" % ("ok" if ok else "WRONG", want, got, why))

    print("\n  [2] real files with their annotation BLOCKS removed must yield the ORIGINAL claims")
    tmp = os.path.join(root, ".sweep-selftest.tmp")
    for rel, expected in sorted(EXPECTED_CLAIMS.items()):
        path = os.path.join(root, rel)
        if not os.path.exists(path):
            print("      SKIP %s (missing)" % rel)
            failures += 1
            continue
        clean = unannotated(path)
        with open(path, encoding="utf-8", errors="replace") as fh:
            broken_text = strip_annotation_blocks(fh.read())
        with open(tmp, "w", encoding="utf-8", newline="") as fh:
            fh.write(broken_text)
        try:
            hits = unannotated(tmp)
        finally:
            os.remove(tmp)
        found = [t for t in hits]
        missing = [e for e in expected
                   if not any(e.lower() in text.lower() for _, text in found)]
        ok = (not clean) and not missing
        failures += 0 if ok else 1
        print("      %-5s %s" % ("ok" if ok else "WRONG", rel))
        print("            as committed: %d unannotated | blocks removed: %d"
              % (len(clean), len(found)))
        for e in expected:
            hit = next((n for n, t in found if e.lower() in t.lower()), None)
            print("            %s expected claim at line %s: %s"
                  % ("found" if hit else "MISSING", hit if hit else "-", e[:60]))

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
