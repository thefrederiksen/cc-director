"""K-14 window_position -- Position a window on the screen."""
import argparse
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "_shared"))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "_shared"))

from output import success, error
from session import log_skill_call, log_skill_result

SKILL_NAME = "window_position"

# PowerShell script to find and position a window using Win32 APIs
_PS_SCRIPT = r'''
param($Title, $Position)
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public class Win32 {
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref RECT pvParam, uint fWinIni);
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }
}
"@

$found = $null
[Win32]::EnumWindows({
    param($h, $l)
    if (-not [Win32]::IsWindowVisible($h)) { return $true }
    $len = [Win32]::GetWindowTextLength($h)
    if ($len -eq 0) { return $true }
    $sb = New-Object System.Text.StringBuilder($len + 1)
    [Win32]::GetWindowText($h, $sb, $sb.Capacity) | Out-Null
    if ($sb.ToString() -like "*$Title*") { $script:found = $h; return $false }
    return $true
}, [IntPtr]::Zero) | Out-Null

if (-not $found) { Write-Output "NOT_FOUND"; exit 0 }

$wa = New-Object Win32+RECT
[Win32]::SystemParametersInfo(0x0030, 0, [ref]$wa, 0) | Out-Null
$sw = $wa.Right - $wa.Left; $sh = $wa.Bottom - $wa.Top
$sx = $wa.Left; $sy = $wa.Top

[Win32]::ShowWindow($found, 9) | Out-Null  # SW_RESTORE
Start-Sleep -Milliseconds 100

switch ($Position) {
    "left"     { [Win32]::SetWindowPos($found, [IntPtr]::Zero, $sx, $sy, [int]($sw*0.5), $sh, 0x0044) | Out-Null }
    "right"    { [Win32]::SetWindowPos($found, [IntPtr]::Zero, [int]($sx+$sw*0.5), $sy, [int]($sw*0.5), $sh, 0x0044) | Out-Null }
    "maximize" { [Win32]::ShowWindow($found, 3) | Out-Null }
    "topleft"  { [Win32]::SetWindowPos($found, [IntPtr]::Zero, $sx, $sy, [int]($sw*0.5), [int]($sh*0.5), 0x0044) | Out-Null }
    "topright" { [Win32]::SetWindowPos($found, [IntPtr]::Zero, [int]($sx+$sw*0.5), $sy, [int]($sw*0.5), [int]($sh*0.5), 0x0044) | Out-Null }
    default    { Write-Output "INVALID_POSITION"; exit 0 }
}
[Win32]::SetForegroundWindow($found) | Out-Null
Write-Output "OK"
'''


def main() -> None:
    parser = argparse.ArgumentParser(description="Position a window on the screen")
    parser.add_argument("--window", "-w", required=True, help="Window title or substring")
    parser.add_argument("--position", "-p", required=True,
                        choices=["left", "right", "maximize", "topleft", "topright"],
                        help="Position: left, right, maximize, topleft, topright")
    args = parser.parse_args()

    log_skill_call(SKILL_NAME, vars(args))

    start = time.perf_counter()
    try:
        proc = subprocess.run(
            ["powershell.exe", "-Command", _PS_SCRIPT, "-Title", args.window, "-Position", args.position],
            capture_output=True, text=True, timeout=15,
        )
        elapsed_ms = int((time.perf_counter() - start) * 1000)
        result = proc.stdout.strip()

        if result == "NOT_FOUND":
            log_skill_result(SKILL_NAME, False, f"Window not found: {args.window}")
            error(SKILL_NAME, f"Window not found: {args.window}")
        elif result == "INVALID_POSITION":
            log_skill_result(SKILL_NAME, False, f"Invalid position: {args.position}")
            error(SKILL_NAME, f"Invalid position: {args.position}")

        log_skill_result(SKILL_NAME, True, f"Positioned {args.window} -> {args.position}")
        success(SKILL_NAME, {
            "window": args.window,
            "position": args.position,
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
        error(SKILL_NAME, f"Failed to position window: {e}")


if __name__ == "__main__":
    main()
