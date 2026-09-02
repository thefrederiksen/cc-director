#!/usr/bin/env python
"""Wrap a cc-scrub proof transcript in its header and write PROOF-windows.md.

The transcript is machine produced by proof-run-windows.sh. This header is
hand written, which is the whole reason this script exists: a figure the
header states is a number that can drift away from the tool, and it has
drifted twice - once on 14.3 and once on 15.0 - both times in narrative and
never in tool output.

So before writing anything, every number-like token the header quotes is
checked against the transcript, and the build FAILS naming the offender if
one is missing. Fix the header or fix the driver. Never hand-edit the
transcript: it is evidence, and an edited transcript is not.

Usage:
    python proof-build-windows.py <transcript.txt> [output.md]

Output defaults to PROOF-windows.md beside this script.

This script is not part of the tool. Nothing imports it, nothing runs it
during the test suite, and no build references it.
"""

import os
import re
import sys


HEADER = """\
# cc-scrub - clean install proof, Windows

This is the real transcript of a real run, not a summary of one. It was
produced by creating an empty virtual environment on a Windows machine,
installing Pillow, winocr and pytest from pip and nothing else, drawing the
synthetic samples, and then driving cc-scrub over them. Every command below
is echoed before its output and every exit code is printed after it.

It was run from `D:/cc-scrub-proof` rather than from a checkout, so nothing
in the transcript names a person, a machine or a private repository. The
samples are drawn by `gen_samples.py` and the denylist is the shipped
`terms.example.txt` copied unchanged - the three planted terms are
`example@example.com`, `myorg/secret-repo` and `internal-hostname.local`,
all deliberately fake.

## What this proves

- **A clean install works from pip alone.** No installer, no external
  executable, no tesseract, no subprocess. Pillow 12.3.0, winocr 0.0.15 and
  their winrt wheels, on Python 3.11.6, Windows 10.0.26200.9278.
- **check-only finds each planted term and prints its coordinates**, and
  exits 1 while leaving the image untouched (step 6).
- **Multi-scale reading is load bearing.** The 10 px grey line in
  `sample-small-ui.png` is reported as `scales=2,3`: the recognizer cannot
  resolve it at native resolution and only reads it once the image is
  enlarged. Read at scale 1 alone, that term is missed.
- **Glyph folding is load bearing.** In `sample-glyph.png` the recognizer
  returns `https:/ftnternal-hostname.local/...` - the leading `i` of
  `internal` comes back as a `t`. With folding on the term is found (step
  6); with `--no-fold` the same image reports `HITS: 0` and exits 0 (step
  7). That pair is the whole argument for folding, and it is why folding is
  on by default.
- **An image with no text is a broken read, not a clean image.**
  `sample-notext.png` reads zero words at every scale and the tool exits 2
  refusing to call it scrubbed (step 8).
- **Nothing unverified is ever published.** Each scrub writes a `.tmp`
  CANDIDATE beside the destination, verifies THAT file, and only then renames
  it onto the `*-scrubbed.png` name (step 9). After three scrubs the output
  directory holds exactly three published files and no candidates (step 10).
- **The verify pass is presence shaped.** Each scrub prints
  `VERIFY PASSED: 1 hit(s) found, 1 region(s) redacted, verify OCR read N
  words in the output and 0 denylist hit(s) remain` - and N is checked as a
  number, so a verify instrument that reads nothing cannot certify anything.
- **An existing output is not replaced by accident.** Re-running the same
  scrub exits 2 and names the file; `--force` is how you say you meant it
  (steps 11 and 12).
- **The input is never a legal destination, in any spelling.** Addressing the
  same file as `shot.png` and `SHOT.PNG` is refused - and refused even with
  `--force`, because that would destroy the only unredacted copy (step 13).
- **Two inputs cannot land on one output.** `Shot.png` and `shot.jpg` both
  resolve to one file on this volume and the run stops before any image is
  read (step 14).
- **A read over the megapixel budget is refused before it is attempted.**
  900x260 at scale 8 is 7200x2080, and the message names the exact count:
  14,976,000 pixels (14.976 megapixels, decimal, as the flag name says) over
  a 1 megapixel budget, with the engine never reached (step 15). The count
  leads and the megapixel figure carries three decimals so that a value just
  over the cap can never print AS the cap.
- **`--patch` is exercised, not merely mentioned.** The mode recommended for
  anything leaving the organisation is run end to end and its output
  re-checked (step 16). An earlier version of this transcript named `--patch`
  in prose and never ran a single `--patch` command, which certified a mode
  it had not exercised.
- **The published outputs read clean on a second, independent pass.** Each
  file is re-opened from disk after the run that wrote it and read again with
  zero hits (step 17). That is a statement about this engine at these scales,
  not about the pixels: see "What this does NOT prove".
- **The whole test suite passes in that same clean environment**: 68 tests,
  NONE SKIPPED, including the three end-to-end scrub-and-verify cases
  (step 19). It is run with `-rs`, so a skip would be printed with its
  reason - a skip nobody prints reads exactly like a pass. The arms that
  need a volume on which two spellings name one file measure that on the
  directory they write to; on this volume all of them run.
- **An unreadable path stops the run rather than granting permission.** Six
  tests inject a permission error into os.stat or os.remove at a guard - the
  input-overwrite check, the existing-output refusal, the case probe's
  readback, the case probe's cleanup, the denylist lookup, and the candidate
  cleanup. Injection is the only way in: these arms cannot be reached on a
  working filesystem. Five of the six FAILED against the code before this
  round, which is what makes them tests rather than comments; the sixth, the
  candidate cleanup, passed already and is pinned so it stays that way. A
  seventh pins the one error that IS an answer - a genuinely missing name
  still means absent.

## What this does NOT prove

- **That no trace of the redacted text survives in the pixels.** The verify
  pass is run by the same recognizer that found the text, at the scales this
  run configured, so a pass proves the term is no longer readable BY THAT
  ENGINE AT THOSE SCALES. Every scrub here used the default `--blur`, which
  removes the original pixel values but leaves a low-frequency average of the
  covered region - that is attackable, and nothing in this transcript says
  otherwise. `--patch` is the mode that leaves no signal from the covered
  pixels at all. See "How the redaction works" in the README.
- **Nothing about macOS.** This is a Windows run. `MacVisionBackend` is a
  different engine with its own clean-install transcript in
  [PROOF-macos.md](PROOF-macos.md); nothing here carries over to it.
  In particular, the output-collision check asks the destination directory
  whether it treats two spellings as one file, and a Windows volume always
  answers yes - so this transcript exercises the check but cannot demonstrate
  the case it was written for, which is a case-insensitive volume under a
  POSIX host. The regression test for that half neutralises
  `os.path.normcase` so it runs anywhere, and it is in step 19; a real
  case-insensitive POSIX volume is verified on the macOS side.
- **Nothing about other people's Windows builds.** The recognizer is the
  operating system's own and its output is not identical across Windows
  versions or installed language packs. The word counts and the exact
  misreadings below are what this machine produced. Re-run the proof rather
  than trusting the numbers.
- **Nothing about real screenshots.** These are synthetic images drawn by
  Pillow. They are built to exercise the small-text and glyph-confusion
  cases on purpose, which is not the same thing as a survey of real product
  chrome.
- **Nothing about a packaged executable.** cc-scrub is run from source here.
  It has no PyInstaller build and is not in the shipped tool bundle.
- **The two verify FAILURE arms are not in this transcript.** No real
  screenshot makes an engine read zero words from a file it has just read
  words from, and none reliably leaves a redacted term readable. Those arms
  are proved in the test suite instead, driven by a scripted backend, and
  they are the tests named `test_a_verify_read_of_zero_words_...` and
  `test_a_term_surviving_in_the_output_...` in step 19.

## The transcript

```
"""

FOOTER = "```" + chr(10)

# Small integers are step numbers, bullet counts and the like, not figures
# quoted from the tool, so they are not required to appear in the transcript.
STRUCTURAL = set(str(n) for n in range(0, 40))


def check_header_against(transcript):
    quoted = set(re.findall(r"\b\d[\d,]*(?:\.\d+)?\b", HEADER))
    missing = sorted(n for n in quoted
                     if n not in STRUCTURAL and n not in transcript)
    if missing:
        raise SystemExit(
            "PROOF HEADER DRIFT: the header quotes figures the transcript "
            "does not contain: %s. Fix the header or the driver - never "
            "hand-edit the transcript." % ", ".join(missing))


def main(argv):
    if not argv:
        raise SystemExit(__doc__)
    transcript_path = argv[0]
    here = os.path.dirname(os.path.abspath(__file__))
    out_path = argv[1] if len(argv) > 1 else os.path.join(
        here, "PROOF-windows.md")

    with open(transcript_path, "r", encoding="ascii") as handle:
        transcript = handle.read()

    check_header_against(transcript)

    # No newline= argument: the file is written with this platform's line
    # endings, which is how the committed transcript was produced. Forcing
    # them either way here would make the rebuilt file differ from the
    # committed one on a byte comparison for a reason nobody cares about.
    with open(out_path, "w", encoding="ascii") as handle:
        handle.write(HEADER + transcript.rstrip(chr(10)) + chr(10) + FOOTER)
    print("wrote %s (header figures checked against the transcript)" % out_path)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
