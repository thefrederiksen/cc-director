"""K-19 screen_find_text -- Find text on screen using OCR."""
import argparse
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "_shared"))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "_shared"))

from trisight_cli import run as cli_run
from output import success, error
from session import log_skill_call, log_skill_result

SKILL_NAME = "screen_find_text"


def main() -> None:
    parser = argparse.ArgumentParser(description="Find text on screen using OCR")
    parser.add_argument("--screenshot", "-s", required=True, help="Screenshot to search in")
    parser.add_argument("--text", "-t", help="Text to search for (case-insensitive substring match)")
    args = parser.parse_args()

    if not os.path.isfile(args.screenshot):
        error(SKILL_NAME, f"Screenshot not found: {args.screenshot}")

    log_skill_call(SKILL_NAME, vars(args))

    cli_args = {"--screenshot": args.screenshot}
    exit_code, stdout, stderr, elapsed_ms = cli_run("ocr", cli_args)

    if exit_code != 0:
        log_skill_result(SKILL_NAME, False, stderr)
        error(SKILL_NAME, f"OCR failed: {stderr}")

    try:
        data = json.loads(stdout)
    except json.JSONDecodeError:
        log_skill_result(SKILL_NAME, False, f"Invalid JSON: {stdout[:200]}")
        error(SKILL_NAME, f"Invalid JSON: {stdout[:200]}")

    regions = data.get("regions", [])

    # Filter by search text if specified
    if args.text:
        query = args.text.lower()
        matches = [r for r in regions if query in r.get("text", "").lower()]
    else:
        matches = regions

    log_skill_result(SKILL_NAME, True, f"{len(matches)} matches found")
    success(SKILL_NAME, {
        "matches": matches,
        "count": len(matches),
        "total_regions": len(regions),
        "query": args.text,
        "elapsed_ms": elapsed_ms,
    })


if __name__ == "__main__":
    main()
