"""K-03 trisight_ocr -- Tier 2 only: Windows OCR text detection."""
import argparse
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "_shared"))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "_shared"))

from trisight_cli import run as cli_run
from output import success, error
from session import log_skill_call, log_skill_result

SKILL_NAME = "trisight_ocr"


def main() -> None:
    parser = argparse.ArgumentParser(description="OCR text detection (Tier 2)")
    parser.add_argument("--screenshot", "-s", required=True, help="Screenshot path")
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

    count = data.get("count", 0)
    log_skill_result(SKILL_NAME, True, f"{count} OCR regions")

    data["elapsed_ms"] = elapsed_ms
    success(SKILL_NAME, data)


if __name__ == "__main__":
    main()
