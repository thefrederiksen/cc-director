#!/usr/bin/env bash
#
# The driver that produced PROOF-windows.md.
#
# It builds a brand new virtual environment, installs Pillow, winocr and
# pytest from pip and nothing else, draws the synthetic samples, and drives
# cc-scrub over them. Every command is echoed before it runs and its exit
# code is printed after it, so the transcript can be read without trusting
# any summary of it.
#
# Usage:
#     bash proof-run-windows.sh [work-directory] > transcript.txt 2>&1
#
# The work directory defaults to ./proof-work. The committed transcript was
# produced with it set to D:/cc-scrub-proof, which is why the paths in that
# file read the way they do: the run is deliberately done from OUTSIDE any
# checkout so no path in the published transcript names a person, a machine
# or a private repository. Pick a neutral path if you regenerate it.
#
# Windows only - it installs winocr. The macOS transcript has its own driver.
#
# This script is not part of the tool. Nothing imports it, nothing runs it
# during the test suite, and no build references it.
#
# Nothing here restates a figure the tool prints. A narrative number that has
# to be kept in step with the tool is a number that drifts, and this file has
# drifted twice - once on 14.3 and once on 15.0 - both times in commentary
# and never in tool output.

set -u

WORK="${1:-$PWD/proof-work}"
TOOL_DIR="$(cd "$(dirname "$0")" && pwd)"
TERMS="$WORK/terms.txt"

mkdir -p "$WORK"

# A virtual environment puts its interpreter in Scripts on Windows and in bin
# everywhere else. This picks the one that is there; if neither is, that is
# an error and the run stops rather than guessing.
venv_python() {
  if [ -x "$WORK/venv/Scripts/python.exe" ]; then
    echo "$WORK/venv/Scripts/python.exe"
  elif [ -x "$WORK/venv/bin/python" ]; then
    echo "$WORK/venv/bin/python"
  else
    echo "FATAL: no interpreter in $WORK/venv - the environment was not created" >&2
    exit 2
  fi
}

run() {
  echo ""
  echo "\$ $*"
  "$@" 2>&1
  echo "[exit $?]"
}

echo "=== 1. the machine and the interpreter ==="
run cmd //c ver
run python -V

echo ""
echo "=== 2. a brand new virtual environment ==="
run python -m venv "$WORK/venv"

PY="$(venv_python)"

echo ""
echo "=== 3. install from pip only - Pillow, winocr, pytest ==="
run "$PY" -m pip install --upgrade pip
run "$PY" -m pip install pillow winocr pytest
run "$PY" -m pip list

echo ""
echo "=== 4. draw the synthetic samples ==="
cd "$TOOL_DIR"
run "$PY" gen_samples.py "$WORK/samples"

echo ""
echo "=== 5. the denylist used for the proof (the shipped example, copied) ==="
cp terms.example.txt "$TERMS"
run cat "$TERMS"

echo ""
echo "=== 6. check-only finds the planted terms, with coordinates ==="
run "$PY" main.py "$WORK/samples/sample-normal.png" --check-only --terms-file "$TERMS"
run "$PY" main.py "$WORK/samples/sample-small-ui.png" --check-only --terms-file "$TERMS"
run "$PY" main.py "$WORK/samples/sample-glyph.png" --check-only --terms-file "$TERMS"

echo ""
echo "=== 7. the same glyph case with folding turned off - it is missed ==="
run "$PY" main.py "$WORK/samples/sample-glyph.png" --check-only --no-fold --terms-file "$TERMS"

echo ""
echo "=== 8. an image with no text at all is a broken read, not a clean image ==="
run "$PY" main.py "$WORK/samples/sample-notext.png" --check-only --terms-file "$TERMS"

echo ""
echo "=== 9. scrub each sample: candidate, verify, then publish ==="
mkdir -p "$WORK/out"
run "$PY" main.py "$WORK/samples/sample-normal.png" -o "$WORK/out" --terms-file "$TERMS"
run "$PY" main.py "$WORK/samples/sample-small-ui.png" -o "$WORK/out" --terms-file "$TERMS"
run "$PY" main.py "$WORK/samples/sample-glyph.png" -o "$WORK/out" --terms-file "$TERMS"

echo ""
echo "=== 10. nothing unverified is left lying about ==="
echo "(the output directory holds the published outputs and no candidates)"
run ls -1 "$WORK/out"

echo ""
echo "=== 11. an existing output is not replaced by accident ==="
run "$PY" main.py "$WORK/samples/sample-normal.png" -o "$WORK/out" --terms-file "$TERMS"

echo ""
echo "=== 12. --force is how you say you meant it ==="
run "$PY" main.py "$WORK/samples/sample-normal.png" -o "$WORK/out" --force --terms-file "$TERMS"

echo ""
echo "=== 13. the input is never a legal destination, in any spelling ==="
cp "$WORK/samples/sample-normal.png" "$WORK/shot.png"
run "$PY" main.py "$WORK/shot.png" -o "$WORK/SHOT.PNG" --force --terms-file "$TERMS"

echo ""
echo "=== 14. two inputs that would land on one output file ==="
echo "(stems differing only in case; the destination directory is asked"
echo " whether it treats the two spellings as one file, and it does here)"
mkdir -p "$WORK/collide"
cp "$WORK/samples/sample-normal.png" "$WORK/collide/Shot.png"
cp "$WORK/samples/sample-normal.png" "$WORK/collide/shot.jpg"
run "$PY" main.py "$WORK/collide" --terms-file "$TERMS"

echo ""
echo "=== 15. a read over the megapixel budget is refused before it is tried ==="
echo "(the scaled read is inside the engine's side limit and over the"
echo " budget, so it is the AREA check firing and not the side check."
echo " The exact count is the tool's to print, below - this commentary"
echo " deliberately restates no figure, because a narrative number that"
echo " has to be kept in step with the tool is a number that drifts)"
run "$PY" main.py "$WORK/samples/sample-normal.png" --check-only --scales 8 --max-megapixels 1 --terms-file "$TERMS"

echo ""
echo "=== 16. --patch, the mode recommended for anything leaving the org ==="
echo "(the transcript used to MENTION --patch in prose and never run it,"
echo " which certifies a mode it never exercised)"
mkdir -p "$WORK/patched"
run "$PY" main.py "$WORK/samples/sample-normal.png" -o "$WORK/patched" --patch --terms-file "$TERMS"
run "$PY" main.py "$WORK/patched/sample-normal-scrubbed.png" --check-only --terms-file "$TERMS"

echo ""
echo "=== 17. re-check every published output - nothing left to find ==="
run "$PY" main.py "$WORK/out/sample-normal-scrubbed.png" --check-only --terms-file "$TERMS"
run "$PY" main.py "$WORK/out/sample-small-ui-scrubbed.png" --check-only --terms-file "$TERMS"
run "$PY" main.py "$WORK/out/sample-glyph-scrubbed.png" --check-only --terms-file "$TERMS"

echo ""
echo "=== 18. a folder holding only outputs is an empty input set ==="
echo "(the *-scrubbed suffix is skipped by design, which is what makes"
echo " re-running a folder safe - the tool says so rather than doing nothing)"
run "$PY" main.py "$WORK/out" --check-only --terms-file "$TERMS"

echo ""
echo "=== 19. the full test suite, in the same clean environment ==="
echo "(-rs so that anything SKIPPED is printed with its reason, because a"
echo " skip that is not shown reads exactly like a pass)"
run "$PY" -m pytest tests/ -v -rs
