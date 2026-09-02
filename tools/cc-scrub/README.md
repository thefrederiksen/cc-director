# cc-scrub

Finds denylisted text in screenshots and covers it, so a screenshot can go
into a public video, a documentation site or a website without a manual pixel
hunt.

**How strongly it is covered depends on the mode, and the default is the
weaker one.** `--blur` removes the original pixel values but leaves a
low-frequency average of the region, which is attackable; `--patch` paints an
opaque colour over the whole rectangle and leaves no signal from the covered
pixels at all. Use `--patch` for anything leaving the organisation, and read
[How the redaction works](#how-the-redaction-works) before deciding that a
blurred screenshot is safe to publish.

Pure Python. Every dependency is a pip wheel. The text recognizer is the
operating system's own, reached through one seam. No external executable is
installed, required or looked for - in particular this tool never calls
tesseract, even on a machine where one happens to be present - and it
launches no subprocess at all.

## Install

On Windows:

```
python -m pip install Pillow winocr
```

`winocr` is a pip wheel that wraps the OCR engine built into Windows through
WinRT. There is no installer and no .exe. Proved on Python 3.11.6 with
Pillow 12.3.0 and winocr 0.0.15; see [PROOF-windows.md](PROOF-windows.md) for
the full clean-install transcript.

On macOS:

```
python -m pip install Pillow pyobjc-framework-Vision
```

`pyobjc-framework-Vision` is a pip wheel that binds the Vision text
recognizer built into macOS, and it pulls in the Quartz and Foundation
bindings it needs. There is no installer and no external engine. Proved on
Python 3.14.6 with Pillow 12.3.0 and pyobjc 12.2.2; see
[PROOF-macos.md](PROOF-macos.md) for the full clean-install transcript.

## Use

```
python main.py <image-or-dir> [-o <out>] [--terms-file terms.txt]
               [--check-only] [--patch|--blur] [--force]
               [--pad N] [--scales 1,2,3] [--lang en] [--no-fold]
               [--max-megapixels N]
```

Common runs:

```
python main.py shot.png                  # scrub one image next to itself
python main.py shots -o out/             # scrub a whole folder into out/
python main.py shots --check-only        # lint a folder, change nothing
python main.py shot.png --patch          # opaque patch instead of a blur
python main.py shots -o out/ --force     # replace outputs already in out/
```

### Where the output goes

- No `-o`: beside the input, as `<stem>-scrubbed.png`.
- `-o` naming a directory, or more than one input: into that directory, as
  `<stem>-scrubbed.png`.
- `-o` naming a file with exactly one input: **that file, verbatim** - the
  name and the suffix are yours. The file is still written as PNG data.

`-o` pointed at a path that does not exist yet is treated as an output *file*
name, which is what you want for a single image and not what you want for a
folder. Create the folder first.

When there are no hits, no output file is written at all.

### What the tool refuses to write over

- **The input, always.** If the output resolves to the input file the run
  stops. That check asks the operating system whether the two paths are the
  same file, so a different letter case, a hard link, a symbolic link or a
  junction is caught, not just an identical spelling. `--force` does not
  lift this - it would destroy the only unredacted copy.
- **An existing output, unless you say so.** An existing `*-scrubbed.png` may
  be one that has already passed verification, so replacing it is a decision
  you make on purpose. Delete it, point `-o` elsewhere, or pass `--force`.
- **Two inputs that want one output.** Names are built from the input's stem,
  so `shot.png` and `shot.jpg` both want `shot-scrubbed.png` - and wherever
  the volume treats two spellings as one file, so do `Shot.png` and
  `shot.jpg`. Every destination is worked out before any image is read, and a
  collision stops the run rather than letting the second result quietly
  replace the first.

  Whether two spellings are one file is answered by the **destination
  directory**, probed once per directory: a probe file is created under a
  mixed-case name, looked up with its case swapped, and removed again. It is
  not answered by `os.path.normcase`, which is the *host's* rule - it
  lower-cases on Windows and is the identity function on POSIX, so a lexical
  comparison would silently stop detecting anything on a case-insensitive
  volume under a POSIX host, which is the normal state of a Mac. A probe that
  cannot be created or read back is exit 2, never an assumption.

Directory input picks up `.png`, `.jpg`, `.jpeg`, `.bmp` and skips anything
already named `*-scrubbed.*`, so re-running a folder picks up the same inputs
each time (and then refuses to replace the outputs unless you pass `--force`).

### Exit codes

| code | meaning |
| ---- | ------- |
| 0    | work done and proved, or `--check-only` found nothing |
| 1    | denylist hits remain readable in an output, or `--check-only` found hits |
| 2    | broken instrument or usage error - OCR unavailable, unreadable image, zero text read, an image too big for the engine or over the megapixel budget, an output that already exists, two inputs colliding on one output, an unmatchable denylist term, a failed write, bad arguments |

`--check-only` is the linter: it prints every hit with its coordinates,
changes nothing, and exits 1 if there is anything to scrub. Point it at a
screenshots folder before publishing.

## The denylist

`terms.txt` in this folder, one term per line, `#` starts a comment.
Matching is case-insensitive and by substring, so `myorg` also hits
`myorg/secret-repo`. Override the location with `--terms-file`.

**`terms.txt` is your own configuration and is never committed.** A real
denylist is a list of the exact strings you are trying to keep out of public
screenshots, so committing it publishes them. This folder's `.gitignore`
excludes it. What ships instead is
[`terms.example.txt`](terms.example.txt), whose entries
(`example@example.com`, `myorg/secret-repo`, `internal-hostname.local`) are
deliberately fake. Copy it to `terms.txt` and put your terms in the copy.

A term with nothing left after normalisation - all punctuation, or all
non-ASCII - is an error, not a warning. Such a term would be counted in the
banner and then skipped in the matcher, so a run could report "no hits
against 6 terms" having actually checked five. The check is made under the
normalisation actually in force, because folding changes which characters
survive: `!!!` folds onto `lll` and can match, and with `--no-fold` it cannot.

## How matching works

1. **OCR at several scales.** The image is read once per factor in
   `--scales` (default `1,2,3`), enlarged with LANCZOS before each read and
   every rectangle divided back down afterwards. Small grey interface text
   around 10 px is unreadable to the engine at native resolution: in the
   proof run the line `repo myorg/secret-repo` collapses to a three-character
   fragment at scale 1 and only resolves at 2 and 3, so the hit is reported
   as `scales=2,3`. Every scale always runs and the hits are unioned. This is
   not a fallback chain - different scales misread the same text in
   different ways, and the union is what makes detection reliable. Every
   scale, scale 1 included, is measured before the image is decoded against
   two limits: the engine's limit on the longest side, and a budget on the
   pixels one scaled read may ask for (`--max-megapixels`, default 192).
   Passing the side limit says nothing about the allocation - a square input
   just under a 16383 px side limit is about 268 megapixels, roughly 800 MB
   of RGB data for the scaled copy alone - so an image too big to read is
   refused by name rather than taken as far as the allocation. The default
   allows the sizes people actually scrub: a 4K screenshot at scale 3 is 75
   megapixels, a 5K one is 132.
2. **Line joining.** Each OCR line is concatenated into one string with a map
   back to the word each character came from, so a term split across
   adjacent OCR words still matches and the covering words' rectangles are
   unioned into one hit rectangle. Joining happens within a line only.
3. **Normalisation.** Both the term and the OCR text are lowercased, then
   folded (step 4), then stripped of everything that is not `a-z0-9`. The
   stripping is what absorbs the punctuation and the spaces the engine
   injects into a URL - it returns `https : / /example. ai/path/session_011`
   for a line whose pixels read `https://example.ai/path/session_011`.
4. **Glyph folding** (on by default, `--no-fold` turns it off). Characters
   that share a shape are folded to one representative on both sides:
   `o0`, `li1tj!|`, `s5`, `z2`, `b8`, `g9`. Small anti-aliased interface text
   provokes exactly these confusions. A URL like `example.ai` can come back
   as `exampte.ai` - the engine misreads the `l` as a `t` - and without
   folding that URL is missed entirely. The proof run shows the same thing on
   a host name: `internal-hostname.local` is read as
   `https:/ftnternal-hostname.local/...`, found with folding, reported as
   `HITS: 0` with `--no-fold`.

   Folding happens *before* the non-alphanumeric characters are dropped, and
   the order is load bearing. Two members of the `l` class, `!` and `|`, are
   not alphanumeric: filtering first threw them away instead of folding them,
   so two of the six advertised folds did nothing at all and an engine that
   read `internal` as `!nternal` produced a false negative.

   Folding deliberately over-matches in the safe direction: the worst case is
   a few extra blurred pixels, and every hit is printed with its coordinates
   and its source line so it can be checked by eye.
5. **Merging.** Overlapping rectangles for the same term collapse into one,
   so a hit seen at three scales is redacted once.

## How the redaction works

Each hit rectangle arrives as floats. Every edge is first grown outwards to a
whole pixel - floor the left and top, ceil the right and bottom - so no part
of a fractional rectangle is left uncovered, and only then is `--pad` applied
(default 4 pixels). Then:

- **`--blur`** (default) mosaics the region down to a handful of cells and
  scales it back with NEAREST, then smooths the blocks with a Gaussian pass
  at radius `max(6, height/2)`. The original pixel values are not present in
  the output - the downsampling discards them. **What remains is a
  low-frequency average of the covered region**, which is not nothing:
  recovering text from a mosaic is a documented attack when the alphabet is
  small, and a Gaussian blur is a linear low-pass filter, not an eraser. This
  mode is not proof against a determined recovery attempt.
- **`--patch`** fills the **whole** hit rectangle with the region's
  per-channel median colour, which is its background colour, so the redaction
  reads as a deliberate one rather than a black bar. Because it paints an
  opaque solid colour over every pixel of the box, **no signal from the
  covered pixels survives at all.**

  It is a plain rectangle and not a rounded one, deliberately. It was drawn
  rounded, and the corners were therefore never painted - which made the mode
  documented as the safe one the only one that provably left original pixels
  inside the hit rectangle. Coverage beats the nicer shape, and a pixel-level
  test now plants pixels in all four corners and requires that none survives.

**For anything leaving the organisation, use `--patch`.** It is the only one
of the two that leaves nothing behind to attack. `--blur` remains the default
because it is the right choice for the common case of tidying an internal
screenshot, where the redaction should still read as part of the picture -
but the choice of default is a judgement about the common case, not a claim
that the two modes are equally strong.

## How the verify pass works

The verify pass is mandatory and it is not a re-read of anything held in
memory. The redacted image is written to a **candidate file on disk**, beside
where the output will go; the tool then opens that file, runs the same
multi-scale OCR and the same matcher over it, and only then decides.

Nothing unverified ever appears under the authoritative `*-scrubbed` name,
and any existing output is left exactly as it was - a run that fails is not
allowed to destroy a result that passed. Writing straight to the final name
and checking afterwards gets both of those wrong.

Publishing is one atomic operation on the same volume, and which one depends
on `--force`:

- **Without `--force`** the candidate is hard-linked onto the destination
  name, which fails rather than replacing if anything is there. That matters
  because the "does an output already exist" check runs *before* the
  candidate is written, and everything since - the write and the whole
  verification - is a window in which another process can create that file. A
  correct answer at inspection time does not make the write safe, so the
  write itself carries the guarantee. On a volume with no hard links this is
  a loud exit 2, never a silent replace.
- **With `--force`** it is a rename, which replaces. That is what `--force`
  asks for.

**Removing the candidate after a failure is best effort.** The tool attempts
it and, if the removal fails, prints a `WARNING` naming the file it left
behind and continues - because an exception is usually already in flight at
that point (the verify failure that stopped the publish) and raising there
would replace the real reason with a housekeeping complaint. So a `*.tmp`
candidate can survive next to the output directory, and **a candidate from a
failed verify can still contain readable denylisted text.** If you see that
warning, delete the file it names.

The pass condition is a presence, never an absence:

```
VERIFY PASSED: 1 hit(s) found, 1 region(s) redacted, verify OCR read
57 words in the output and 0 denylist hit(s) remain.
```

Three ways it refuses to certify a run:

- **Any denylist term still readable in the output** - prints each surviving
  hit with coordinates, exit 1.
- **The original OCR read zero words** - that is a broken read, never a clean
  image, so the tool refuses to call it scrubbed. Exit 2.
- **The verify OCR read zero words from the candidate** - the instrument
  itself is broken and proves nothing. Exit 2.

In both failure cases nothing is published and the destination is untouched.
Removal of the candidate is best effort, as above.

### What the verify pass does not prove

The verify pass is run by the **same recognizer that found the text in the
first place**. That is true of every backend, on every platform, and it is a
property of the design rather than a defect in any one engine.

So a pass proves that *this engine*, at *the scales this run configured*,
can no longer read the term in the written file. It does not prove that no
trace of the text survives in the pixels. A different engine, a better one,
or the same one at a scale this run did not try, is not what was asked.

Three things follow:

- **The rectangle is the engine's own estimate of where the text is**, and
  nothing guarantees it is tight around the glyphs. `--pad` is the margin over
  it and it defaults to 4 pixels; `--pad 0` removes the only margin there is.
- **What is inside the rectangle is covered, and how well depends on the
  mode.** `--blur` removes the original pixel values but leaves a
  low-frequency average of the region, which is attackable. `--patch` leaves
  no signal from the covered pixels at all. See
  [How the redaction works](#how-the-redaction-works).
- **The verify pass cannot tell those two apart.** It asks the recognizer
  whether it can still read the term, and the recognizer says no to both. A
  pass is not a statement about how recoverable the covered pixels are.

There is deliberately no "safety margin" beyond `--pad`. A number nobody has
measured, added to look careful, would be a guess presented as a guarantee.

## The OCR seam

All the platform-specific code lives in [`src/ocr_backend.py`](src/ocr_backend.py)
and nowhere else. Everything above it - the multi-scale reads, the line
joining, the normalisation, the folding, the merging, the redaction, the
verify pass, the exit codes - is platform independent and does not know which
engine answered.

```
backend = get_backend()
words = backend.recognize(pil_image, lang)
```

`recognize` returns a flat list of dictionaries in reading order:

```
{"text": str, "x": float, "y": float, "w": float, "h": float, "line": int}
```

`x`, `y`, `w` and `h` are pixels in the coordinate space of the image handed
in, origin at the top left. `line` is the index of the recognized line the
word came from: words that share a `line` value are on one line, and that is
what the line-joining step above needs. It is not decorative - a backend that
gave every word a different `line` value would silently switch joining off
and start missing terms.

A backend also declares `name` (used in error messages) and
`max_image_dimension` (the longest side the engine accepts, which is what
bounds `--scales`).

`get_backend()` picks by platform and nothing else. There is no probing, no
trying one engine and then another, and no silent substitution: an operating
system whose recognizer this tool cannot drive is an error, not a degraded
mode.

## Per-operating-system notes

**Windows** - `WindowsOcrBackend`, the engine built into the operating
system, reached through the `winocr` pip wheel and WinRT. Implemented and
proved; see [PROOF-windows.md](PROOF-windows.md). The engine reports its own
installed recognizer languages and names them in the error if `--lang` does
not resolve. Its limit on the longest side is what `--scales` is bounded by,
and the tool says so by name rather than silently skipping a scale.

**macOS** - `MacVisionBackend`, the Vision text recognizer built into the
operating system (`VNRecognizeTextRequest`, accurate recognition level),
reached through the `pyobjc-framework-Vision` pip wheel. Implemented and
proved; see [PROOF-macos.md](PROOF-macos.md). The image goes to the
recognizer as an in-memory CGImage - no temporary file, no subprocess. The
recognizer reports rectangles normalized to 0..1 and measured from the
bottom-left corner; the backend converts them to the seam's top-left pixel
coordinates and asks the recognizer itself for each word's rectangle within
its line. Language correction is off on purpose: correction rewrites what
was read toward dictionary words, and the strings this tool hunts -
addresses, host names, repository paths - are exactly the ones a dictionary
does not hold. This engine reads small text noticeably better than the
Windows one: it reads the 11 px glyph sample cleanly at every scale, which
is why the unfolded-miss half of the glyph test asserts on Windows only.
`--lang` tags resolve against the recognizer's supported list (`en` names
`en-US`); a tag that names no supported language stops the run and prints
the full list. The longest side accepted is 16384 px.

**Anything else** - `get_backend()` raises, naming the platform. There is no
portable fallback engine and none will be added.

## Development

```
python gen_samples.py samples     # draw the synthetic test images
python -m pytest tests/ -q        # 68 tests
```

The integration tests drive the **real recognizer** over the generated
samples. On a platform with no backend yet they skip by platform name - never
by probing whether OCR happens to work, so a broken install on a supported
platform fails the suite instead of quietly passing it.

**One invariant to hold when changing this tool.** `os.path.exists`, `isfile`
and `isdir` return `False` for *every* error, and in this tool `False` is
routinely the answer that PERMITS an action - publish over that file, treat
those two paths as different files. So none of them are used: `stat_or_none`
is, and it treats only `FileNotFoundError` and `NotADirectoryError` as
genuine absences and raises on everything else. If you add a check, ask what
`False` causes there before you reach for `os.path.exists`.

A few arms need a volume on which two spellings name one file. That is
measured on the directory the test writes to, using the same probe the tool
uses, and never inferred from `sys.platform` - a Mac's default volume is
case-insensitive, so a platform gate skipped those arms exactly where they
would have run. Where such an arm skips, the reason names the directory and
says the answer was measured.

Alongside them the suite carries a `ScriptedBackend`: a deliberate test double
that implements the seam contract and returns reads written down in the test.
It is test tooling, not a runtime fallback - `get_backend()` cannot reach it
and it never ships. It exists because the verify pass's failure arms cannot be
reached with a real engine: no screenshot makes a recognizer read zero words
from a file it has just read words from, and none reliably leaves a redacted
term readable. Those arms are the ones that must never rot, so they are driven
directly - along with the exact scale-to-native coordinate mapping and the
adjacent-word joining, both of which are asserted as numbers rather than
inferred from a hit count.

## Known limits

- **A term split across two OCR *lines* is not matched.** Joining happens
  within a line only.
- **An inserted glyph defeats a match.** Folding handles a character read as
  the wrong character; it cannot handle a character the engine invents. A
  slash read as an `i` turns `myorg/secret-repo` into `myorgisecret-repo`,
  which normalises to one character more than the term and does not match.
- **Detection is OCR-bound.** If the engine cannot resolve the text at any
  configured scale, the tool cannot find it. Very small, very low-contrast or
  heavily-kerned text may need a higher `--scales` value; the ceiling is the
  engine's own limit on the longest side.
- **Glyph folding can over-match.** Short terms are the risk - a two or three
  character term after folding will hit far more than intended. `|` and `!`
  fold onto `l`, so a run of table borders or box drawing normalises to a run
  of `l`s. That is over-matching in the safe direction - the cost is a few
  extra blurred pixels, and every hit is printed - but keep terms specific,
  read the printed hits, and use `--no-fold` if an exact match is wanted.
- **`--check-only` is a linter, not a guarantee.** It reports what the OCR can
  see. It cannot report sensitive text the OCR cannot read, which is why the
  scrub path always re-reads its own output.
- **The verify pass uses the same engine that found the text**, at the scales
  the run configured, on every platform. It proves the term is no longer
  readable *by that engine at those scales* - not that no trace survives. The
  rectangle it redacts is that engine's own estimate of where the text is and
  is not guaranteed tight; `--pad` (default 4) is the only margin over it, and
  `--pad 0` removes it. See
  [What the verify pass does not prove](#what-the-verify-pass-does-not-prove).
- **`--blur` is not proof against a determined recovery attempt.** It removes
  the original pixel values but what remains is a low-frequency average of the
  covered region; depixelation attacks on text from a small alphabet are
  documented, and a Gaussian is a linear filter. Use `--patch` for anything
  leaving the organisation.
- **Case aliasing is the aliasing the output-collision check covers.** The
  destination directory is probed for it directly. Other ways a filesystem can
  make two names one file - Unicode normalisation, for one - are not probed
  for.
- **The engine is not deterministic across operating system versions.** The
  hit counts in `PROOF-windows.md` were produced on one machine; re-run the
  proof rather than assuming the numbers.
- **Not in the shipped tool bundle.** cc-scrub is a repository tool, run from
  source. It has no PyInstaller build and is not selected for the installer.

## Files

| file | what it is |
| ---- | ---------- |
| `main.py` | the entry point |
| `src/cli.py` | everything above the OCR seam |
| `src/ocr_backend.py` | the OCR seam and the per-platform backends |
| `gen_samples.py` | draws the four synthetic test images |
| `terms.example.txt` | the example denylist, all entries fake |
| `terms.txt` | your denylist - git-ignored, never committed |
| `tests/` | the test suite |
| `PROOF-windows.md` | the recorded clean-install proof run, Windows |
