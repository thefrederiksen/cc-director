"""K-15 window_minimize_all -- Minimize all windows to show clean desktop."""
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "_shared"))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "_shared"))

from output import success, error
from session import log_skill_call, log_skill_result

SKILL_NAME = "window_minimize_all"


def main() -> None:
    log_skill_call(SKILL_NAME, {})

    start = time.perf_counter()
    try:
        cmd = "$shell = New-Object -ComObject Shell.Application; $shell.MinimizeAll()"
        proc = subprocess.run(
            ["powershell.exe", "-Command", cmd],
            capture_output=True, text=True, timeout=10,
        )
        time.sleep(0.3)  # Give windows time to minimize
        elapsed_ms = int((time.perf_counter() - start) * 1000)

        if proc.returncode != 0:
            log_skill_result(SKILL_NAME, False, proc.stderr.strip())
            error(SKILL_NAME, f"MinimizeAll failed: {proc.stderr.strip()}")

        log_skill_result(SKILL_NAME, True, "All windows minimized")
        success(SKILL_NAME, {"elapsed_ms": elapsed_ms})
    except subprocess.TimeoutExpired:
        log_skill_result(SKILL_NAME, False, "Command timed out")
        error(SKILL_NAME, "PowerShell command timed out")
    except FileNotFoundError:
        log_skill_result(SKILL_NAME, False, "PowerShell not found")
        error(SKILL_NAME, "PowerShell not found. Install Windows PowerShell.")
    except subprocess.SubprocessError as e:
        log_skill_result(SKILL_NAME, False, str(e))
        error(SKILL_NAME, f"Failed to minimize all: {e}")


if __name__ == "__main__":
    main()
