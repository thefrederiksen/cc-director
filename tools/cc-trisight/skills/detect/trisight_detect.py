"""K-01 trisight_detect -- Full 3-tier detection pipeline (UIA + OCR + Pixel + Fusion)."""
import argparse
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "_shared"))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "_shared"))

from trisight_cli import run as cli_run
from output import success, error
from session import log_skill_call, log_skill_result

SKILL_NAME = "trisight_detect"


def main() -> None:
    parser = argparse.ArgumentParser(description="Full 3-tier detection pipeline")
    parser.add_argument("--window", "-w", required=True, help="Window title to detect elements in")
    parser.add_argument("--screenshot", "-s", help="Screenshot path (required for OCR/Pixel tiers)")
    parser.add_argument("--tiers", default="uia,ocr,pixel", help="Comma-separated tiers: uia,ocr,pixel (default: all)")
    parser.add_argument("--annotate", action="store_true", help="Also produce annotated screenshot")
    parser.add_argument("--output", "-o", help="Output path for annotated screenshot")
    parser.add_argument("--depth", "-d", type=int, default=15, help="Max UIA tree depth (default 15)")
    args = parser.parse_args()

    log_skill_call(SKILL_NAME, vars(args))

    cli_args: dict[str, str | None] = {"--window": args.window}
    if args.screenshot:
        cli_args["--screenshot"] = args.screenshot
    cli_args["--tiers"] = args.tiers
    cli_args["--depth"] = str(args.depth)
    if args.annotate:
        cli_args["--annotate"] = None
    if args.output:
        cli_args["--output"] = args.output

    exit_code, stdout, stderr, elapsed_ms = cli_run("detect", cli_args)

    if exit_code != 0:
        log_skill_result(SKILL_NAME, False, stderr)
        error(SKILL_NAME, f"Detection failed: {stderr}")

    try:
        data = json.loads(stdout)
    except json.JSONDecodeError:
        log_skill_result(SKILL_NAME, False, f"Invalid JSON: {stdout[:200]}")
        error(SKILL_NAME, f"Invalid JSON from trisight_cli: {stdout[:200]}")

    count = data.get("elementCount", 0)
    log_skill_result(SKILL_NAME, True, f"{count} elements detected")

    data["elapsed_ms"] = elapsed_ms
    success(SKILL_NAME, data)


if __name__ == "__main__":
    main()
