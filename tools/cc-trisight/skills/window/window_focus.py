"""K-13 window_focus -- Focus a window by title."""
import argparse
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "_shared"))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "_shared"))

from output import success, error
from session import log_skill_call, log_skill_result

SKILL_NAME = "window_focus"


def main() -> None:
    parser = argparse.ArgumentParser(description="Focus a window")
    parser.add_argument("--window", "-w", required=True, help="Window title or substring to match")
    parser.add_argument("--delay", type=float, default=0.2, help="Seconds to wait after focusing (default 0.2)")
    args = parser.parse_args()

    log_skill_call(SKILL_NAME, vars(args))

    start = time.perf_counter()
    safe_title = args.window.replace("'", "''")
    cmd = f"$wshell = New-Object -ComObject wscript.shell; $wshell.AppActivate('{safe_title}')"

    try:
        proc = subprocess.run(
            ["powershell.exe", "-Command", cmd],
            capture_output=True, text=True, timeout=10,
        )
        time.sleep(args.delay)
        elapsed_ms = int((time.perf_counter() - start) * 1000)

        # AppActivate returns True/False — stdout contains the result
        result = proc.stdout.strip()
        if result == "False":
            log_skill_result(SKILL_NAME, False, f"Window not found: {args.window}")
            error(SKILL_NAME, f"Window not found: {args.window}")

        log_skill_result(SKILL_NAME, True, f"Focused: {args.window}")
        success(SKILL_NAME, {
            "window": args.window,
            "elapsed_ms": elapsed_ms,
        })
    except subprocess.TimeoutExpired:
        log_skill_result(SKILL_NAME, False, "Command timed out")
        error(SKILL_NAME, "PowerShell command timed out")
    except FileNotFoundError:
        log_skill_result(SKILL_NAME, False, "PowerShell not found")
        error(SKILL_NAME, "PowerShell not found. Install Windows PowerShell.")
    except subprocess.SubprocessError as e:
        log_skill_result(SKILL_NAME, False, str(e))
        error(SKILL_NAME, f"Failed to focus window: {e}")


if __name__ == "__main__":
    main()
