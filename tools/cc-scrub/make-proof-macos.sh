#!/bin/bash
# The driver that produced tools/cc-scrub/PROOF-macos.md - the macOS half
# ONLY. PROOF-windows.md is produced by its own driver on a Windows
# machine; one script does not make both proofs.
#
# Nothing in the tool, its tests or its build references this file. It is
# committed because a transcript only one machine can regenerate is weaker
# than it looks: this is the exact sequence of commands whose recorded
# output is the fenced transcript in PROOF-macos.md, so anyone with a Mac
# can reproduce the run and compare.
#
# Usage, on a macOS machine with python3 on the path:
#
#   BASE=/private/tmp/cc-scrub-proof     # a neutral path: the transcript
#   rm -rf "$BASE" && mkdir -p "$BASE"   # must name no person or machine
#   git archive HEAD tools/cc-scrub | tar -x -C "$BASE" --strip-components=1
#   bash tools/cc-scrub/make-proof-macos.sh run
#
# The transcript lands at $BASE/transcript.txt. The narrative half of
# PROOF-macos.md is written by hand against that transcript; the fenced
# transcript section is then replaced mechanically, never edited:
#
#   bash tools/cc-scrub/make-proof-macos.sh splice tools/cc-scrub/PROOF-macos.md
#
# Everything here is ASCII only, and every command is echoed before its
# output with its exit code printed after it - the transcript records
# failures as faithfully as passes.
set -u

BASE=/private/tmp/cc-scrub-proof
T="$BASE/transcript.txt"
MODE="${1:-run}"

if [ "$MODE" = "splice" ]; then
  if [ $# -ne 2 ]; then
    echo "usage: make-proof-macos.sh splice <path-to-PROOF-macos.md>" >&2
    exit 2
  fi
  PROOF="$2" TRANSCRIPT="$T" python3 - <<'PYEOF'
import os
import sys

transcript_path = os.environ["TRANSCRIPT"]
proof_path = os.environ["PROOF"]
transcript = open(transcript_path).read().rstrip("\n")
doc = open(proof_path).read()
marker = "## The transcript\n\n```\n"
head, sep, _old = doc.partition(marker)
if not sep:
    sys.exit("FATAL: transcript marker not found in %s" % proof_path)
doc = head + marker + transcript + "\n```\n"
if not all(ord(ch) < 128 for ch in doc):
    sys.exit("FATAL: non-ASCII character in the assembled proof")
open(proof_path, "w", newline="").write(doc)
print("spliced %s into %s" % (transcript_path, proof_path))
PYEOF
  exit $?
fi

if [ "$MODE" != "run" ]; then
  echo "usage: make-proof-macos.sh [run|splice <proof-file>]" >&2
  exit 2
fi

cd "$BASE/cc-scrub" || exit 9
: > "$T"

say() { printf '%s\n' "$@" >> "$T"; }

run() {
  printf '$ %s\n' "$*" >> "$T"
  "$@" >> "$T" 2>&1
  printf '[exit %d]\n\n' "$?" >> "$T"
}

say "=== 1. the machine and the interpreter ==="
say ""
run sw_vers
run python3 -V

say "=== 2. a brand new virtual environment ==="
say ""
run python3 -m venv "$BASE/venv"

say "=== 3. install from pip only - Pillow, the Vision binding, pytest ==="
say ""
run ../venv/bin/python -m pip install --upgrade pip
run ../venv/bin/python -m pip install pillow pyobjc-framework-Vision pytest
run ../venv/bin/python -m pip list

say "=== 4. draw the synthetic samples ==="
say ""
run ../venv/bin/python gen_samples.py ../samples

say "=== 5. the denylist used for the proof (the shipped example, copied) ==="
say ""
cp terms.example.txt ../terms.txt
run cat ../terms.txt

say "=== 6. check-only finds the planted terms, with coordinates ==="
say ""
run ../venv/bin/python main.py ../samples/sample-normal.png --check-only --terms-file ../terms.txt
run ../venv/bin/python main.py ../samples/sample-small-ui.png --check-only --terms-file ../terms.txt
run ../venv/bin/python main.py ../samples/sample-glyph.png --check-only --terms-file ../terms.txt

say "=== 7. the same glyph case with folding turned off ==="
say "(this recognizer reads the sample cleanly, so the term is still found -"
say " see the notes above the transcript; the fold-miss property is proven"
say " by the scripted-backend tests in step 12 instead)"
say ""
run ../venv/bin/python main.py ../samples/sample-glyph.png --check-only --no-fold --terms-file ../terms.txt

say "=== 8. an image with no text at all is a broken read, not a clean image ==="
say ""
run ../venv/bin/python main.py ../samples/sample-notext.png --check-only --terms-file ../terms.txt

say "=== 9. scrub each sample and let the verify pass rule on it ==="
say "(-o naming a directory requires the directory to exist - a path that"
say " does not exist yet is read as an output file name, so it is created"
say " here first, exactly as the README says to)"
say ""
run mkdir ../out
run ../venv/bin/python main.py ../samples/sample-normal.png -o ../out --terms-file ../terms.txt
run ../venv/bin/python main.py ../samples/sample-small-ui.png -o ../out --terms-file ../terms.txt
run ../venv/bin/python main.py ../samples/sample-glyph.png -o ../out --terms-file ../terms.txt

say "=== 9b. the same scrub in --patch mode - exercised, not just described ==="
say ""
run mkdir ../out-patch
run ../venv/bin/python main.py ../samples/sample-normal.png -o ../out-patch --patch --terms-file ../terms.txt

say "=== 10. re-check every scrubbed output with the same engine ==="
say ""
run ../venv/bin/python main.py ../out/sample-normal-scrubbed.png --check-only --terms-file ../terms.txt
run ../venv/bin/python main.py ../out/sample-small-ui-scrubbed.png --check-only --terms-file ../terms.txt
run ../venv/bin/python main.py ../out/sample-glyph-scrubbed.png --check-only --terms-file ../terms.txt
run ../venv/bin/python main.py ../out-patch/sample-normal-scrubbed.png --check-only --terms-file ../terms.txt

say "=== 11. a folder holding only outputs is an empty input set ==="
say "(the *-scrubbed suffix is skipped by design, which is what makes"
say " re-running a folder safe - the tool says so rather than doing nothing)"
say ""
run ../venv/bin/python main.py ../out --check-only --terms-file ../terms.txt

say "=== 12. the full test suite, in the same clean environment ==="
say ""
run ../venv/bin/python -m pytest tests/ -v -rs

echo "DRIVER DONE - transcript at $T"
