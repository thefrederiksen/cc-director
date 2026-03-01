"""K-10 desktop_launch -- Launch an application."""
import argparse
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "_shared"))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "_shared"))

from output import success, error
from session import log_skill_call, log_skill_result

SKILL_NAME = "desktop_launch"


def main() -> None:
    parser = argparse.ArgumentParser(description="Launch an application")
    parser.add_argument("--app", required=True, help="Application name or path (e.g., 'notepad', 'outlook', 'C:\\\\Program Files\\\\...\\\\app.exe')")
    parser.add_argument("--args", dest="app_args", default="", help="Arguments to pass to the application")
    parser.add_argument("--wait", type=float, default=1.0, help="Seconds to wait after launch (default 1.0)")
    args = parser.parse_args()

    log_skill_call(SKILL_NAME, vars(args))

    start = time.perf_counter()
    try:
        subprocess.Popen(
            f'start "" "{args.app}" {args.app_args}',
            shell=True,
        )
        time.sleep(args.wait)
        elapsed_ms = int((time.perf_counter() - start) * 1000)

        log_skill_result(SKILL_NAME, True, f"Launched {args.app}")
        success(SKILL_NAME, {
            "app": args.app,
            "args": args.app_args,
            "elapsed_ms": elapsed_ms,
        })
    except FileNotFoundError:
        log_skill_result(SKILL_NAME, False, f"Application not found: {args.app}")
        error(SKILL_NAME, f"Application not found: {args.app}")
    except OSError as e:
        log_skill_result(SKILL_NAME, False, str(e))
        error(SKILL_NAME, f"Failed to launch {args.app}: {e}")


if __name__ == "__main__":
    main()
