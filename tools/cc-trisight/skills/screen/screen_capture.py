"""K-17 screen_capture -- Take a screenshot of the desktop or a specific window."""
import argparse
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "_shared"))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "_shared"))

from cc_click import run as cc_run
from output import success, error
from session import log_skill_call, log_skill_result, log_screenshot, next_screenshot_path

SKILL_NAME = "screen_capture"


def main() -> None:
    parser = argparse.ArgumentParser(description="Take a screenshot")
    parser.add_argument("--window", "-w", help="Window title to capture (omit for full desktop)")
    parser.add_argument("--output", "-o", help="Output path (auto-generated if omitted)")
    args = parser.parse_args()

    log_skill_call(SKILL_NAME, vars(args))

    output_path = args.output or next_screenshot_path()
    os.makedirs(os.path.dirname(output_path), exist_ok=True)

    cc_args = {"-o": output_path}
    if args.window:
        cc_args["-w"] = args.window

    exit_code, stdout, stderr, elapsed_ms = cc_run("screenshot", cc_args)

    if exit_code != 0:
        log_skill_result(SKILL_NAME, False, stderr)
        error(SKILL_NAME, f"Screenshot failed: {stderr}")

    if not os.path.isfile(output_path):
        log_skill_result(SKILL_NAME, False, "File not created")
        error(SKILL_NAME, "Screenshot command succeeded but file not found")

    size_kb = os.path.getsize(output_path) // 1024
    log_screenshot(output_path, f"capture window={args.window or 'desktop'}")
    log_skill_result(SKILL_NAME, True, f"{output_path} ({size_kb} KB)")

    success(SKILL_NAME, {
        "path": output_path,
        "size_kb": size_kb,
        "window": args.window,
        "elapsed_ms": elapsed_ms,
    })


if __name__ == "__main__":
    main()
