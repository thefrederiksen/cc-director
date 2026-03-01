"""K-11 window_list -- List all visible windows."""
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "_shared"))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "_shared"))

from cc_click import run as cc_run
from output import success, error
from session import log_skill_call, log_skill_result

SKILL_NAME = "window_list"


def main() -> None:
    log_skill_call(SKILL_NAME, {})

    exit_code, stdout, stderr, elapsed_ms = cc_run("list-windows")

    if exit_code != 0:
        log_skill_result(SKILL_NAME, False, stderr)
        error(SKILL_NAME, f"list-windows failed: {stderr}")

    try:
        windows = json.loads(stdout)
    except json.JSONDecodeError:
        windows = stdout

    count = len(windows) if isinstance(windows, list) else 0
    log_skill_result(SKILL_NAME, True, f"{count} windows found")

    success(SKILL_NAME, {
        "windows": windows,
        "count": count,
        "elapsed_ms": elapsed_ms,
    })


if __name__ == "__main__":
    main()
