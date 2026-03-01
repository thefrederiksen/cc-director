"""K-04 trisight_pixel -- Tier 3 only: Pixel analysis detection (wraps tools/pixel_detect.py)."""
import argparse
import json
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "_shared"))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "_shared"))

from config import get_python_path
from output import success, error
from session import log_skill_call, log_skill_result

SKILL_NAME = "trisight_pixel"

_PIXEL_DETECT_SCRIPT = os.path.normpath(os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "..", "..", "..", "tools", "pixel_detect.py",
))


def main() -> None:
    parser = argparse.ArgumentParser(description="Pixel analysis detection (Tier 3)")
    parser.add_argument("--screenshot", "-s", required=True, help="Screenshot path")
    args = parser.parse_args()

    if not os.path.isfile(args.screenshot):
        error(SKILL_NAME, f"Screenshot not found: {args.screenshot}")

    script = os.environ.get("CC_PIXEL_DETECT_PATH", _PIXEL_DETECT_SCRIPT)
    if not os.path.isfile(script):
        error(SKILL_NAME, f"pixel_detect.py not found at: {script}")

    log_skill_call(SKILL_NAME, vars(args))

    python = get_python_path()
    start = time.perf_counter()
    try:
        proc = subprocess.run(
            [python, script, args.screenshot],
            capture_output=True, text=True, timeout=30,
        )
        elapsed_ms = int((time.perf_counter() - start) * 1000)

        if proc.returncode != 0:
            log_skill_result(SKILL_NAME, False, proc.stderr.strip())
            error(SKILL_NAME, f"pixel_detect failed: {proc.stderr.strip()}")

        try:
            data = json.loads(proc.stdout.strip())
        except json.JSONDecodeError:
            log_skill_result(SKILL_NAME, False, f"Invalid JSON: {proc.stdout[:200]}")
            error(SKILL_NAME, f"Invalid JSON from pixel_detect: {proc.stdout[:200]}")

        elements = data.get("elements", [])
        log_skill_result(SKILL_NAME, True, f"{len(elements)} pixel detections")

        success(SKILL_NAME, {
            "elements": elements,
            "count": len(elements),
            "elapsed_ms": elapsed_ms,
            "script_elapsed_ms": data.get("elapsed_ms"),
        })
    except subprocess.TimeoutExpired:
        log_skill_result(SKILL_NAME, False, "Timed out after 30s")
        error(SKILL_NAME, "pixel_detect timed out after 30s")
    except FileNotFoundError:
        log_skill_result(SKILL_NAME, False, f"Python not found: {python}")
        error(SKILL_NAME, f"Python not found: {python}")


if __name__ == "__main__":
    main()
