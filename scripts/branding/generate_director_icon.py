"""
Director application icon generator (Windows).

Renders scripts/branding/director-icon.svg into the multi-size Windows icon
used by the Director executable and its main window:

    src/CcDirector.Avalonia/app.ico

Run from the repository root:

    python scripts/branding/generate_director_icon.py

Requires cairosvg and Pillow:

    python -m pip install cairosvg pillow

The mark matches the macOS icon (scripts/local-build/mac/AppIcon.svg) so the
Director looks like the same application on both platforms. See that file and
director-icon.svg for why the two geometries differ.

Every size is rendered at four times its final width and downsampled. Cairo
antialiases text with subpixel coverage, which leaves coloured fringes on the
edges of the "D" when it is rendered straight to the target size; averaging four
pixels down to one removes them. ASCII-only output.
"""
import io
import os

import cairosvg
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(os.path.dirname(HERE))
SVG_SOURCE = os.path.join(HERE, "director-icon.svg")
ICO_TARGET = os.path.join(REPO_ROOT, "src", "CcDirector.Avalonia", "app.ico")

# Windows asks for the icon at these widths across the shell: 16 in the title
# bar and small taskbar, 20/24/40 at scaled display settings, 32 in the taskbar
# and Alt+Tab, 48 in Explorer, 128/256 for large tiles and file previews.
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
SUPERSAMPLE = 4


def render(size):
    """Render the SVG at the given width, supersampled and downsampled."""
    png = cairosvg.svg2png(
        url=SVG_SOURCE,
        output_width=size * SUPERSAMPLE,
        output_height=size * SUPERSAMPLE,
    )
    big = Image.open(io.BytesIO(png)).convert("RGBA")
    return big.resize((size, size), Image.LANCZOS)


def main():
    frames = {size: render(size) for size in SIZES}
    largest = frames[max(SIZES)]
    largest.save(
        ICO_TARGET,
        format="ICO",
        sizes=[(s, s) for s in SIZES],
        append_images=[frames[s] for s in SIZES if s != max(SIZES)],
    )
    print("WROTE", ICO_TARGET, SIZES)
    print("DONE")


if __name__ == "__main__":
    main()
