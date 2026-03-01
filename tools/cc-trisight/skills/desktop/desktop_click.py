"""K-05 desktop_click -- Click a UI element by name, automation ID, or coordinates."""
import argparse
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "_shared"))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "_shared"))

from cc_click import run as cc_run
from output import success, error
from session import log_skill_call, log_skill_result

SKILL_NAME = "desktop_click"


def main() -> None:
    parser = argparse.ArgumentParser(description="Click a UI element")
    parser.add_argument("--window", "-w", help="Window title or substring")
    parser.add_argument("--name", help="Element name/text to click")
    parser.add_argument("--id", dest="automation_id", help="Element AutomationId to click")
    parser.add_argument("--xy", help="Screen coordinates to click (e.g., '500,300')")
    args = parser.parse_args()

    if not args.name and not args.automation_id and not args.xy:
        error(SKILL_NAME, "Provide at least one of: --name, --id, --xy")

    log_skill_call(SKILL_NAME, vars(args))

    cc_args = {}
    if args.window:
        cc_args["-w"] = args.window
    if args.name:
        cc_args["--name"] = args.name
    if args.automation_id:
        cc_args["--id"] = args.automation_id
    if args.xy:
        cc_args["--xy"] = args.xy

    exit_code, stdout, stderr, elapsed_ms = cc_run("click", cc_args)

    if exit_code != 0:
        log_skill_result(SKILL_NAME, False, stderr)
        error(SKILL_NAME, f"Click failed: {stderr}")

    target = args.name or args.automation_id or args.xy
    log_skill_result(SKILL_NAME, True, f"Clicked {target}")
    success(SKILL_NAME, {
        "target": target,
        "window": args.window,
        "result": stdout,
        "elapsed_ms": elapsed_ms,
    })


if __name__ == "__main__":
    main()
