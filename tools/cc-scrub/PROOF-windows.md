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
  900x260 at scale 8 is 7200x2080, which is 14,976,000 pixels - 15.0
  megapixels, decimal, as the flag name says - over a 1 megapixel budget, and
  the engine is never reached (step 15).
- **The published outputs read clean on a second, independent pass.** Each
  file is re-opened from disk after the run that wrote it and read again with
  zero hits (step 16). That is a statement about this engine at these scales,
  not about the pixels: see "What this does NOT prove".
- **The whole test suite passes in that same clean environment**: 56 tests,
  NONE SKIPPED, including the three end-to-end scrub-and-verify cases
  (step 18). It is run with `-rs`, so a skip would be printed with its
  reason - a skip nobody prints reads exactly like a pass. The arms that
  need a volume on which two spellings name one file measure that on the
  directory they write to; on this volume all of them run.

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
  `os.path.normcase` so it runs anywhere, and it is in step 18; a real
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
  `test_a_term_surviving_in_the_output_...` in step 18.

## The transcript

```
=== 1. the machine and the interpreter ===

$ cmd //c ver

Microsoft Windows [Version 10.0.26200.9278]
[exit 0]

$ python -V
Python 3.11.6
[exit 0]

=== 2. a brand new virtual environment ===

$ python -m venv D:/cc-scrub-proof/venv
[exit 0]

=== 3. install from pip only - Pillow, winocr, pytest ===

$ D:/cc-scrub-proof/venv/Scripts/python.exe -m pip install --upgrade pip
Requirement already satisfied: pip in .\venv\Lib\site-packages (26.2.1)
[exit 0]

$ D:/cc-scrub-proof/venv/Scripts/python.exe -m pip install pillow winocr pytest
Requirement already satisfied: pillow in .\venv\Lib\site-packages (12.3.0)
Requirement already satisfied: winocr in .\venv\Lib\site-packages (0.0.15)
Requirement already satisfied: pytest in .\venv\Lib\site-packages (9.1.1)
Requirement already satisfied: winrt-windows-foundation-collections in .\venv\Lib\site-packages (from winocr) (3.2.1)
Requirement already satisfied: winrt-windows-foundation in .\venv\Lib\site-packages (from winocr) (3.2.1)
Requirement already satisfied: winrt-windows-globalization in .\venv\Lib\site-packages (from winocr) (3.2.1)
Requirement already satisfied: winrt-windows-graphics-imaging in .\venv\Lib\site-packages (from winocr) (3.2.1)
Requirement already satisfied: winrt-windows-media-ocr in .\venv\Lib\site-packages (from winocr) (3.2.1)
Requirement already satisfied: winrt-windows-storage-streams in .\venv\Lib\site-packages (from winocr) (3.2.1)
Requirement already satisfied: colorama>=0.4 in .\venv\Lib\site-packages (from pytest) (0.4.6)
Requirement already satisfied: iniconfig>=1.0.1 in .\venv\Lib\site-packages (from pytest) (2.3.0)
Requirement already satisfied: packaging>=22 in .\venv\Lib\site-packages (from pytest) (26.3)
Requirement already satisfied: pluggy<2,>=1.5 in .\venv\Lib\site-packages (from pytest) (1.6.0)
Requirement already satisfied: pygments>=2.7.2 in .\venv\Lib\site-packages (from pytest) (2.21.0)
Requirement already satisfied: winrt-runtime~=3.2.1.0 in .\venv\Lib\site-packages (from winrt-windows-foundation->winocr) (3.2.1)
Requirement already satisfied: typing_extensions>=4.12.2 in .\venv\Lib\site-packages (from winrt-runtime~=3.2.1.0->winrt-windows-foundation->winocr) (4.16.0)
[exit 0]

$ D:/cc-scrub-proof/venv/Scripts/python.exe -m pip list
Package                              Version
------------------------------------ -------
colorama                             0.4.6
iniconfig                            2.3.0
packaging                            26.3
pillow                               12.3.0
pip                                  26.2.1
pluggy                               1.6.0
Pygments                             2.21.0
pytest                               9.1.1
setuptools                           65.5.0
typing_extensions                    4.16.0
winocr                               0.0.15
winrt-runtime                        3.2.1
winrt-Windows.Foundation             3.2.1
winrt-Windows.Foundation.Collections 3.2.1
winrt-Windows.Globalization          3.2.1
winrt-Windows.Graphics.Imaging       3.2.1
winrt-Windows.Media.Ocr              3.2.1
winrt-Windows.Storage.Streams        3.2.1
[exit 0]

=== 4. draw the synthetic samples ===

$ D:/cc-scrub-proof/venv/Scripts/python.exe gen_samples.py D:/cc-scrub-proof/samples
cc-scrub sample generator
  WROTE D:/cc-scrub-proof/samples\sample-normal.png (900x260)
  WROTE D:/cc-scrub-proof/samples\sample-small-ui.png (900x220)
  WROTE D:/cc-scrub-proof/samples\sample-glyph.png (900x220)
  WROTE D:/cc-scrub-proof/samples\sample-notext.png (640x400)
Planted terms: example@example.com, myorg/secret-repo, internal-hostname.local
[exit 0]

=== 5. the denylist used for the proof (the shipped example, copied) ===

$ cat D:/cc-scrub-proof/terms.txt
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

$ D:/cc-scrub-proof/venv/Scripts/python.exe main.py D:/cc-scrub-proof/samples/sample-normal.png --check-only --terms-file D:/cc-scrub-proof/terms.txt
cc-scrub
  ocr engine : Windows OCR (winocr / WinRT)
  terms file : D:/cc-scrub-proof/terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : check-only

IMAGE D:/cc-scrub-proof/samples/sample-normal.png
  size 900x260  scales 1,2,3  fold on
  ocr scale 1: 20 words in 5 lines
  ocr scale 2: 20 words in 5 lines
  ocr scale 3: 20 words in 5 lines
  HITS: 1
    term='example@example.com' rect=(x=182 y=122 w=213 h=20) scales=1,2,3 ocr_line='Contact address: example@example.com'
  CHECK-ONLY: 1 hit(s) present, image NOT modified.

SUMMARY: 1 image(s) processed, 0 clean, 1 failed.
  FAILED D:/cc-scrub-proof/samples/sample-normal.png
[exit 1]

$ D:/cc-scrub-proof/venv/Scripts/python.exe main.py D:/cc-scrub-proof/samples/sample-small-ui.png --check-only --terms-file D:/cc-scrub-proof/terms.txt
cc-scrub
  ocr engine : Windows OCR (winocr / WinRT)
  terms file : D:/cc-scrub-proof/terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : check-only

IMAGE D:/cc-scrub-proof/samples/sample-small-ui.png
  size 900x220  scales 1,2,3  fold on
  ocr scale 1: 17 words in 6 lines
  ocr scale 2: 18 words in 6 lines
  ocr scale 3: 18 words in 6 lines
  HITS: 1
    term='myorg/secret-repo' rect=(x=41 y=78 w=84 h=10) scales=2,3 ocr_line='repo myorg/secret-repo'
  CHECK-ONLY: 1 hit(s) present, image NOT modified.

SUMMARY: 1 image(s) processed, 0 clean, 1 failed.
  FAILED D:/cc-scrub-proof/samples/sample-small-ui.png
[exit 1]

$ D:/cc-scrub-proof/venv/Scripts/python.exe main.py D:/cc-scrub-proof/samples/sample-glyph.png --check-only --terms-file D:/cc-scrub-proof/terms.txt
cc-scrub
  ocr engine : Windows OCR (winocr / WinRT)
  terms file : D:/cc-scrub-proof/terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : check-only

IMAGE D:/cc-scrub-proof/samples/sample-glyph.png
  size 900x220  scales 1,2,3  fold on
  ocr scale 1: 17 words in 4 lines
  ocr scale 2: 18 words in 4 lines
  ocr scale 3: 17 words in 4 lines
  HITS: 1
    term='internal-hostname.local' rect=(x=18 y=72 w=233 h=10) scales=2,3 ocr_line='https:/ftnternal-hostname.local/status/session'
  CHECK-ONLY: 1 hit(s) present, image NOT modified.

SUMMARY: 1 image(s) processed, 0 clean, 1 failed.
  FAILED D:/cc-scrub-proof/samples/sample-glyph.png
[exit 1]

=== 7. the same glyph case with folding turned off - it is missed ===

$ D:/cc-scrub-proof/venv/Scripts/python.exe main.py D:/cc-scrub-proof/samples/sample-glyph.png --check-only --no-fold --terms-file D:/cc-scrub-proof/terms.txt
cc-scrub
  ocr engine : Windows OCR (winocr / WinRT)
  terms file : D:/cc-scrub-proof/terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : check-only

IMAGE D:/cc-scrub-proof/samples/sample-glyph.png
  size 900x220  scales 1,2,3  fold off
  ocr scale 1: 17 words in 4 lines
  ocr scale 2: 18 words in 4 lines
  ocr scale 3: 17 words in 4 lines
  HITS: 0
  CHECK-ONLY: no hits against 3 terms over 52 OCR words.

SUMMARY: 1 image(s) processed, 1 clean, 0 failed.
[exit 0]

=== 8. an image with no text at all is a broken read, not a clean image ===

$ D:/cc-scrub-proof/venv/Scripts/python.exe main.py D:/cc-scrub-proof/samples/sample-notext.png --check-only --terms-file D:/cc-scrub-proof/terms.txt
FATAL: OCR read ZERO words from D:/cc-scrub-proof/samples/sample-notext.png across scales 1,2,3. That is a broken read, not a clean image. Refusing to call this scrubbed.
cc-scrub
  ocr engine : Windows OCR (winocr / WinRT)
  terms file : D:/cc-scrub-proof/terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : check-only

IMAGE D:/cc-scrub-proof/samples/sample-notext.png
  size 640x400  scales 1,2,3  fold on
  ocr scale 1: 0 words in 0 lines
  ocr scale 2: 0 words in 0 lines
  ocr scale 3: 0 words in 0 lines
[exit 2]

=== 9. scrub each sample: candidate, verify, then publish ===

$ D:/cc-scrub-proof/venv/Scripts/python.exe main.py D:/cc-scrub-proof/samples/sample-normal.png -o D:/cc-scrub-proof/out --terms-file D:/cc-scrub-proof/terms.txt
FATAL: output D:/cc-scrub-proof/out\sample-normal-scrubbed.png already exists. Refusing to replace it - it may be an output that has already passed verification. Delete it, point -o somewhere else, or pass --force to replace it.
cc-scrub
  ocr engine : Windows OCR (winocr / WinRT)
  terms file : D:/cc-scrub-proof/terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : blur

IMAGE D:/cc-scrub-proof/samples/sample-normal.png
  size 900x260  scales 1,2,3  fold on
  ocr scale 1: 20 words in 5 lines
  ocr scale 2: 20 words in 5 lines
  ocr scale 3: 20 words in 5 lines
  HITS: 1
    term='example@example.com' rect=(x=182 y=122 w=213 h=20) scales=1,2,3 ocr_line='Contact address: example@example.com'
[exit 2]

$ D:/cc-scrub-proof/venv/Scripts/python.exe main.py D:/cc-scrub-proof/samples/sample-small-ui.png -o D:/cc-scrub-proof/out --terms-file D:/cc-scrub-proof/terms.txt
FATAL: output D:/cc-scrub-proof/out\sample-small-ui-scrubbed.png already exists. Refusing to replace it - it may be an output that has already passed verification. Delete it, point -o somewhere else, or pass --force to replace it.
cc-scrub
  ocr engine : Windows OCR (winocr / WinRT)
  terms file : D:/cc-scrub-proof/terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : blur

IMAGE D:/cc-scrub-proof/samples/sample-small-ui.png
  size 900x220  scales 1,2,3  fold on
  ocr scale 1: 17 words in 6 lines
  ocr scale 2: 18 words in 6 lines
  ocr scale 3: 18 words in 6 lines
  HITS: 1
    term='myorg/secret-repo' rect=(x=41 y=78 w=84 h=10) scales=2,3 ocr_line='repo myorg/secret-repo'
[exit 2]

$ D:/cc-scrub-proof/venv/Scripts/python.exe main.py D:/cc-scrub-proof/samples/sample-glyph.png -o D:/cc-scrub-proof/out --terms-file D:/cc-scrub-proof/terms.txt
FATAL: output D:/cc-scrub-proof/out\sample-glyph-scrubbed.png already exists. Refusing to replace it - it may be an output that has already passed verification. Delete it, point -o somewhere else, or pass --force to replace it.
cc-scrub
  ocr engine : Windows OCR (winocr / WinRT)
  terms file : D:/cc-scrub-proof/terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : blur

IMAGE D:/cc-scrub-proof/samples/sample-glyph.png
  size 900x220  scales 1,2,3  fold on
  ocr scale 1: 17 words in 4 lines
  ocr scale 2: 18 words in 4 lines
  ocr scale 3: 17 words in 4 lines
  HITS: 1
    term='internal-hostname.local' rect=(x=18 y=72 w=233 h=10) scales=2,3 ocr_line='https:/ftnternal-hostname.local/status/session'
[exit 2]

=== 10. nothing unverified is left lying about ===
(the output directory holds three published outputs and no candidates)

$ ls -1 D:/cc-scrub-proof/out
sample-glyph-scrubbed.png
sample-normal-scrubbed.png
sample-small-ui-scrubbed.png
[exit 0]

=== 11. an existing output is not replaced by accident ===

$ D:/cc-scrub-proof/venv/Scripts/python.exe main.py D:/cc-scrub-proof/samples/sample-normal.png -o D:/cc-scrub-proof/out --terms-file D:/cc-scrub-proof/terms.txt
FATAL: output D:/cc-scrub-proof/out\sample-normal-scrubbed.png already exists. Refusing to replace it - it may be an output that has already passed verification. Delete it, point -o somewhere else, or pass --force to replace it.
cc-scrub
  ocr engine : Windows OCR (winocr / WinRT)
  terms file : D:/cc-scrub-proof/terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : blur

IMAGE D:/cc-scrub-proof/samples/sample-normal.png
  size 900x260  scales 1,2,3  fold on
  ocr scale 1: 20 words in 5 lines
  ocr scale 2: 20 words in 5 lines
  ocr scale 3: 20 words in 5 lines
  HITS: 1
    term='example@example.com' rect=(x=182 y=122 w=213 h=20) scales=1,2,3 ocr_line='Contact address: example@example.com'
[exit 2]

=== 12. --force is how you say you meant it ===

$ D:/cc-scrub-proof/venv/Scripts/python.exe main.py D:/cc-scrub-proof/samples/sample-normal.png -o D:/cc-scrub-proof/out --force --terms-file D:/cc-scrub-proof/terms.txt
cc-scrub
  ocr engine : Windows OCR (winocr / WinRT)
  terms file : D:/cc-scrub-proof/terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : blur

IMAGE D:/cc-scrub-proof/samples/sample-normal.png
  size 900x260  scales 1,2,3  fold on
  ocr scale 1: 20 words in 5 lines
  ocr scale 2: 20 words in 5 lines
  ocr scale 3: 20 words in 5 lines
  HITS: 1
    term='example@example.com' rect=(x=182 y=122 w=213 h=20) scales=1,2,3 ocr_line='Contact address: example@example.com'
  CANDIDATE D:\cc-scrub-proof\out\sample-normal-scrubbed.png.0vlhz4t2.tmp (mode=blur pad=4)
  VERIFY: re-reading D:\cc-scrub-proof\out\sample-normal-scrubbed.png.0vlhz4t2.tmp
    verify scale 1: 19 words in 5 lines
    verify scale 2: 19 words in 5 lines
    verify scale 3: 19 words in 5 lines
  WROTE D:/cc-scrub-proof/out\sample-normal-scrubbed.png (mode=blur pad=4)
  VERIFY PASSED: 1 hit(s) found, 1 region(s) redacted, verify OCR read 57 words in the output and 0 denylist hit(s) remain.

SUMMARY: 1 image(s) processed, 1 clean, 0 failed.
[exit 0]

=== 13. the input is never a legal destination, in any spelling ===

$ D:/cc-scrub-proof/venv/Scripts/python.exe main.py D:/cc-scrub-proof/shot.png -o D:/cc-scrub-proof/SHOT.PNG --force --terms-file D:/cc-scrub-proof/terms.txt
FATAL: refusing to overwrite the input image D:/cc-scrub-proof/shot.png (the output path D:/cc-scrub-proof/SHOT.PNG is the same file)
cc-scrub
  ocr engine : Windows OCR (winocr / WinRT)
  terms file : D:/cc-scrub-proof/terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : blur

IMAGE D:/cc-scrub-proof/shot.png
  size 900x260  scales 1,2,3  fold on
  ocr scale 1: 20 words in 5 lines
  ocr scale 2: 20 words in 5 lines
  ocr scale 3: 20 words in 5 lines
  HITS: 1
    term='example@example.com' rect=(x=182 y=122 w=213 h=20) scales=1,2,3 ocr_line='Contact address: example@example.com'
[exit 2]

=== 14. two inputs that would land on one output file ===
(stems differing only in case; the destination directory is asked
 whether it treats the two spellings as one file, and it does here)

$ D:/cc-scrub-proof/venv/Scripts/python.exe main.py D:/cc-scrub-proof/collide --terms-file D:/cc-scrub-proof/terms.txt
FATAL: D:/cc-scrub-proof/collide\Shot.png and D:/cc-scrub-proof/collide\shot.jpg would both be written to D:\cc-scrub-proof\collide\shot-scrubbed.png. Rename one input, give -o separate directories, or scrub them in separate runs.
[exit 2]

=== 15. a read over the megapixel budget is refused before it is tried ===
(900x260 at scale 8 is 7200x2080 - inside the engine's side limit, and
 14,976,000 pixels, which is 15.0 megapixels and over a 1 megapixel budget)

$ D:/cc-scrub-proof/venv/Scripts/python.exe main.py D:/cc-scrub-proof/samples/sample-normal.png --check-only --scales 8 --max-megapixels 1 --terms-file D:/cc-scrub-proof/terms.txt
FATAL: D:/cc-scrub-proof/samples/sample-normal.png is 900x260; at scale 8 that is 7200x2080, 15.0 megapixels, over the 1 megapixel budget for one read. Use a smaller --scales value, a smaller image, or raise --max-megapixels if this machine has the memory for it.
cc-scrub
  ocr engine : Windows OCR (winocr / WinRT)
  terms file : D:/cc-scrub-proof/terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : check-only

IMAGE D:/cc-scrub-proof/samples/sample-normal.png
[exit 2]

=== 16. re-check every published output - nothing left to find ===

$ D:/cc-scrub-proof/venv/Scripts/python.exe main.py D:/cc-scrub-proof/out/sample-normal-scrubbed.png --check-only --terms-file D:/cc-scrub-proof/terms.txt
cc-scrub
  ocr engine : Windows OCR (winocr / WinRT)
  terms file : D:/cc-scrub-proof/terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : check-only

IMAGE D:/cc-scrub-proof/out/sample-normal-scrubbed.png
  size 900x260  scales 1,2,3  fold on
  ocr scale 1: 19 words in 5 lines
  ocr scale 2: 19 words in 5 lines
  ocr scale 3: 19 words in 5 lines
  HITS: 0
  CHECK-ONLY: no hits against 3 terms over 57 OCR words.

SUMMARY: 1 image(s) processed, 1 clean, 0 failed.
[exit 0]

$ D:/cc-scrub-proof/venv/Scripts/python.exe main.py D:/cc-scrub-proof/out/sample-small-ui-scrubbed.png --check-only --terms-file D:/cc-scrub-proof/terms.txt
cc-scrub
  ocr engine : Windows OCR (winocr / WinRT)
  terms file : D:/cc-scrub-proof/terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : check-only

IMAGE D:/cc-scrub-proof/out/sample-small-ui-scrubbed.png
  size 900x220  scales 1,2,3  fold on
  ocr scale 1: 16 words in 5 lines
  ocr scale 2: 17 words in 6 lines
  ocr scale 3: 17 words in 6 lines
  HITS: 0
  CHECK-ONLY: no hits against 3 terms over 50 OCR words.

SUMMARY: 1 image(s) processed, 1 clean, 0 failed.
[exit 0]

$ D:/cc-scrub-proof/venv/Scripts/python.exe main.py D:/cc-scrub-proof/out/sample-glyph-scrubbed.png --check-only --terms-file D:/cc-scrub-proof/terms.txt
cc-scrub
  ocr engine : Windows OCR (winocr / WinRT)
  terms file : D:/cc-scrub-proof/terms.txt (3 terms)
    - example@example.com
    - myorg/secret-repo
    - internal-hostname.local
  images     : 1
  mode       : check-only

IMAGE D:/cc-scrub-proof/out/sample-glyph-scrubbed.png
  size 900x220  scales 1,2,3  fold on
  ocr scale 1: 16 words in 3 lines
  ocr scale 2: 17 words in 3 lines
  ocr scale 3: 16 words in 3 lines
  HITS: 0
  CHECK-ONLY: no hits against 3 terms over 49 OCR words.

SUMMARY: 1 image(s) processed, 1 clean, 0 failed.
[exit 0]

=== 17. a folder holding only outputs is an empty input set ===
(the *-scrubbed suffix is skipped by design, which is what makes
 re-running a folder safe - the tool says so rather than doing nothing)

$ D:/cc-scrub-proof/venv/Scripts/python.exe main.py D:/cc-scrub-proof/out --check-only --terms-file D:/cc-scrub-proof/terms.txt
FATAL: no images found in directory D:/cc-scrub-proof/out
[exit 2]

=== 18. the full test suite, in the same clean environment ===
(-rs so that anything SKIPPED is printed with its reason, because a
 skip that is not shown reads exactly like a pass)

$ D:/cc-scrub-proof/venv/Scripts/python.exe -m pytest tests/ -v -rs
============================= test session starts =============================
platform win32 -- Python 3.11.6, pytest-9.1.1, pluggy-1.6.0 -- D:\cc-scrub-proof\venv\Scripts\python.exe
cachedir: .pytest_cache
rootdir: D:\cc-scrub-proof\cc-scrub
configfile: pyproject.toml
collecting ... collected 56 items

tests/test_cc_scrub.py::test_generator_writes_all_four_samples PASSED    [  1%]
tests/test_cc_scrub.py::test_check_only_finds_the_planted_email_with_coordinates PASSED [  3%]
tests/test_cc_scrub.py::test_check_only_finds_small_grey_ui_text_only_after_upscaling PASSED [  5%]
tests/test_cc_scrub.py::test_glyph_confusion_is_caught_by_folding_and_missed_without_it PASSED [  7%]
tests/test_cc_scrub.py::test_scrub_redacts_and_the_verify_pass_proves_it[sample-normal.png-example@example.com] PASSED [  8%]
tests/test_cc_scrub.py::test_scrub_redacts_and_the_verify_pass_proves_it[sample-small-ui.png-myorg/secret-repo] PASSED [ 10%]
tests/test_cc_scrub.py::test_scrub_redacts_and_the_verify_pass_proves_it[sample-glyph.png-internal-hostname.local] PASSED [ 12%]
tests/test_cc_scrub.py::test_the_scrubbed_output_is_clean_when_checked_again PASSED [ 14%]
tests/test_cc_scrub.py::test_a_directory_run_skips_files_already_scrubbed PASSED [ 16%]
tests/test_cc_scrub.py::test_an_image_with_no_text_is_a_broken_read_not_a_clean_image PASSED [ 17%]
tests/test_cc_scrub.py::test_a_verify_read_of_zero_words_publishes_nothing_and_exits_two PASSED [ 19%]
tests/test_cc_scrub.py::test_a_term_surviving_in_the_output_exits_one_and_publishes_nothing PASSED [ 21%]
tests/test_cc_scrub.py::test_a_passing_scripted_run_publishes_the_candidate PASSED [ 23%]
tests/test_cc_scrub.py::test_folding_finds_a_misread_term_and_no_fold_misses_it PASSED [ 25%]
tests/test_cc_scrub.py::test_a_term_split_across_adjacent_words_on_one_line_is_joined PASSED [ 26%]
tests/test_cc_scrub.py::test_a_term_split_across_two_lines_is_not_joined PASSED [ 28%]
tests/test_cc_scrub.py::test_a_scaled_read_maps_back_to_native_coordinates_exactly PASSED [ 30%]
tests/test_cc_scrub.py::test_a_read_over_the_megapixel_budget_is_refused_before_it_is_attempted PASSED [ 32%]
tests/test_cc_scrub.py::test_the_megapixel_budget_must_be_at_least_one PASSED [ 33%]
tests/test_cc_scrub.py::test_an_image_too_big_for_the_engine_is_refused_before_it_is_read PASSED [ 35%]
tests/test_cc_scrub.py::test_refuses_to_overwrite_the_input_image PASSED [ 37%]
tests/test_cc_scrub.py::test_refuses_to_overwrite_the_input_addressed_in_a_different_case PASSED [ 39%]
tests/test_cc_scrub.py::test_refuses_to_overwrite_the_input_reached_through_a_hard_link PASSED [ 41%]
tests/test_cc_scrub.py::test_a_missing_terms_file_names_the_example_to_copy PASSED [ 42%]
tests/test_cc_scrub.py::test_the_shipped_example_denylist_parses PASSED  [ 44%]
tests/test_cc_scrub.py::test_bad_scales_are_a_usage_error PASSED         [ 46%]
tests/test_cc_scrub.py::test_word_ranges_count_utf16_code_units_not_code_points PASSED [ 48%]
tests/test_cc_scrub.py::test_word_ranges_match_code_points_for_plain_text PASSED [ 50%]
tests/test_cc_scrub.py::test_a_denylist_term_that_can_never_match_is_refused PASSED [ 51%]
tests/test_cc_scrub.py::test_term_validation_follows_the_normalisation_actually_in_force PASSED [ 53%]
tests/test_cc_scrub.py::test_two_inputs_that_would_share_one_output_are_refused PASSED [ 55%]
tests/test_cc_scrub.py::test_two_inputs_whose_stems_differ_only_in_case_are_refused PASSED [ 57%]
tests/test_cc_scrub.py::test_collision_detection_does_not_depend_on_the_host_normcase PASSED [ 58%]
tests/test_cc_scrub.py::test_the_case_probe_answers_from_the_filesystem_and_cleans_up PASSED [ 60%]
tests/test_cc_scrub.py::test_a_probe_readback_error_is_an_error_not_a_case_sensitive_answer PASSED [ 62%]
tests/test_cc_scrub.py::test_a_probe_that_cannot_be_removed_is_an_error_not_a_warning PASSED [ 64%]
tests/test_cc_scrub.py::test_a_genuinely_missing_swapped_name_is_the_case_sensitive_answer PASSED [ 66%]
tests/test_cc_scrub.py::test_the_megapixel_budget_is_decimal_megapixels_not_mebipixels PASSED [ 67%]
tests/test_cc_scrub.py::test_the_case_probe_refuses_to_guess_when_it_cannot_be_created PASSED [ 69%]
tests/test_cc_scrub.py::test_an_output_directory_that_cannot_be_created_exits_two PASSED [ 71%]
tests/test_cc_scrub.py::test_pad_box_grows_outwards_to_whole_pixels PASSED [ 73%]
tests/test_cc_scrub.py::test_pad_box_pads_and_clamps_to_the_image PASSED [ 75%]
tests/test_cc_scrub.py::test_normalise_drops_punctuation_and_case PASSED [ 76%]
tests/test_cc_scrub.py::test_normalise_folds_every_advertised_glyph_class PASSED [ 78%]
tests/test_cc_scrub.py::test_parse_scales_sorts_and_deduplicates PASSED  [ 80%]
tests/test_cc_scrub.py::test_parse_scales_rejects_rubbish PASSED         [ 82%]
tests/test_cc_scrub.py::test_load_terms_ignores_comments_and_blank_lines PASSED [ 83%]
tests/test_cc_scrub.py::test_load_terms_rejects_an_empty_denylist PASSED [ 85%]
tests/test_cc_scrub.py::test_merge_hits_unions_overlapping_rectangles_of_one_term PASSED [ 87%]
tests/test_cc_scrub.py::test_merge_hits_keeps_different_terms_apart PASSED [ 89%]
tests/test_cc_scrub.py::test_gather_inputs_skips_already_scrubbed_files PASSED [ 91%]
tests/test_cc_scrub.py::test_gather_inputs_rejects_a_missing_path PASSED [ 92%]
tests/test_cc_scrub.py::test_is_same_file_sees_through_a_case_variant_of_an_existing_path PASSED [ 94%]
tests/test_cc_scrub.py::test_is_same_file_compares_canonically_when_the_target_is_not_created_yet PASSED [ 96%]
tests/test_cc_scrub.py::test_is_same_file_says_no_to_two_genuinely_different_files PASSED [ 98%]
tests/test_cc_scrub.py::test_output_path_defaults_to_the_scrubbed_name_beside_the_input PASSED [100%]

============================= 56 passed in 2.73s ==============================
[exit 0]
```
