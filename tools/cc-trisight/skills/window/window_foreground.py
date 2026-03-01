"""K-16 window_foreground -- Get the title of the current foreground window."""
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "_shared"))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "_shared"))

from output import success, error
from session import log_skill_call, log_skill_result

SKILL_NAME = "window_foreground"

_PS_SCRIPT = r'''
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class FgWin {
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
}
"@
$h = [FgWin]::GetForegroundWindow()
if ($h -eq [IntPtr]::Zero) { Write-Output ""; exit 0 }
$len = [FgWin]::GetWindowTextLength($h)
if ($len -eq 0) { Write-Output ""; exit 0 }
$sb = New-Object System.Text.StringBuilder($len + 1)
[FgWin]::GetWindowText($h, $sb, $sb.Capacity) | Out-Null
Write-Output $sb.ToString()
'''


def main() -> None:
    log_skill_call(SKILL_NAME, {})

    start = time.perf_counter()
    try:
        proc = subprocess.run(
            ["powershell.exe", "-Command", _PS_SCRIPT],
            capture_output=True, text=True, timeout=10,
        )
        elapsed_ms = int((time.perf_counter() - start) * 1000)

        title = proc.stdout.strip()
        if not title:
            log_skill_result(SKILL_NAME, True, "No foreground window")
            success(SKILL_NAME, {"title": None, "elapsed_ms": elapsed_ms})

        log_skill_result(SKILL_NAME, True, f"Foreground: {title}")
        success(SKILL_NAME, {"title": title, "elapsed_ms": elapsed_ms})
    except subprocess.TimeoutExpired:
        log_skill_result(SKILL_NAME, False, "Command timed out")
        error(SKILL_NAME, "PowerShell command timed out")
    except FileNotFoundError:
        log_skill_result(SKILL_NAME, False, "PowerShell not found")
        error(SKILL_NAME, "PowerShell not found. Install Windows PowerShell.")
    except subprocess.SubprocessError as e:
        log_skill_result(SKILL_NAME, False, str(e))
        error(SKILL_NAME, f"Failed to get foreground window: {e}")


if __name__ == "__main__":
    main()
