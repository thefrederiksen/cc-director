"""K-22 screen_diff -- Compare two screenshots and highlight differences."""
import argparse
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "_shared"))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "_shared"))

from output import success, error
from session import log_skill_call, log_skill_result, log_screenshot, next_screenshot_path

SKILL_NAME = "screen_diff"


def main() -> None:
    parser = argparse.ArgumentParser(description="Compare two screenshots")
    parser.add_argument("--before", "-b", required=True, help="Path to 'before' screenshot")
    parser.add_argument("--after", "-a", required=True, help="Path to 'after' screenshot")
    parser.add_argument("--output", "-o", help="Output path for diff image (auto-generated if omitted)")
    parser.add_argument("--threshold", type=int, default=30, help="Pixel difference threshold 0-255 (default 30)")
    args = parser.parse_args()

    for label, path in [("before", args.before), ("after", args.after)]:
        if not os.path.isfile(path):
            error(SKILL_NAME, f"{label} screenshot not found: {path}")

    log_skill_call(SKILL_NAME, vars(args))

    try:
        from PIL import Image, ImageChops
    except ImportError:
        error(SKILL_NAME, "Pillow is required: pip install Pillow")

    try:
        img_before = Image.open(args.before).convert("RGB")
        img_after = Image.open(args.after).convert("RGB")

        # Resize to match if needed
        if img_before.size != img_after.size:
            img_after = img_after.resize(img_before.size)

        # Compute difference
        diff = ImageChops.difference(img_before, img_after)

        # Convert to grayscale for analysis
        diff_gray = diff.convert("L")
        pixels = list(diff_gray.getdata())
        total = len(pixels)
        changed = sum(1 for p in pixels if p > args.threshold)
        change_pct = round(changed / total * 100, 2) if total > 0 else 0

        # Create highlighted diff image (red overlay on changed areas)
        diff_highlight = img_after.copy()
        diff_data = diff_gray.getdata()
        highlight_data = list(diff_highlight.getdata())
        for i, p in enumerate(diff_data):
            if p > args.threshold:
                r, g, b = highlight_data[i]
                highlight_data[i] = (min(255, r + 128), max(0, g - 64), max(0, b - 64))
        diff_highlight.putdata(highlight_data)

        output_path = args.output or next_screenshot_path()
        os.makedirs(os.path.dirname(output_path), exist_ok=True)
        diff_highlight.save(output_path)

        log_screenshot(output_path, "screen_diff")
        log_skill_result(SKILL_NAME, True, f"{change_pct}% changed")

        success(SKILL_NAME, {
            "diff_path": output_path,
            "change_percent": change_pct,
            "changed_pixels": changed,
            "total_pixels": total,
            "threshold": args.threshold,
            "before_size": list(img_before.size),
            "after_size": list(img_after.size),
        })
    except FileNotFoundError as e:
        log_skill_result(SKILL_NAME, False, str(e))
        error(SKILL_NAME, f"Screenshot not found: {e}")
    except OSError as e:
        log_skill_result(SKILL_NAME, False, str(e))
        error(SKILL_NAME, f"Diff failed: {e}")


if __name__ == "__main__":
    main()
