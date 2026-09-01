# cc-scrub

Finds denylisted text in screenshots and destroys it, so a screenshot can go
into a public video, a documentation site or a website without a manual pixel
hunt.

Pure Python. Every dependency is a pip wheel. The text recognizer is the
operating system's own, reached through one seam. No external executable is
installed, required or looked for - in particular this tool never calls
tesseract, even on a machine where one happens to be present - and it
launches no subprocess at all.

## Install

```
python -m pip install Pillow winocr
```

`winocr` is a pip wheel that wraps the OCR engine built into Windows through
WinRT. There is no installer and no .exe. Proved on Python 3.11.6 with
Pillow 12.3.0 and winocr 0.0.15; see [PROOF-windows.md](PROOF-windows.md) for
the full clean-install transcript.

On macOS the engine is Apple's Vision text recognizer. That backend is not
implemented yet - see [Per-operating-system notes](#per-operating-system-notes).

## Use

```
python main.py <image-or-dir> [-o <out>] [--terms-file terms.txt]
               [--check-only] [--patch|--blur]
               [--pad N] [--scales 1,2,3] [--lang en] [--no-fold]
```

Common runs:

```
python main.py shot.png                  # scrub one image next to itself
python main.py shots -o out/             # scrub a whole folder into out/
python main.py shots --check-only        # lint a folder, change nothing
python main.py shot.png --patch          # opaque patch instead of a blur
```

The output is always written as `<name>-scrubbed.png`. The input is never
overwritten; the tool refuses outright if `-o` resolves to the input path.
When there are no hits, no output file is written at all.

Directory input picks up `.png`, `.jpg`, `.jpeg`, `.bmp` and skips anything
already named `*-scrubbed.*`, so re-running a folder is safe.

`-o` pointed at a directory that does not exist yet is treated as an output
*file* name, which is what you want for a single image and not what you want
for a folder. Create the folder first.

### Exit codes

| code | meaning |
| ---- | ------- |
| 0    | work done and proved, or `--check-only` found nothing |
| 1    | denylist hits remain readable in an output, or `--check-only` found hits |
| 2    | broken instrument or usage error - OCR unavailable, unreadable image, zero text read, bad arguments |

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

## How matching works

1. **OCR at several scales.** The image is read once per factor in
   `--scales` (default `1,2,3`), enlarged with LANCZOS before each read and
   every rectangle divided back down afterwards. Small grey interface text
   around 10 px is unreadable to the engine at native resolution: in the
   proof run the line `repo myorg/secret-repo` collapses to a three-character
   fragment at scale 1 and only resolves at 2 and 3, so the hit is reported
   as `scales=2,3`. Every scale always runs and the hits are unioned. This is
   not a fallback chain - different scales misread the same text in
   different ways, and the union is what makes detection reliable.
2. **Line joining.** Each OCR line is concatenated into one string with a map
   back to the word each character came from, so a term split across
   adjacent OCR words still matches and the covering words' rectangles are
   unioned into one hit rectangle. Joining happens within a line only.
3. **Normalisation.** Both the term and the OCR text are lowercased and
   stripped of everything that is not `a-z0-9`. That is what absorbs the
   punctuation and the spaces the engine injects into a URL - it returns
   `https : / /example. ai/path/session_011` for a line whose pixels read
   `https://example.ai/path/session_011`.
4. **Glyph folding** (on by default, `--no-fold` turns it off). Characters
   that share a shape are folded to one representative on both sides:
   `o0`, `li1tj!|`, `s5`, `z2`, `b8`, `g9`. Small anti-aliased interface text
   provokes exactly these confusions. A URL like `example.ai` can come back
   as `exampte.ai` - the engine misreads the `l` as a `t` - and without
   folding that URL is missed entirely. The proof run shows the same thing on
   a host name: `internal-hostname.local` is read as
   `https:/ftnternal-hostname.local/...`, found with folding, reported as
   `HITS: 0` with `--no-fold`.

   Folding deliberately over-matches in the safe direction: the worst case is
   a few extra blurred pixels, and every hit is printed with its coordinates
   and its source line so it can be checked by eye.
5. **Merging.** Overlapping rectangles for the same term collapse into one,
   so a hit seen at three scales is redacted once.

## How the redaction works

Each hit rectangle is padded by `--pad` pixels (default 4) and then:

- **`--blur`** (default) mosaics the region down to a handful of cells and
  scales it back with NEAREST, which throws the pixels away outright, then
  smooths the blocks with a Gaussian pass at radius `max(6, height/2)`. The
  text is not merely hard to read - the information is gone.
- **`--patch`** fills a rounded rectangle with the region's per-channel
  median colour, which is its background colour, so the redaction reads as a
  deliberate one rather than a black bar.

## How the verify pass works

The verify pass is mandatory and it is not a re-read of anything held in
memory. After the output is saved, the tool opens the **output file from
disk**, runs the same multi-scale OCR and the same matcher over it, and only
then decides.

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
- **The verify OCR read zero words from the output** - the instrument itself
  is broken and proves nothing. Exit 2.

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

**macOS** - `MacVisionBackend`, Apple's Vision text recognizer
(`VNRecognizeTextRequest`). **Implemented by the macOS backend** - it is
present in `ocr_backend.py` today and raises on construction, naming exactly
what it needs to return. It is deliberately loud rather than a stub that
returns an empty word list, because an empty word list looks exactly like a
clean screenshot, which is the one failure this tool must never produce. The
macOS proof is recorded separately when that backend lands.

**Anything else** - `get_backend()` raises, naming the platform. There is no
portable fallback engine and none will be added.

## Development

```
python gen_samples.py samples     # draw the synthetic test images
python -m pytest tests/ -q        # 25 tests, end to end over those images
```

The tests drive the real recognizer over the generated samples. Nothing is
mocked: a mocked engine would prove nothing about the engine this tool
actually depends on. On a platform with no backend yet the OCR tests skip by
platform name - never by probing whether OCR happens to work, so a broken
install on a supported platform fails the suite instead of quietly passing
it.

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
  character term after folding will hit far more than intended. Keep terms
  specific, read the printed hits, and use `--no-fold` if an exact match is
  wanted.
- **`--check-only` is a linter, not a guarantee.** It reports what the OCR can
  see. It cannot report sensitive text the OCR cannot read, which is why the
  scrub path always re-reads its own output.
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
