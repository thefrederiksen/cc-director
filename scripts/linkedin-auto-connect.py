"""LinkedIn Auto-Connect Orchestration Script.

Scheduled by cc_scheduler to run at 7 AM weekdays.
Adds a random delay (0-120 min) so actual execution is 7:00-9:00 AM,
ensures cc-browser daemon is running, then uses cc-browser with the
LinkedIn navigation skill to send connection requests.

NOTE: cc-linkedin CLI has been removed (issue #71). This script now uses
cc-browser connections + the LinkedIn navigation skill directly. The actual
connection logic must be performed by an LLM agent using the skill's
workflow guidance. This script just ensures the browser is ready and
provides the orchestration wrapper.

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
                ["cc-browser", "daemon"],
                capture_output=True, text=True, timeout=30
            )
            if start_result.returncode != 0:
                print("[linkedin-auto-connect] ERROR: Failed to start browser daemon")
                print(start_result.stderr)
                return 1
            # Give daemon time to fully initialize
            time.sleep(5)
    except FileNotFoundError:
        print("[linkedin-auto-connect] ERROR: cc-browser not found on PATH")
        return 1
    except subprocess.TimeoutExpired:
        print("[linkedin-auto-connect] ERROR: cc-browser timed out")
        return 1

    # Step 3: Open linkedin connection (launches Chrome with LinkedIn profile)
    try:
        result = subprocess.run(
            ["cc-browser", "connections", "open", "linkedin"],
            capture_output=True, text=True, timeout=30
        )
        if result.returncode != 0:
            print(f"[linkedin-auto-connect] ERROR: Failed to open linkedin connection")
            print(result.stderr)
            return 1
    except FileNotFoundError:
        print("[linkedin-auto-connect] ERROR: cc-browser not found on PATH")
        return 1
    except subprocess.TimeoutExpired:
        print("[linkedin-auto-connect] ERROR: cc-browser timed out opening connection")
        return 1

    # Step 4: Auto-connect is now handled by LLM agent using the LinkedIn
    # navigation skill. This script ensures the browser is ready.
    # The /linkedin-connect Claude Code skill orchestrates the actual
    # connection requests using cc-browser commands with skill guidance.
    print("[linkedin-auto-connect] LinkedIn connection opened.")
    print("[linkedin-auto-connect] Use /linkedin-connect skill to send connection requests.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
