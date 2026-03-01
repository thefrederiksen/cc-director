"""K-02 trisight_uia -- Tier 1 only: UI Automation element tree."""
import argparse
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "_shared"))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "_shared"))

from trisight_cli import run as cli_run
from output import success, error
from session import log_skill_call, log_skill_result

SKILL_NAME = "trisight_uia"


def main() -> None:
    parser = argparse.ArgumentParser(description="UIA element detection (Tier 1)")
    parser.add_argument("--window", "-w", required=True, help="Window title")
    parser.add_argument("--depth", "-d", type=int, default=15, help="Max tree depth (default 15)")
    args = parser.parse_args()

    log_skill_call(SKILL_NAME, vars(args))

    cli_args = {"--window": args.window, "--depth": str(args.depth)}
    exit_code, stdout, stderr, elapsed_ms = cli_run("uia", cli_args)

    if exit_code != 0:
        log_skill_result(SKILL_NAME, False, stderr)
        error(SKILL_NAME, f"UIA detection failed: {stderr}")

    try:
        data = json.loads(stdout)
    except json.JSONDecodeError:
        log_skill_result(SKILL_NAME, False, f"Invalid JSON: {stdout[:200]}")
        error(SKILL_NAME, f"Invalid JSON: {stdout[:200]}")

    count = data.get("count", 0)
    log_skill_result(SKILL_NAME, True, f"{count} UIA elements")

    data["elapsed_ms"] = elapsed_ms
    success(SKILL_NAME, data)


if __name__ == "__main__":
    main()
