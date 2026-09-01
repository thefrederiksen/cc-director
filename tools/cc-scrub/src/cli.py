#!/usr/bin/env python
"""cc-scrub - find denylisted text in screenshots and destroy it.

Pure Python. Every dependency is a pip wheel. The text recognizer is the
operating system's own, reached through the single seam in ocr_backend.py.
No external executable is installed, required or looked for - in particular
this tool never calls tesseract - and it launches no subprocess at all.

Exit codes:
    0   work done and proved (or --check-only found nothing)
    1   denylist hits remain readable, or --check-only found hits
    2   broken instrument / usage error (OCR unavailable, unreadable image,
        zero text read from an image, bad arguments)

All output is ASCII only.
"""

import argparse
import os
import sys

try:
    from PIL import Image, ImageDraw, ImageFilter
except ImportError as exc:
    sys.stderr.write("FATAL: Pillow is not installed (%s).\n" % exc)
    sys.stderr.write("Fix: python -m pip install Pillow\n")
    sys.exit(2)

from .ocr_backend import ScrubError, get_backend


IMAGE_SUFFIXES = (".png", ".jpg", ".jpeg", ".bmp")
SCRUBBED_MARK = "-scrubbed"

TOOL_ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_TERMS_FILE = os.path.join(TOOL_ROOT, "terms.txt")
EXAMPLE_TERMS_FILE = os.path.join(TOOL_ROOT, "terms.example.txt")


# ----------------------------------------------------------------- text tools

def ascii_safe(text):
    """Return text with every non-ASCII character replaced by '?'.

    Repo rule: nothing this tool prints may contain non-ASCII. OCR output
    routinely contains them, so every string that reaches stdout goes
    through here.
    """
    return text.encode("ascii", "replace").decode("ascii")


# Glyph confusion classes. OCR of small anti-aliased UI text confuses
# characters that share a shape. These classes are applied identically to
# the denylist term and to the OCR text, so matching survives the
# confusion. This is deliberate over-matching in the safe direction: the
# worst case is that a few extra pixels get blurred, and every hit is
# printed with its coordinates and its source text so it can be checked by
# eye. Turn it off with --no-fold to require an exact match.
#
# The 'l'/'t' pair is in here because it is the confusion that actually
# bites on screenshots of terminals: a URL like example.ai can come back as
# exampte.ai, because the engine misreads the l as a t.
_FOLD_CLASSES = (
    ("o", "o0"),
    ("l", "li1tj!|"),
    ("s", "s5"),
    ("z", "z2"),
    ("b", "b8"),
    ("g", "g9"),
)

_FOLD_MAP = {}
for _target, _members in _FOLD_CLASSES:
    for _ch in _members:
        _FOLD_MAP[_ch] = _target


def normalise(text, fold=True):
    """Lowercase, drop everything that is not a-z0-9, then fold glyph classes.

    Dropping the non-alphanumerics is what lets a term match across OCR
    word splits and injected punctuation noise: the OCR reads
    "https : //example. ai/path/session_011" and the term
    "example.ai/path/session" still lands.
    """
    out = []
    for ch in text.lower():
        if ch.isascii() and (ch.isalpha() or ch.isdigit()):
            out.append(_FOLD_MAP.get(ch, ch) if fold else ch)
    return "".join(out)


def load_terms(path):
    """Read the denylist. One term per line; '#' starts a comment."""
    if not os.path.isfile(path):
        raise ScrubError(
            "terms file not found: %s. The denylist is your own config and is "
            "never committed; copy %s to %s and put your terms in it."
            % (path, EXAMPLE_TERMS_FILE, DEFAULT_TERMS_FILE))
    terms = []
    with open(path, "r", encoding="utf-8") as handle:
        for raw in handle:
            line = raw.split("#", 1)[0].strip()
            if line:
                terms.append(line)
    if not terms:
        raise ScrubError("terms file %s has no terms in it" % path)
    return terms


# ------------------------------------------------------------------------ ocr

class OcrPass(object):
    """One OCR read of one image at one scale."""

    def __init__(self, scale, lines, word_count):
        self.scale = scale
        self.lines = lines          # list of list of (text, rect) at scale 1
        self.word_count = word_count


def ocr_image(image, scale, lang, backend):
    """OCR image upscaled by 'scale'; return an OcrPass with rects at scale 1.

    Small grey UI text is invisible to the engine at native resolution, so
    the image is enlarged with LANCZOS before the read and every rectangle
    is divided back down afterwards.
    """
    width, height = image.size
    if scale != 1:
        width, height = width * scale, height * scale
        if max(width, height) > backend.max_image_dimension:
            raise ScrubError(
                "scale %d makes the image %dx%d, over the %s limit of %d px. "
                "Use a smaller --scales value."
                % (scale, width, height, backend.name,
                   backend.max_image_dimension))
        scaled = image.resize((width, height), Image.LANCZOS)
    else:
        scaled = image

    try:
        words = backend.recognize(scaled, lang)
    except ScrubError as exc:
        raise ScrubError("at scale %d: %s" % (scale, exc))

    lines = []
    current_line = None
    current_words = []
    words_seen = 0
    for word in words:
        if current_line is not None and word["line"] != current_line:
            if current_words:
                lines.append(current_words)
            current_words = []
        current_line = word["line"]
        box = (word["x"] / float(scale),
               word["y"] / float(scale),
               word["w"] / float(scale),
               word["h"] / float(scale))
        current_words.append((word["text"], box))
        words_seen += 1
    if current_words:
        lines.append(current_words)
    return OcrPass(scale, lines, words_seen)


# -------------------------------------------------------------------- matching

class Hit(object):
    def __init__(self, term, box, scale, source_text):
        self.term = term
        self.box = box                    # (x0, y0, x1, y1) floats, scale 1
        self.scales = [scale]
        self.source_text = source_text

    def as_rect(self):
        x0, y0, x1, y1 = self.box
        return (int(x0), int(y0), int(x1 - x0), int(y1 - y0))


def find_hits(ocr_pass, terms, fold):
    """Match every term against every OCR line of one pass.

    Each line is concatenated into one normalised string with a map back to
    the word that produced each character, so a term split across adjacent
    OCR words still matches and the covering words' rectangles are unioned
    into a single hit rectangle.
    """
    hits = []
    for words in ocr_pass.lines:
        joined = []
        owner = []
        for index, (text, _box) in enumerate(words):
            piece = normalise(text, fold)
            joined.append(piece)
            owner.extend([index] * len(piece))
        line_norm = "".join(joined)
        if not line_norm:
            continue
        line_text = " ".join(text for text, _box in words)

        for term in terms:
            needle = normalise(term, fold)
            if not needle:
                continue
            start = line_norm.find(needle)
            while start != -1:
                covered = sorted(set(owner[start:start + len(needle)]))
                boxes = [words[i][1] for i in covered]
                x0 = min(b[0] for b in boxes)
                y0 = min(b[1] for b in boxes)
                x1 = max(b[0] + b[2] for b in boxes)
                y1 = max(b[1] + b[3] for b in boxes)
                hits.append(Hit(term, (x0, y0, x1, y1),
                                ocr_pass.scale, line_text))
                start = line_norm.find(needle, start + 1)
    return hits


def collect_hits(image, terms, scales, lang, fold, backend):
    """Run every scale, union the hits, merge overlapping rectangles.

    Every scale always runs - this is not a fallback chain. Different
    scales read the same small text differently, and the union is what
    makes detection reliable.
    """
    passes = []
    for scale in scales:
        passes.append(ocr_image(image, scale, lang, backend))

    total_words = sum(p.word_count for p in passes)
    raw = []
    for ocr_pass in passes:
        raw.extend(find_hits(ocr_pass, terms, fold))

    merged = merge_hits(raw)
    return merged, passes, total_words


def merge_hits(hits):
    """Merge hits of the same term whose rectangles overlap or touch."""
    merged = []
    for hit in hits:
        for other in merged:
            if other.term == hit.term and _overlaps(other.box, hit.box):
                other.box = (min(other.box[0], hit.box[0]),
                             min(other.box[1], hit.box[1]),
                             max(other.box[2], hit.box[2]),
                             max(other.box[3], hit.box[3]))
                for scale in hit.scales:
                    if scale not in other.scales:
                        other.scales.append(scale)
                if len(hit.source_text) > len(other.source_text):
                    other.source_text = hit.source_text
                break
        else:
            merged.append(hit)
    for hit in merged:
        hit.scales.sort()
    merged.sort(key=lambda h: (h.box[1], h.box[0]))
    return merged


def _overlaps(a, b):
    return not (a[2] < b[0] or b[2] < a[0] or a[3] < b[1] or b[3] < a[1])


# -------------------------------------------------------------------- redaction

def _pad_box(box, pad, size):
    x0 = max(0, int(box[0]) - pad)
    y0 = max(0, int(box[1]) - pad)
    x1 = min(size[0], int(round(box[2])) + pad)
    y1 = min(size[1], int(round(box[3])) + pad)
    return (x0, y0, x1, y1)


def _median_colour(region):
    """Per-channel median of the region, read off the channel histograms.

    The median of a text region is its background colour, so a patch filled
    with it reads as a deliberate redaction rather than a black bar.
    """
    rgb = region.convert("RGB")
    total = rgb.size[0] * rgb.size[1]
    colour = []
    for channel in rgb.split():
        histogram = channel.histogram()
        seen = 0
        for value, count in enumerate(histogram):
            seen += count
            if seen * 2 >= total:
                colour.append(value)
                break
    return tuple(colour)


def _destroy(region):
    """Mosaic the region down to a handful of cells, then blur it smooth.

    Downsampling throws the pixels away outright, so the text is not merely
    hard to read - the information is gone. The Gaussian pass on top only
    removes the blocky edges.
    """
    width, height = region.size
    cells_x = max(1, width // 14)
    cells_y = max(1, height // 6)
    mosaic = region.resize((cells_x, cells_y), Image.BILINEAR)
    mosaic = mosaic.resize((width, height), Image.NEAREST)
    radius = max(6.0, height / 2.0)
    return mosaic.filter(ImageFilter.GaussianBlur(radius=radius))


def redact(image, hits, pad, mode):
    """Return a new image with every hit rectangle destroyed."""
    out = image.copy()
    for hit in hits:
        box = _pad_box(hit.box, pad, out.size)
        if box[2] <= box[0] or box[3] <= box[1]:
            raise ScrubError("hit for term '%s' produced an empty rectangle %s"
                             % (ascii_safe(hit.term), box))
        region = out.crop(box)
        if mode == "patch":
            colour = _median_colour(region)
            draw = ImageDraw.Draw(out)
            radius = max(2, min(8, (box[3] - box[1]) // 2))
            draw.rounded_rectangle(box, radius=radius, fill=colour)
        else:
            out.paste(_destroy(region), box)
    return out


# ------------------------------------------------------------------- reporting

def describe(hit):
    x, y, w, h = hit.as_rect()
    return ("term='%s' rect=(x=%d y=%d w=%d h=%d) scales=%s ocr_line='%s'"
            % (ascii_safe(hit.term), x, y, w, h,
               ",".join(str(s) for s in hit.scales),
               ascii_safe(hit.source_text)))


# ----------------------------------------------------------------- per image

def process_image(path, out_path, terms, args, backend):
    """Scrub one image. Returns True if the image ends up proved clean."""
    print("")
    print("IMAGE %s" % path)
    try:
        image = Image.open(path)
        image.load()
    except Exception as exc:
        raise ScrubError("cannot read image %s: %s" % (path, exc))
    image = image.convert("RGB")
    print("  size %dx%d  scales %s  fold %s"
          % (image.size[0], image.size[1],
             ",".join(str(s) for s in args.scales),
             "on" if not args.no_fold else "off"))

    hits, passes, total_words = collect_hits(
        image, terms, args.scales, args.lang, not args.no_fold, backend)

    for ocr_pass in passes:
        print("  ocr scale %d: %d words in %d lines"
              % (ocr_pass.scale, ocr_pass.word_count, len(ocr_pass.lines)))

    # Broken instrument, not a clean image. An empty OCR result can never
    # be read as "there was nothing sensitive here".
    if total_words == 0:
        raise ScrubError(
            "OCR read ZERO words from %s across scales %s. That is a broken "
            "read, not a clean image. Refusing to call this scrubbed."
            % (path, ",".join(str(s) for s in args.scales)))

    print("  HITS: %d" % len(hits))
    for hit in hits:
        print("    %s" % describe(hit))

    if args.check_only:
        if hits:
            print("  CHECK-ONLY: %d hit(s) present, image NOT modified." % len(hits))
            return False
        print("  CHECK-ONLY: no hits against %d terms over %d OCR words."
              % (len(terms), total_words))
        return True

    if not hits:
        print("  Nothing to redact. No output written (input left untouched).")
        return True

    scrubbed = redact(image, hits, args.pad, args.mode)
    if os.path.abspath(out_path) == os.path.abspath(path):
        raise ScrubError("refusing to overwrite the input image %s" % path)
    scrubbed.save(out_path)
    print("  WROTE %s (mode=%s pad=%d)" % (out_path, args.mode, args.pad))

    # Mandatory verify pass: re-OCR the OUTPUT, from disk.
    print("  VERIFY: re-reading %s" % out_path)
    verify_image = Image.open(out_path)
    verify_image.load()
    verify_image = verify_image.convert("RGB")
    remaining, verify_passes, verify_words = collect_hits(
        verify_image, terms, args.scales, args.lang, not args.no_fold, backend)
    for ocr_pass in verify_passes:
        print("    verify scale %d: %d words in %d lines"
              % (ocr_pass.scale, ocr_pass.word_count, len(ocr_pass.lines)))

    # The verify instrument must itself prove it can read. A verify pass
    # that reads nothing proves nothing.
    if verify_words == 0:
        raise ScrubError(
            "verify OCR read ZERO words from the output %s. The verify "
            "instrument is broken; the result is not proved." % out_path)

    if remaining:
        print("  VERIFY FAILED: %d denylist hit(s) still readable in the output:"
              % len(remaining))
        for hit in remaining:
            print("    %s" % describe(hit))
        return False

    print("  VERIFY PASSED: %d hit(s) found, %d region(s) redacted, verify OCR "
          "read %d words in the output and 0 denylist hit(s) remain."
          % (len(hits), len(hits), verify_words))
    return True


# ----------------------------------------------------------------------- main

def gather_inputs(target):
    if os.path.isfile(target):
        return [target]
    if os.path.isdir(target):
        found = []
        for name in sorted(os.listdir(target)):
            stem, suffix = os.path.splitext(name)
            if suffix.lower() in IMAGE_SUFFIXES and not stem.endswith(SCRUBBED_MARK):
                found.append(os.path.join(target, name))
        if not found:
            raise ScrubError("no images found in directory %s" % target)
        return found
    raise ScrubError("no such file or directory: %s" % target)


def output_path_for(source, out_option, many):
    stem, _suffix = os.path.splitext(os.path.basename(source))
    default_name = stem + SCRUBBED_MARK + ".png"
    if not out_option:
        return os.path.join(os.path.dirname(os.path.abspath(source)), default_name)
    if many or os.path.isdir(out_option):
        if not os.path.isdir(out_option):
            os.makedirs(out_option)
        return os.path.join(out_option, default_name)
    parent = os.path.dirname(os.path.abspath(out_option))
    if parent and not os.path.isdir(parent):
        os.makedirs(parent)
    return out_option


def parse_scales(text):
    scales = []
    for piece in text.split(","):
        piece = piece.strip()
        if not piece:
            continue
        if not piece.isdigit() or int(piece) < 1:
            raise ScrubError("--scales takes positive whole numbers, got '%s'"
                             % ascii_safe(piece))
        value = int(piece)
        if value not in scales:
            scales.append(value)
    if not scales:
        raise ScrubError("--scales is empty")
    return sorted(scales)


def build_parser():
    parser = argparse.ArgumentParser(
        prog="cc-scrub",
        description="Blur denylisted text out of screenshots (pure Python).")
    parser.add_argument("target", help="image file or directory of images")
    parser.add_argument("-o", "--out",
                        help="output file (single input) or directory")
    parser.add_argument("--terms-file", default=DEFAULT_TERMS_FILE,
                        help="denylist file, one term per line")
    parser.add_argument("--check-only", action="store_true",
                        help="report hits and coordinates, change nothing, "
                             "exit 1 if any hit")
    parser.add_argument("--patch", dest="mode", action="store_const",
                        const="patch",
                        help="paint an opaque rounded patch instead of blurring")
    parser.add_argument("--blur", dest="mode", action="store_const",
                        const="blur", help="mosaic and blur the region (default)")
    parser.add_argument("--pad", type=int, default=4,
                        help="pixels of padding around each hit (default 4)")
    parser.add_argument("--scales", default="1,2,3",
                        help="OCR upscale factors, comma separated "
                             "(default 1,2,3)")
    parser.add_argument("--lang", default="en",
                        help="OCR language tag (default en)")
    parser.add_argument("--no-fold", action="store_true",
                        help="disable OCR glyph-confusion folding and require "
                             "an exact normalised match")
    parser.set_defaults(mode="blur")
    return parser


def main(argv):
    parser = build_parser()
    args = parser.parse_args(argv)

    try:
        args.scales = parse_scales(args.scales)
        if args.pad < 0:
            raise ScrubError("--pad cannot be negative")
        terms = load_terms(args.terms_file)
        sources = gather_inputs(args.target)
        backend = get_backend()
    except ScrubError as exc:
        sys.stderr.write("FATAL: %s\n" % ascii_safe(str(exc)))
        return 2

    print("cc-scrub")
    print("  ocr engine : %s" % backend.name)
    print("  terms file : %s (%d terms)" % (args.terms_file, len(terms)))
    for term in terms:
        print("    - %s" % ascii_safe(term))
    print("  images     : %d" % len(sources))
    print("  mode       : %s" % ("check-only" if args.check_only else args.mode))

    failures = []
    for source in sources:
        out_path = (None if args.check_only
                    else output_path_for(source, args.out, len(sources) > 1))
        try:
            ok = process_image(source, out_path, terms, args, backend)
        except ScrubError as exc:
            sys.stderr.write("FATAL: %s\n" % ascii_safe(str(exc)))
            return 2
        if not ok:
            failures.append(source)

    print("")
    print("SUMMARY: %d image(s) processed, %d clean, %d failed."
          % (len(sources), len(sources) - len(failures), len(failures)))
    if failures:
        for source in failures:
            print("  FAILED %s" % source)
        return 1
    return 0


def main_entry():
    """Console-script entry point declared in pyproject.toml."""
    sys.exit(main(sys.argv[1:]))


if __name__ == "__main__":
    main_entry()
