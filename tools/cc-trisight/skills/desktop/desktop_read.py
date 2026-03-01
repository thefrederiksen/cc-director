"""K-07 desktop_read -- Read text from a UI element."""
import argparse
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "_shared"))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "_shared"))

from cc_click import run as cc_run
from output import success, error
from session import log_skill_call, log_skill_result

SKILL_NAME = "desktop_read"


def main() -> None:
    parser = argparse.ArgumentParser(description="Read text from a UI element")
    parser.add_argument("--window", "-w", required=True, help="Window title or substring")
    parser.add_argument("--name", help="Element name to read from")
    parser.add_argument("--id", dest="automation_id", help="Element AutomationId to read from")
    args = parser.parse_args()

    log_skill_call(SKILL_NAME, vars(args))

    cc_args = {"-w": args.window}
    if args.name:
        cc_args["--name"] = args.name
    if args.automation_id:
        cc_args["--id"] = args.automation_id

    exit_code, stdout, stderr, elapsed_ms = cc_run("read-text", cc_args)

    if exit_code != 0:
        log_skill_result(SKILL_NAME, False, stderr)
        error(SKILL_NAME, f"Read failed: {stderr}")

    log_skill_result(SKILL_NAME, True, f"Read {len(stdout)} chars")
    success(SKILL_NAME, {
        "text": stdout,
        "text_length": len(stdout),
        "window": args.window,
        "element": args.name or args.automation_id,
        "elapsed_ms": elapsed_ms,
    })


if __name__ == "__main__":
    main()
