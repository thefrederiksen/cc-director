#!/usr/bin/env python
"""Render the synthetic sample screenshots cc-scrub is tested against.

Real screenshots cannot be committed to a public repository, and a test
that needs a private file is a test nobody else can run. So the samples are
drawn here, from nothing but Pillow, and every planted term is one of the
deliberately fake ones in terms.example.txt.

Four images, each aimed at one thing the tool has to survive:

    sample-normal.png    ordinary dark-on-light text at a readable size
    sample-small-ui.png  ~10 px grey-on-dark text, the size real product
                         chrome uses, which the recognizer cannot resolve at
                         native resolution and only reads once upscaled
    sample-glyph.png     a host name full of the letter l, the glyph the
                         recognizer most often returns as a t
    sample-notext.png    shapes and a gradient, no text at all - the image
                         that must exit 2 as a broken read rather than pass
                         as clean

Run it directly to write them somewhere:

    python gen_samples.py out/

All output is ASCII only.
"""

import os
import sys

try:
    from PIL import Image, ImageDraw, ImageFilter, ImageFont
except ImportError as exc:
    sys.stderr.write("FATAL: Pillow is not installed (%s).\n" % exc)
    sys.stderr.write("Fix: python -m pip install Pillow\n")
    sys.exit(2)


# The planted terms. These are the example denylist entries, so nothing
# real is ever drawn into a committed or published sample.
TERM_EMAIL = "example@example.com"
TERM_REPO = "myorg/secret-repo"
TERM_HOST = "internal-hostname.local"

SAMPLE_NAMES = ("sample-normal.png", "sample-small-ui.png",
                "sample-glyph.png", "sample-notext.png")


def _font(size):
    """A scalable font with no system font hunting and no download.

    Pillow ships one, and load_default takes a size from 10.1.0 onward.
    Asking for it by size is what keeps these samples identical on every
    machine - a system font would render differently per operating system
    and the recognizer would read different words.
    """
    return ImageFont.load_default(size=size)


def _text_block(draw, x, y, lines, font, fill, leading):
    for index, line in enumerate(lines):
        draw.text((x, y + index * leading), line, font=font, fill=fill)


def _normal(path):
    """Ordinary text at a readable size, dark on light."""
    image = Image.new("RGB", (900, 260), (250, 250, 248))
    draw = ImageDraw.Draw(image)
    draw.rectangle((0, 0, 899, 46), fill=(232, 232, 228))
    _text_block(draw, 24, 14, ["Account settings"], _font(22), (20, 20, 20), 0)
    _text_block(draw, 24, 78, [
        "Owner of this workspace",
        "Contact address: " + TERM_EMAIL,
        "Plan: standard, renews every month",
        "Seats in use: 4 of 10",
    ], _font(20), (35, 35, 40), 40)
    image.save(path)
    return path


def _small_ui(path):
    """About 10 px grey on a dark ground - product chrome, not body text."""
    image = Image.new("RGB", (900, 220), (28, 28, 32))
    draw = ImageDraw.Draw(image)
    draw.rectangle((0, 0, 899, 30), fill=(38, 38, 44))
    _text_block(draw, 18, 9, ["Session detail"], _font(13), (210, 210, 214), 0)
    _text_block(draw, 18, 54, [
        "branch feature/rendering",
        "repo " + TERM_REPO,
        "machine build agent two",
        "started eleven minutes ago",
        "last commit touched four files",
    ], _font(10), (175, 175, 182), 22)
    image.save(path)
    return path


def _glyph(path):
    """A host name made mostly of the letter l, on a terminal-like ground."""
    image = Image.new("RGB", (900, 220), (24, 26, 30))
    draw = ImageDraw.Draw(image)
    _text_block(draw, 18, 30, [
        "connecting to the build host",
        "https://" + TERM_HOST + "/status/session",
        "handshake complete after two seconds",
        "streaming the log until the run ends",
    ], _font(11), (198, 200, 206), 40)
    image.save(path)
    return path


def _notext(path):
    """A gradient and some shapes. No glyphs anywhere."""
    image = Image.new("RGB", (640, 400))
    draw = ImageDraw.Draw(image)
    for y in range(400):
        shade = 40 + int(y * 0.35)
        draw.line((0, y, 639, y), fill=(shade, shade // 2 + 20, 120 - shade // 4))
    draw.ellipse((80, 90, 300, 310), fill=(220, 190, 90))
    draw.ellipse((360, 140, 520, 300), fill=(90, 170, 200))
    # Blurred on purpose. Sharp shapes give the recognizer edges it will
    # occasionally return as a stray glyph, and one stray glyph would turn
    # this from "reads nothing" into "reads something", which is not the
    # case this sample exists to cover.
    image = image.filter(ImageFilter.GaussianBlur(radius=6))
    image.save(path)
    return path


def generate(out_dir):
    """Write all four samples into out_dir; return their paths in order."""
    if not os.path.isdir(out_dir):
        os.makedirs(out_dir)
    return [
        _normal(os.path.join(out_dir, "sample-normal.png")),
        _small_ui(os.path.join(out_dir, "sample-small-ui.png")),
        _glyph(os.path.join(out_dir, "sample-glyph.png")),
        _notext(os.path.join(out_dir, "sample-notext.png")),
    ]


def main(argv):
    out_dir = argv[0] if argv else "samples"
    print("cc-scrub sample generator")
    for path in generate(out_dir):
        with Image.open(path) as image:
            print("  WROTE %s (%dx%d)" % (path, image.size[0], image.size[1]))
    print("Planted terms: %s, %s, %s" % (TERM_EMAIL, TERM_REPO, TERM_HOST))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
