"""K-08 desktop_keys -- Send keystrokes to the focused window via SendKeys.

Types text one character at a time with a small delay between each,
just like a human would. This prevents dropped characters in apps like
Outlook that have autocomplete or slow field initialization.
"""
import argparse
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "_shared"))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "_shared"))

from output import success, error
from session import log_skill_call, log_skill_result

SKILL_NAME = "desktop_keys"

# SendKeys special chars that need brace-escaping for literal typing
_SPECIAL = {
    "+": "{+}", "^": "{^}", "%": "{%}", "~": "{~}",
    "(": "{(}", ")": "{)}", "{": "{{}", "}": "{}}",
}

# Patterns that look like SendKeys key names — the agent probably meant desktop_shortcut
import re
_SENDKEYS_PATTERN = re.compile(
    r"\{(ESCAPE|ESC|ENTER|TAB|BACKSPACE|DELETE|DEL|UP|DOWN|LEFT|RIGHT|"
    r"HOME|END|PGUP|PGDN|F[1-9]|F1[0-2]|BREAK|INSERT|CAPSLOCK|NUMLOCK|"
    r"SCROLLLOCK|PRTSC)\}",
    re.IGNORECASE,
)


def _check_for_key_names(text: str) -> str | None:
    """Detect if text contains SendKeys-style key names like {ESCAPE}.
    Returns an error message if found, None if clean."""
    match = _SENDKEYS_PATTERN.search(text)
    if match:
        key_name = match.group(1)
        return (
            f"ERROR: desktop_keys is for typing TEXT, not pressing keys. "
            f"You passed '{{{key_name}}}' which will be typed as literal text. "
            f"To press the {key_name} key, use: "
            f"python skills/trisight/desktop/desktop_shortcut.py --shortcut \"{key_name}\""
        )
    return None


def _escape(text: str) -> str:
    """Escape SendKeys special characters for literal text input."""
    return "".join(_SPECIAL.get(c, c) for c in text)


def _build_slow_type_ps(text: str, delay_ms: int) -> str:
    """Build a PowerShell script that types one character at a time with delay.

    Supports \\n (literal backslash-n in the input) as Enter key presses,
    so the agent can type multi-line text in a single call.
    """
    lines = ["Add-Type -AssemblyName System.Windows.Forms"]
    # Split on literal \n sequences to handle newlines
    parts = text.replace("\\n", "\n").split("\n")
    for i, part in enumerate(parts):
        # Type each character in this line
        for ch in part:
            escaped = _SPECIAL.get(ch, ch)
            ps_escaped = escaped.replace("'", "''")
            lines.append(f"[System.Windows.Forms.SendKeys]::SendWait('{ps_escaped}')")
            if delay_ms > 0:
                lines.append(f"Start-Sleep -Milliseconds {delay_ms}")
        # Press Enter between lines (not after the last one)
        if i < len(parts) - 1:
            lines.append("[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')")
            if delay_ms > 0:
                lines.append(f"Start-Sleep -Milliseconds {delay_ms}")
    return "; ".join(lines)


def main() -> None:
    parser = argparse.ArgumentParser(description="Send keystrokes to the focused window")
    parser.add_argument("--keys", required=True, help="Text to type (special chars are escaped)")
    parser.add_argument("--raw", action="store_true", help="Send raw SendKeys format without escaping")
    parser.add_argument("--delay", type=int, default=30,
                        help="Milliseconds between each character (default: 30). Set to 0 for instant.")
    args = parser.parse_args()

    log_skill_call(SKILL_NAME, vars(args))

    # Guard: reject SendKeys key names like {ESCAPE} — agent should use desktop_shortcut
    if not args.raw:
        key_error = _check_for_key_names(args.keys)
        if key_error:
            log_skill_result(SKILL_NAME, False, key_error)
            error(SKILL_NAME, key_error)

    start = time.perf_counter()
    try:
        if args.raw:
            # Raw mode: send everything at once, no escaping, no per-char delay
            ps_keys = args.keys.replace("'", "''")
            cmd = f"Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.SendKeys]::SendWait('{ps_keys}')"
        elif args.delay > 0:
            # Default: type one char at a time like a human
            cmd = _build_slow_type_ps(args.keys, args.delay)
        else:
            # --delay 0: blast all at once (old behavior)
            keys = _escape(args.keys)
            ps_keys = keys.replace("'", "''")
            cmd = f"Add-Type -AssemblyName System.Windows.Forms; [System.Windows.Forms.SendKeys]::SendWait('{ps_keys}')"

        proc = subprocess.run(
            ["powershell.exe", "-Command", cmd],
            capture_output=True, text=True, timeout=30,
        )
        elapsed_ms = int((time.perf_counter() - start) * 1000)

        if proc.returncode != 0:
            log_skill_result(SKILL_NAME, False, proc.stderr.strip())
            error(SKILL_NAME, f"SendKeys failed: {proc.stderr.strip()}")

        log_skill_result(SKILL_NAME, True, f"Sent keys: {args.keys[:50]}")
        success(SKILL_NAME, {
            "keys": args.keys,
            "raw": args.raw,
            "delay_ms": args.delay,
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
        error(SKILL_NAME, f"Failed to send keys: {e}")


if __name__ == "__main__":
    main()
