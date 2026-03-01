"""K-09 desktop_shortcut -- Send a keyboard shortcut (Ctrl+S, Alt+F4, etc.)."""
import argparse
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "_shared"))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "_shared"))

from output import success, error
from session import log_skill_call, log_skill_result

SKILL_NAME = "desktop_shortcut"

# Map friendly key names to SendKeys format
_KEY_MAP = {
    "enter": "{ENTER}", "tab": "{TAB}", "escape": "{ESC}", "esc": "{ESC}",
    "backspace": "{BACKSPACE}", "delete": "{DELETE}", "del": "{DELETE}",
    "up": "{UP}", "down": "{DOWN}", "left": "{LEFT}", "right": "{RIGHT}",
    "home": "{HOME}", "end": "{END}", "pageup": "{PGUP}", "pagedown": "{PGDN}",
    "f1": "{F1}", "f2": "{F2}", "f3": "{F3}", "f4": "{F4}",
    "f5": "{F5}", "f6": "{F6}", "f7": "{F7}", "f8": "{F8}",
    "f9": "{F9}", "f10": "{F10}", "f11": "{F11}", "f12": "{F12}",
}


def _to_sendkeys(shortcut: str) -> str:
    """Convert 'Ctrl+Shift+S' format to SendKeys '^+s' format."""
    parts = [p.strip().lower() for p in shortcut.split("+")]
    modifiers = ""
    key_part = ""

    for p in parts:
        if p == "ctrl":
            modifiers += "^"
        elif p == "shift":
            modifiers += "+"
        elif p == "alt":
            modifiers += "%"
        else:
            key_part = _KEY_MAP.get(p, p)

    return modifiers + key_part


def main() -> None:
    parser = argparse.ArgumentParser(description="Send a keyboard shortcut")
    parser.add_argument("--shortcut", "-s", required=True,
                        help="Shortcut in 'Ctrl+S' format (e.g., 'Ctrl+Shift+S', 'Alt+F4', 'Ctrl+Enter')")
    parser.add_argument("--window", "-w", help="Window title to focus first")
    parser.add_argument("--delay", type=float, default=0.2, help="Seconds to wait after focusing window (default 0.2)")
    args = parser.parse_args()

    log_skill_call(SKILL_NAME, vars(args))

    start = time.perf_counter()
    try:
        # Focus window first if specified
        if args.window:
            safe_title = args.window.replace("'", "''")
            focus_cmd = f"$wshell = New-Object -ComObject wscript.shell; $wshell.AppActivate('{safe_title}')"
            subprocess.run(
                ["powershell.exe", "-Command", focus_cmd],
                capture_output=True, text=True, timeout=10,
            )
            time.sleep(args.delay)

        sendkeys_fmt = _to_sendkeys(args.shortcut)
        ps_keys = sendkeys_fmt.replace("'", "''")
        cmd = f"Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.SendKeys]::SendWait('{ps_keys}')"

        proc = subprocess.run(
            ["powershell.exe", "-Command", cmd],
            capture_output=True, text=True, timeout=10,
        )
        elapsed_ms = int((time.perf_counter() - start) * 1000)

        if proc.returncode != 0:
            log_skill_result(SKILL_NAME, False, proc.stderr.strip())
            error(SKILL_NAME, f"Shortcut failed: {proc.stderr.strip()}")

        log_skill_result(SKILL_NAME, True, f"Sent {args.shortcut}")
        success(SKILL_NAME, {
            "shortcut": args.shortcut,
            "sendkeys_format": sendkeys_fmt,
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
        error(SKILL_NAME, f"Failed to send shortcut: {e}")


if __name__ == "__main__":
    main()
