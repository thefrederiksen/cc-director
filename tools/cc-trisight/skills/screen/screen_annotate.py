"""K-18 screen_annotate -- Produce annotated screenshot with numbered element boxes."""
import argparse
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "_shared"))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "_shared"))

from trisight_cli import run as cli_run
from output import success, error
from session import log_skill_call, log_skill_result, log_screenshot

SKILL_NAME = "screen_annotate"


def main() -> None:
    parser = argparse.ArgumentParser(description="Annotate screenshot with detected elements")
    parser.add_argument("--screenshot", "-s", required=True, help="Input screenshot path")
    parser.add_argument("--window", "-w", help="Window title for auto-detection")
    parser.add_argument("--elements", "-e", help="Pre-computed elements JSON file (skip detection)")
    parser.add_argument("--output", "-o", help="Output annotated screenshot path")
    args = parser.parse_args()

    if not os.path.isfile(args.screenshot):
        error(SKILL_NAME, f"Screenshot not found: {args.screenshot}")

    if not args.window and not args.elements:
        error(SKILL_NAME, "Either --window (for auto-detect) or --elements (pre-computed) is required")

    log_skill_call(SKILL_NAME, vars(args))

    cli_args: dict[str, str | None] = {"--screenshot": args.screenshot}
    if args.window:
        cli_args["--window"] = args.window
    if args.elements:
        cli_args["--elements"] = args.elements
    if args.output:
        cli_args["--output"] = args.output

    exit_code, stdout, stderr, elapsed_ms = cli_run("annotate", cli_args)

    if exit_code != 0:
        log_skill_result(SKILL_NAME, False, stderr)
        error(SKILL_NAME, f"Annotation failed: {stderr}")

    try:
        data = json.loads(stdout)
    except json.JSONDecodeError:
        log_skill_result(SKILL_NAME, False, f"Invalid JSON: {stdout[:200]}")
        error(SKILL_NAME, f"Invalid JSON: {stdout[:200]}")

    annotated_path = data.get("annotatedPath", "")
    if annotated_path:
        log_screenshot(annotated_path, "annotated")

    log_skill_result(SKILL_NAME, True, f"Annotated -> {annotated_path}")
    data["elapsed_ms"] = elapsed_ms
    success(SKILL_NAME, data)


if __name__ == "__main__":
    main()
