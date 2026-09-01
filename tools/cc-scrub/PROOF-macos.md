# cc-scrub - clean install proof, macOS

This is the real transcript of a real run, not a summary of one. It was
produced by creating an empty virtual environment on a macOS machine,
installing Pillow, pyobjc-framework-Vision and pytest from pip and nothing
else, drawing the synthetic samples, and then driving cc-scrub over them.
Every command below is echoed before its output and every exit code is
printed after it.

It was run from `/private/tmp/cc-scrub-proof` rather than from a checkout,
so nothing in the transcript names a person, a machine or a private
repository. The samples are drawn by `gen_samples.py` and the denylist is
the shipped `terms.example.txt` copied unchanged - the three planted terms
are `example@example.com`, `myorg/secret-repo` and
`internal-hostname.local`, all deliberately fake.

## What this proves

- **A clean install works from pip alone.** No installer, no external
  executable, no tesseract, no subprocess. Pillow 12.3.0 and
  pyobjc-framework-Vision 12.2.2 - which pulls in its pyobjc-core, Cocoa,
  Quartz and CoreML wheels itself - on Python 3.14.6, macOS 26.5.2.
- **check-only finds each planted term and prints CORRECT pixel
  coordinates**, exiting 1 and leaving the image untouched (step 6). The
  recognizer reports rectangles normalized to 0..1 from the bottom-left
  corner, and the conversion to top-left pixels is visible in the numbers:
  the small-ui repo line is drawn 76 px from the top of a 220 px image and
  is reported at `y=76` (a bottom-origin mistake would report 130), and the
  glyph host line is drawn at 70 and reported at `y=72` (a mistake would
  report 135). The final catch is step 9: a flipped rectangle would blur
  the wrong pixels, the term would survive in the output, and the verify
  pass would refuse - it does not.
- **Line joining is visible in a live read, though not load bearing for
  it.** The engine returned the label word `repo` split in two as `re po`
  (step 6), which shows the joined-line matcher at work on real engine
  output - but the sensitive term itself came back as one OCR word in that
  read, so this transcript does not demonstrate that joining was the
  difference between hit and miss. The property itself is proven
  deterministically by the scripted-backend joining tests in step 12.
- **This engine reads every planted term at every scale, native included** -
  each hit is reported as `scales=1,2,3`. On this engine and these samples
  the multi-scale union is redundancy rather than the difference between
  hit and miss; contrast the Windows proof, where the 10 px line resolves
  only at scales 2 and 3. Every scale still always runs.
- **The fold-versus-miss demonstration is not reproducible against this
  engine.** It reads the 11 px glyph sample cleanly at every scale -
  `https://internal-hostname.local/status/session`, letter perfect - so
  with `--no-fold` the term is STILL found and the run exits 1 (step 7),
  where the Windows engine misreads the leading glyph and misses. That is
  why the unfolded-miss half of the glyph test asserts on Windows only. The
  fold-miss property itself is proven on every platform by the
  scripted-backend and folding unit tests inside step 12, which feed the
  matcher a synthetic misread deterministically.
- **An image with no text is a broken read, not a clean image.**
  `sample-notext.png` reads zero words at every scale and the tool exits 2
  refusing to call it scrubbed (step 8).
- **The output is published only after it verifies.** Each scrub writes a
  `CANDIDATE` temporary, re-reads that candidate from disk, and only then
  renames it to the final name: `VERIFY PASSED: 1 hit(s) found, 1
  region(s) redacted, verify OCR read N words in the output and 0 denylist
  hit(s) remain`, with N at 57, 54 and 49 - never zero, so a verify
  instrument that reads nothing cannot certify anything (step 9).
- **The outputs really are clean.** Each written file is re-opened from
  disk and read again, independently of the scrub run, with zero hits
  (step 10).
- **The whole test suite passes in that same clean environment**: 52
  collected, 52 passed, zero skipped, zero failures (step 12). Nothing is
  assumed from the platform name: the case-alias arms - tests whose
  premise is that two spellings name one file - measure the volume they
  actually write to with the same probe the tool uses, and they skip only
  on a measured answer (the suite step runs with -rs so any skip would be
  printed with its reason; none appears). On this machine's
  case-insensitive volume every one of them ran, so the guards on the only
  unredacted copy of the input - the case-variant arm, the hard-link arm -
  and the case-colliding-output refusal are all proved live here, not
  inferred from Windows.

## What this does NOT prove

- **Nothing about Windows.** That proof is
  [PROOF-windows.md](PROOF-windows.md).
- **Nothing about other macOS versions.** The recognizer is the operating
  system's own and its output is not identical across versions. The word
  counts and the exact reads below are what this machine produced. Re-run
  the proof rather than trusting the numbers.
- **Nothing about real screenshots.** These are synthetic images drawn by
  Pillow. They are built to exercise the small-text and glyph-confusion
  cases on purpose, which is not the same thing as a survey of real product
  chrome.
- **Nothing about a packaged executable.** cc-scrub is run from source
  here. It has no PyInstaller build and is not in the shipped tool bundle.

## The transcript

```
=== 1. the machine and the interpreter ===

$ sw_vers
ProductName:		macOS
ProductVersion:		26.5.2
BuildVersion:		25F84
[exit 0]

$ python3 -V
Python 3.14.6
[exit 0]

=== 2. a brand new virtual environment ===

$ python3 -m venv /private/tmp/cc-scrub-proof/venv
[exit 0]

=== 3. install from pip only - Pillow, the Vision binding, pytest ===

$ ../venv/bin/python -m pip install --upgrade pip
Requirement already satisfied: pip in /private/tmp/cc-scrub-proof/venv/lib/python3.14/site-packages (26.1.2)
Collecting pip
  Using cached pip-26.2.1-py3-none-any.whl.metadata (4.6 kB)
Using cached pip-26.2.1-py3-none-any.whl (1.8 MB)
Installing collected packages: pip
  Attempting uninstall: pip
    Found existing installation: pip 26.1.2
    Uninstalling pip-26.1.2:
      Successfully uninstalled pip-26.1.2
Successfully installed pip-26.2.1
[exit 0]

$ ../venv/bin/python -m pip install pillow pyobjc-framework-Vision pytest
Collecting pillow
  Using cached pillow-12.3.0-cp314-cp314-macosx_11_0_arm64.whl.metadata (9.1 kB)
Collecting pyobjc-framework-Vision
  Using cached pyobjc_framework_vision-12.2.2-cp314-cp314-macosx_10_15_universal2.whl.metadata (2.6 kB)
Collecting pytest
  Using cached pytest-9.1.1-py3-none-any.whl.metadata (7.6 kB)
Collecting pyobjc-core>=12.2.2 (from pyobjc-framework-Vision)
  Using cached pyobjc_core-12.2.2-cp314-cp314-macosx_10_15_universal2.whl.metadata (2.8 kB)
Collecting pyobjc-framework-Cocoa>=12.2.2 (from pyobjc-framework-Vision)
  Using cached pyobjc_framework_cocoa-12.2.2-cp314-cp314-macosx_10_15_universal2.whl.metadata (2.6 kB)
Collecting pyobjc-framework-Quartz>=12.2.2 (from pyobjc-framework-Vision)
  Using cached pyobjc_framework_quartz-12.2.2-cp314-cp314-macosx_10_15_universal2.whl.metadata (3.6 kB)
Collecting pyobjc-framework-CoreML>=12.2.2 (from pyobjc-framework-Vision)
  Using cached pyobjc_framework_coreml-12.2.2-cp314-cp314-macosx_10_15_universal2.whl.metadata (2.5 kB)
Collecting iniconfig>=1.0.1 (from pytest)
  Using cached iniconfig-2.3.0-py3-none-any.whl.metadata (2.5 kB)
Collecting packaging>=22 (from pytest)
  Using cached packaging-26.3-py3-none-any.whl.metadata (3.5 kB)
Collecting pluggy<2,>=1.5 (from pytest)
  Using cached pluggy-1.6.0-py3-none-any.whl.metadata (4.8 kB)
Collecting pygments>=2.7.2 (from pytest)
  Using cached pygments-2.21.0-py3-none-any.whl.metadata (2.5 kB)
Using cached pillow-12.3.0-cp314-cp314-macosx_11_0_arm64.whl (4.8 MB)
Using cached pyobjc_framework_vision-12.2.2-cp314-cp314-macosx_10_15_universal2.whl (16 kB)
Using cached pytest-9.1.1-py3-none-any.whl (386 kB)
Using cached pluggy-1.6.0-py3-none-any.whl (20 kB)
Using cached iniconfig-2.3.0-py3-none-any.whl (7.5 kB)
Using cached packaging-26.3-py3-none-any.whl (129 kB)
Using cached pygments-2.21.0-py3-none-any.whl (1.3 MB)
Using cached pyobjc_core-12.2.2-cp314-cp314-macosx_10_15_universal2.whl (6.4 MB)
Using cached pyobjc_framework_cocoa-12.2.2-cp314-cp314-macosx_10_15_universal2.whl (388 kB)
Using cached pyobjc_framework_coreml-12.2.2-cp314-cp314-macosx_10_15_universal2.whl (12 kB)
Using cached pyobjc_framework_quartz-12.2.2-cp314-cp314-macosx_10_15_universal2.whl (219 kB)
Installing collected packages: pyobjc-core, pygments, pluggy, pillow, packaging, iniconfig, pytest, pyobjc-framework-Cocoa, pyobjc-framework-Quartz, pyobjc-framework-CoreML, pyobjc-framework-Vision

Successfully installed iniconfig-2.3.0 packaging-26.3 pillow-12.3.0 pluggy-1.6.0 pygments-2.21.0 pyobjc-core-12.2.2 pyobjc-framework-Cocoa-12.2.2 pyobjc-framework-CoreML-12.2.2 pyobjc-framework-Quartz-12.2.2 pyobjc-framework-Vision-12.2.2 pytest-9.1.1
[exit 0]

$ ../venv/bin/python -m pip list
Package                 Version
----------------------- -------
iniconfig               2.3.0
packaging               26.3
pillow                  12.3.0
pip                     26.2.1
pluggy                  1.6.0
Pygments                2.21.0
pyobjc-core             12.2.2
pyobjc-framework-Cocoa  12.2.2
pyobjc-framework-CoreML 12.2.2
pyobjc-framework-Quartz 12.2.2
pyobjc-framework-Vision 12.2.2
pytest                  9.1.1
[exit 0]

=== 4. draw the synthetic samples ===

$ ../venv/bin/python gen_samples.py ../samples
cc-scrub sample generator
  WROTE ../samples/sample-normal.png (900x260)
  WROTE ../samples/sample-small-ui.png (900x220)
  WROTE ../samples/sample-glyph.png (900x220)
  WROTE ../samples/sample-notext.png (640x400)
Planted terms: example@example.com, myorg/secret-repo, internal-hostname.local
[exit 0]

=== 5. the denylist used for the proof (the shipped example, copied) ===

$ cat ../terms.txt
# cc-scrub denylist - EXAMPLE ONLY.
#
# Copy this file to terms.txt beside it and put your own terms in that copy.
# terms.txt is listed in this folder's .gitignore and must never be
# committed: a real denylist is a list of the exact strings you are trying
# to keep out of public screenshots, so committing it publishes them.
#
# One term per line. '#' starts a comment. Matching is case-insensitive and
# by substring, so 'myorg' also hits 'myorg/secret-repo'. Punctuation and
# spaces are ignored on both sides, so a term split across OCR words still
# matches.
#
# Keep terms specific. Glyph folding (on by default) deliberately
# over-matches, so a two or three character term will hit far more than you
# intended.

example@example.com
myorg/secret-repo
internal-hostname.local
[exit 0]

=== 6. check-only finds the planted terms, with coordinates ===

$ ../venv/bin/python main.py ../samples/sample-normal.png --check-only --terms-file ../terms.txt
cc-scrub
  ocr engine : macOS Vision text recognizer (pyobjc)
  terms file : ../terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : check-only

IMAGE ../samples/sample-normal.png
  size 900x260  scales 1,2,3  fold on
  ocr scale 1: 20 words in 5 lines
  ocr scale 2: 20 words in 5 lines
  ocr scale 3: 20 words in 5 lines
  HITS: 1
    term='example@example.com' rect=(x=180 y=115 w=220 h=30) scales=1,2,3 ocr_line='Contact address: example@example.com'
  CHECK-ONLY: 1 hit(s) present, image NOT modified.

SUMMARY: 1 image(s) processed, 0 clean, 1 failed.
  FAILED ../samples/sample-normal.png
[exit 1]

$ ../venv/bin/python main.py ../samples/sample-small-ui.png --check-only --terms-file ../terms.txt
cc-scrub
  ocr engine : macOS Vision text recognizer (pyobjc)
  terms file : ../terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : check-only

IMAGE ../samples/sample-small-ui.png
  size 900x220  scales 1,2,3  fold on
  ocr scale 1: 18 words in 6 lines
  ocr scale 2: 19 words in 6 lines
  ocr scale 3: 20 words in 6 lines
  HITS: 1
    term='myorg/secret-repo' rect=(x=39 y=76 w=87 h=14) scales=1,2,3 ocr_line='re po myorg/secret-repo'
  CHECK-ONLY: 1 hit(s) present, image NOT modified.

SUMMARY: 1 image(s) processed, 0 clean, 1 failed.
  FAILED ../samples/sample-small-ui.png
[exit 1]

$ ../venv/bin/python main.py ../samples/sample-glyph.png --check-only --terms-file ../terms.txt
cc-scrub
  ocr engine : macOS Vision text recognizer (pyobjc)
  terms file : ../terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : check-only

IMAGE ../samples/sample-glyph.png
  size 900x220  scales 1,2,3  fold on
  ocr scale 1: 18 words in 4 lines
  ocr scale 2: 18 words in 4 lines
  ocr scale 3: 18 words in 4 lines
  HITS: 1
    term='internal-hostname.local' rect=(x=16 y=72 w=237 h=13) scales=1,2,3 ocr_line='https://internal-hostname.local/status/session'
  CHECK-ONLY: 1 hit(s) present, image NOT modified.

SUMMARY: 1 image(s) processed, 0 clean, 1 failed.
  FAILED ../samples/sample-glyph.png
[exit 1]

=== 7. the same glyph case with folding turned off ===
(this recognizer reads the sample cleanly, so the term is still found -
 see the notes above the transcript; the fold-miss property is proven
 by the scripted-backend tests in step 12 instead)

$ ../venv/bin/python main.py ../samples/sample-glyph.png --check-only --no-fold --terms-file ../terms.txt
cc-scrub
  ocr engine : macOS Vision text recognizer (pyobjc)
  terms file : ../terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : check-only

IMAGE ../samples/sample-glyph.png
  size 900x220  scales 1,2,3  fold off
  ocr scale 1: 18 words in 4 lines
  ocr scale 2: 18 words in 4 lines
  ocr scale 3: 18 words in 4 lines
  HITS: 1
    term='internal-hostname.local' rect=(x=16 y=72 w=237 h=13) scales=1,2,3 ocr_line='https://internal-hostname.local/status/session'
  CHECK-ONLY: 1 hit(s) present, image NOT modified.

SUMMARY: 1 image(s) processed, 0 clean, 1 failed.
  FAILED ../samples/sample-glyph.png
[exit 1]

=== 8. an image with no text at all is a broken read, not a clean image ===

$ ../venv/bin/python main.py ../samples/sample-notext.png --check-only --terms-file ../terms.txt
FATAL: OCR read ZERO words from ../samples/sample-notext.png across scales 1,2,3. That is a broken read, not a clean image. Refusing to call this scrubbed.
cc-scrub
  ocr engine : macOS Vision text recognizer (pyobjc)
  terms file : ../terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : check-only

IMAGE ../samples/sample-notext.png
  size 640x400  scales 1,2,3  fold on
  ocr scale 1: 0 words in 0 lines
  ocr scale 2: 0 words in 0 lines
  ocr scale 3: 0 words in 0 lines
[exit 2]

=== 9. scrub each sample and let the verify pass rule on it ===
(-o naming a directory requires the directory to exist - a path that
 does not exist yet is read as an output file name, so it is created
 here first, exactly as the README says to)

$ mkdir ../out
[exit 0]

$ ../venv/bin/python main.py ../samples/sample-normal.png -o ../out --terms-file ../terms.txt
cc-scrub
  ocr engine : macOS Vision text recognizer (pyobjc)
  terms file : ../terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : blur

IMAGE ../samples/sample-normal.png
  size 900x260  scales 1,2,3  fold on
  ocr scale 1: 20 words in 5 lines
  ocr scale 2: 20 words in 5 lines
  ocr scale 3: 20 words in 5 lines
  HITS: 1
    term='example@example.com' rect=(x=180 y=115 w=220 h=30) scales=1,2,3 ocr_line='Contact address: example@example.com'
  CANDIDATE /private/tmp/cc-scrub-proof/out/sample-normal-scrubbed.png.aru8vlyr.tmp (mode=blur pad=4)
  VERIFY: re-reading /private/tmp/cc-scrub-proof/out/sample-normal-scrubbed.png.aru8vlyr.tmp
    verify scale 1: 19 words in 5 lines
    verify scale 2: 19 words in 5 lines
    verify scale 3: 19 words in 5 lines
  WROTE ../out/sample-normal-scrubbed.png (mode=blur pad=4)
  VERIFY PASSED: 1 hit(s) found, 1 region(s) redacted, verify OCR read 57 words in the output and 0 denylist hit(s) remain.

SUMMARY: 1 image(s) processed, 1 clean, 0 failed.
[exit 0]

$ ../venv/bin/python main.py ../samples/sample-small-ui.png -o ../out --terms-file ../terms.txt
cc-scrub
  ocr engine : macOS Vision text recognizer (pyobjc)
  terms file : ../terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : blur

IMAGE ../samples/sample-small-ui.png
  size 900x220  scales 1,2,3  fold on
  ocr scale 1: 18 words in 6 lines
  ocr scale 2: 19 words in 6 lines
  ocr scale 3: 20 words in 6 lines
  HITS: 1
    term='myorg/secret-repo' rect=(x=39 y=76 w=87 h=14) scales=1,2,3 ocr_line='re po myorg/secret-repo'
  CANDIDATE /private/tmp/cc-scrub-proof/out/sample-small-ui-scrubbed.png.4k1sfa5d.tmp (mode=blur pad=4)
  VERIFY: re-reading /private/tmp/cc-scrub-proof/out/sample-small-ui-scrubbed.png.4k1sfa5d.tmp
    verify scale 1: 18 words in 6 lines
    verify scale 2: 18 words in 6 lines
    verify scale 3: 18 words in 6 lines
  WROTE ../out/sample-small-ui-scrubbed.png (mode=blur pad=4)
  VERIFY PASSED: 1 hit(s) found, 1 region(s) redacted, verify OCR read 54 words in the output and 0 denylist hit(s) remain.

SUMMARY: 1 image(s) processed, 1 clean, 0 failed.
[exit 0]

$ ../venv/bin/python main.py ../samples/sample-glyph.png -o ../out --terms-file ../terms.txt
cc-scrub
  ocr engine : macOS Vision text recognizer (pyobjc)
  terms file : ../terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : blur

IMAGE ../samples/sample-glyph.png
  size 900x220  scales 1,2,3  fold on
  ocr scale 1: 18 words in 4 lines
  ocr scale 2: 18 words in 4 lines
  ocr scale 3: 18 words in 4 lines
  HITS: 1
    term='internal-hostname.local' rect=(x=16 y=72 w=237 h=13) scales=1,2,3 ocr_line='https://internal-hostname.local/status/session'
  CANDIDATE /private/tmp/cc-scrub-proof/out/sample-glyph-scrubbed.png.7x90v8yn.tmp (mode=blur pad=4)
  VERIFY: re-reading /private/tmp/cc-scrub-proof/out/sample-glyph-scrubbed.png.7x90v8yn.tmp
    verify scale 1: 15 words in 3 lines
    verify scale 2: 17 words in 3 lines
    verify scale 3: 17 words in 3 lines
  WROTE ../out/sample-glyph-scrubbed.png (mode=blur pad=4)
  VERIFY PASSED: 1 hit(s) found, 1 region(s) redacted, verify OCR read 49 words in the output and 0 denylist hit(s) remain.

SUMMARY: 1 image(s) processed, 1 clean, 0 failed.
[exit 0]

=== 10. re-check every scrubbed output - nothing left to find ===

$ ../venv/bin/python main.py ../out/sample-normal-scrubbed.png --check-only --terms-file ../terms.txt
cc-scrub
  ocr engine : macOS Vision text recognizer (pyobjc)
  terms file : ../terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : check-only

IMAGE ../out/sample-normal-scrubbed.png
  size 900x260  scales 1,2,3  fold on
  ocr scale 1: 19 words in 5 lines
  ocr scale 2: 19 words in 5 lines
  ocr scale 3: 19 words in 5 lines
  HITS: 0
  CHECK-ONLY: no hits against 3 terms over 57 OCR words.

SUMMARY: 1 image(s) processed, 1 clean, 0 failed.
[exit 0]

$ ../venv/bin/python main.py ../out/sample-small-ui-scrubbed.png --check-only --terms-file ../terms.txt
cc-scrub
  ocr engine : macOS Vision text recognizer (pyobjc)
  terms file : ../terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : check-only

IMAGE ../out/sample-small-ui-scrubbed.png
  size 900x220  scales 1,2,3  fold on
  ocr scale 1: 18 words in 6 lines
  ocr scale 2: 18 words in 6 lines
  ocr scale 3: 18 words in 6 lines
  HITS: 0
  CHECK-ONLY: no hits against 3 terms over 54 OCR words.

SUMMARY: 1 image(s) processed, 1 clean, 0 failed.
[exit 0]

$ ../venv/bin/python main.py ../out/sample-glyph-scrubbed.png --check-only --terms-file ../terms.txt
cc-scrub
  ocr engine : macOS Vision text recognizer (pyobjc)
  terms file : ../terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : check-only

IMAGE ../out/sample-glyph-scrubbed.png
  size 900x220  scales 1,2,3  fold on
  ocr scale 1: 15 words in 3 lines
  ocr scale 2: 17 words in 3 lines
  ocr scale 3: 17 words in 3 lines
  HITS: 0
  CHECK-ONLY: no hits against 3 terms over 49 OCR words.

SUMMARY: 1 image(s) processed, 1 clean, 0 failed.
[exit 0]

=== 11. a folder holding only outputs is an empty input set ===
(the *-scrubbed suffix is skipped by design, which is what makes
 re-running a folder safe - the tool says so rather than doing nothing)

$ ../venv/bin/python main.py ../out --check-only --terms-file ../terms.txt
FATAL: no images found in directory ../out
[exit 2]

=== 12. the full test suite, in the same clean environment ===

$ ../venv/bin/python -m pytest tests/ -v -rs
============================= test session starts ==============================
platform darwin -- Python 3.14.6, pytest-9.1.1, pluggy-1.6.0 -- /private/tmp/cc-scrub-proof/venv/bin/python
cachedir: .pytest_cache
rootdir: /private/tmp/cc-scrub-proof/cc-scrub
configfile: pyproject.toml
collecting ... collected 52 items

tests/test_cc_scrub.py::test_generator_writes_all_four_samples PASSED    [  1%]
tests/test_cc_scrub.py::test_check_only_finds_the_planted_email_with_coordinates PASSED [  3%]
tests/test_cc_scrub.py::test_check_only_finds_small_grey_ui_text_only_after_upscaling PASSED [  5%]
tests/test_cc_scrub.py::test_glyph_confusion_is_caught_by_folding_and_missed_without_it PASSED [  7%]
tests/test_cc_scrub.py::test_scrub_redacts_and_the_verify_pass_proves_it[sample-normal.png-example@example.com] PASSED [  9%]
tests/test_cc_scrub.py::test_scrub_redacts_and_the_verify_pass_proves_it[sample-small-ui.png-myorg/secret-repo] PASSED [ 11%]
tests/test_cc_scrub.py::test_scrub_redacts_and_the_verify_pass_proves_it[sample-glyph.png-internal-hostname.local] PASSED [ 13%]
tests/test_cc_scrub.py::test_the_scrubbed_output_is_clean_when_checked_again PASSED [ 15%]
tests/test_cc_scrub.py::test_a_directory_run_skips_files_already_scrubbed PASSED [ 17%]
tests/test_cc_scrub.py::test_an_image_with_no_text_is_a_broken_read_not_a_clean_image PASSED [ 19%]
tests/test_cc_scrub.py::test_a_verify_read_of_zero_words_publishes_nothing_and_exits_two PASSED [ 21%]
tests/test_cc_scrub.py::test_a_term_surviving_in_the_output_exits_one_and_publishes_nothing PASSED [ 23%]
tests/test_cc_scrub.py::test_a_passing_scripted_run_publishes_the_candidate PASSED [ 25%]
tests/test_cc_scrub.py::test_folding_finds_a_misread_term_and_no_fold_misses_it PASSED [ 26%]
tests/test_cc_scrub.py::test_a_term_split_across_adjacent_words_on_one_line_is_joined PASSED [ 28%]
tests/test_cc_scrub.py::test_a_term_split_across_two_lines_is_not_joined PASSED [ 30%]
tests/test_cc_scrub.py::test_a_scaled_read_maps_back_to_native_coordinates_exactly PASSED [ 32%]
tests/test_cc_scrub.py::test_a_read_over_the_megapixel_budget_is_refused_before_it_is_attempted PASSED [ 34%]
tests/test_cc_scrub.py::test_the_megapixel_budget_must_be_at_least_one PASSED [ 36%]
tests/test_cc_scrub.py::test_an_image_too_big_for_the_engine_is_refused_before_it_is_read PASSED [ 38%]
tests/test_cc_scrub.py::test_refuses_to_overwrite_the_input_image PASSED [ 40%]
tests/test_cc_scrub.py::test_refuses_to_overwrite_the_input_addressed_in_a_different_case PASSED [ 42%]
tests/test_cc_scrub.py::test_refuses_to_overwrite_the_input_reached_through_a_hard_link PASSED [ 44%]
tests/test_cc_scrub.py::test_a_missing_terms_file_names_the_example_to_copy PASSED [ 46%]
tests/test_cc_scrub.py::test_the_shipped_example_denylist_parses PASSED  [ 48%]
tests/test_cc_scrub.py::test_bad_scales_are_a_usage_error PASSED         [ 50%]
tests/test_cc_scrub.py::test_word_ranges_count_utf16_code_units_not_code_points PASSED [ 51%]
tests/test_cc_scrub.py::test_word_ranges_match_code_points_for_plain_text PASSED [ 53%]
tests/test_cc_scrub.py::test_a_denylist_term_that_can_never_match_is_refused PASSED [ 55%]
tests/test_cc_scrub.py::test_term_validation_follows_the_normalisation_actually_in_force PASSED [ 57%]
tests/test_cc_scrub.py::test_two_inputs_that_would_share_one_output_are_refused PASSED [ 59%]
tests/test_cc_scrub.py::test_two_inputs_whose_stems_differ_only_in_case_are_refused PASSED [ 61%]
tests/test_cc_scrub.py::test_collision_detection_does_not_depend_on_the_host_normcase PASSED [ 63%]
tests/test_cc_scrub.py::test_the_case_probe_answers_from_the_filesystem_and_cleans_up PASSED [ 65%]
tests/test_cc_scrub.py::test_the_case_probe_refuses_to_guess_when_it_cannot_be_created PASSED [ 67%]
tests/test_cc_scrub.py::test_an_output_directory_that_cannot_be_created_exits_two PASSED [ 69%]
tests/test_cc_scrub.py::test_pad_box_grows_outwards_to_whole_pixels PASSED [ 71%]
tests/test_cc_scrub.py::test_pad_box_pads_and_clamps_to_the_image PASSED [ 73%]
tests/test_cc_scrub.py::test_normalise_drops_punctuation_and_case PASSED [ 75%]
tests/test_cc_scrub.py::test_normalise_folds_every_advertised_glyph_class PASSED [ 76%]
tests/test_cc_scrub.py::test_parse_scales_sorts_and_deduplicates PASSED  [ 78%]
tests/test_cc_scrub.py::test_parse_scales_rejects_rubbish PASSED         [ 80%]
tests/test_cc_scrub.py::test_load_terms_ignores_comments_and_blank_lines PASSED [ 82%]
tests/test_cc_scrub.py::test_load_terms_rejects_an_empty_denylist PASSED [ 84%]
tests/test_cc_scrub.py::test_merge_hits_unions_overlapping_rectangles_of_one_term PASSED [ 86%]
tests/test_cc_scrub.py::test_merge_hits_keeps_different_terms_apart PASSED [ 88%]
tests/test_cc_scrub.py::test_gather_inputs_skips_already_scrubbed_files PASSED [ 90%]
tests/test_cc_scrub.py::test_gather_inputs_rejects_a_missing_path PASSED [ 92%]
tests/test_cc_scrub.py::test_is_same_file_sees_through_a_case_variant_of_an_existing_path PASSED [ 94%]
tests/test_cc_scrub.py::test_is_same_file_compares_canonically_when_the_target_is_not_created_yet PASSED [ 96%]
tests/test_cc_scrub.py::test_is_same_file_says_no_to_two_genuinely_different_files PASSED [ 98%]
tests/test_cc_scrub.py::test_output_path_defaults_to_the_scrubbed_name_beside_the_input PASSED [100%]

============================== 52 passed in 3.08s ==============================
[exit 0]
```
