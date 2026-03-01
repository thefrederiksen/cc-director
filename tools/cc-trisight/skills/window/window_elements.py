"""K-12 window_elements -- List UI elements in a window."""
import argparse
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "_shared"))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "_shared"))

from cc_click import run as cc_run
from output import success, error
from session import log_skill_call, log_skill_result

SKILL_NAME = "window_elements"


def main() -> None:
    parser = argparse.ArgumentParser(description="List UI elements in a window")
    parser.add_argument("--window", "-w", required=True, help="Window title or substring")
    parser.add_argument("--type", "-t", dest="control_type", help="Filter by control type (e.g., Button, TextBox, MenuItem)")
    parser.add_argument("--depth", "-d", type=int, default=25, help="Max depth to search (default 25)")
    args = parser.parse_args()

    log_skill_call(SKILL_NAME, vars(args))

    cc_args = {"-w": args.window, "-d": str(args.depth)}
    if args.control_type:
        cc_args["-t"] = args.control_type

    exit_code, stdout, stderr, elapsed_ms = cc_run("list-elements", cc_args)

    if exit_code != 0:
        log_skill_result(SKILL_NAME, False, stderr)
        error(SKILL_NAME, f"list-elements failed: {stderr}")

    try:
        elements = json.loads(stdout)
    except json.JSONDecodeError:
        elements = stdout

    count = len(elements) if isinstance(elements, list) else 0
    log_skill_result(SKILL_NAME, True, f"{count} elements in {args.window}")

    success(SKILL_NAME, {
        "elements": elements,
        "count": count,
        "window": args.window,
        "control_type": args.control_type,
        "depth": args.depth,
        "elapsed_ms": elapsed_ms,
    })


if __name__ == "__main__":
    main()
