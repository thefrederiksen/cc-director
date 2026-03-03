"""LinkedIn Auto-Connect Orchestration Script.

Scheduled by cc_scheduler to run at 7 AM weekdays.
Adds a random delay (0-120 min) so actual execution is 7:00-9:00 AM,
ensures cc-browser daemon is running, then invokes cc-linkedin auto-connect.

Exit codes:
    0 - Success
    1 - Browser/login error
    2 - Rate limited or other failure
"""

import random
import subprocess
import sys
import time

# -- Configuration -----------------------------------------------------------
# Set MIN_DELAY=0 and MAX_DELAY=0 for testing (skip random wait)
MIN_DELAY = int(sys.argv[1]) if len(sys.argv) > 1 else 0
MAX_DELAY = int(sys.argv[2]) if len(sys.argv) > 2 else 7200  # 120 minutes
MIN_COUNT = 3
MAX_COUNT = 5


def main() -> int:
    # Step 1: Random delay to spread execution across 7-9 AM window
    delay_seconds = random.randint(MIN_DELAY, MAX_DELAY)
    delay_minutes = delay_seconds // 60
    print(f"[linkedin-auto-connect] Random delay: {delay_minutes}m {delay_seconds % 60}s")

    if delay_seconds > 0:
        time.sleep(delay_seconds)

    print("[linkedin-auto-connect] Starting...")

    # Step 2: Ensure cc-browser daemon is running
    try:
        result = subprocess.run(
            ["cc-browser", "status"],
            capture_output=True, text=True, timeout=15
        )
        if result.returncode != 0 or "running" not in result.stdout.lower():
            print("[linkedin-auto-connect] Browser daemon not running, starting...")
            start_result = subprocess.run(
                ["cc-browser", "start", "--workspace", "linkedin"],
                capture_output=True, text=True, timeout=30
            )
            if start_result.returncode != 0:
                print(f"[linkedin-auto-connect] ERROR: Failed to start browser daemon")
                print(start_result.stderr)
                return 1
            # Give browser time to fully initialize
            time.sleep(5)
    except FileNotFoundError:
        print("[linkedin-auto-connect] ERROR: cc-browser not found on PATH")
        return 1
    except subprocess.TimeoutExpired:
        print("[linkedin-auto-connect] ERROR: cc-browser timed out")
        return 1

    # Step 3: Run auto-connect with random count
    count = random.randint(MIN_COUNT, MAX_COUNT)
    print(f"[linkedin-auto-connect] Connecting with {count} people...")

    try:
        result = subprocess.run(
            ["cc-linkedin", "auto-connect", "--count", str(count)],
            capture_output=False,  # Let output flow to stdout for scheduler logging
            timeout=600,  # 10 min should be more than enough for 3-5 connections
        )
        if result.returncode != 0:
            print(f"[linkedin-auto-connect] ERROR: cc-linkedin exited with code {result.returncode}")
            return 2
    except FileNotFoundError:
        print("[linkedin-auto-connect] ERROR: cc-linkedin not found on PATH")
        return 1
    except subprocess.TimeoutExpired:
        print("[linkedin-auto-connect] ERROR: cc-linkedin timed out after 10 minutes")
        return 2

    print("[linkedin-auto-connect] Done.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
