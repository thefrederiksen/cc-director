"""K-11b desktop_kill -- Kill an application by process name or window title."""
import argparse
import os
import subprocess
import sys
import time

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "_shared"))
sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "_shared"))

from output import success, error
from session import log_skill_call, log_skill_result

SKILL_NAME = "desktop_kill"


def _kill_by_process(name: str) -> dict:
    """Kill process(es) by image name. Returns {killed: [...], pids: [...]}."""
    # Ensure it ends with .exe
    if not name.lower().endswith(".exe"):
        name = name + ".exe"

    # First, find matching PIDs via tasklist
    pids = []
    try:
        result = subprocess.run(
            ["tasklist", "/FI", f"IMAGENAME eq {name}", "/FO", "CSV", "/NH"],
            capture_output=True, text=True, timeout=10,
        )
        for line in result.stdout.strip().splitlines():
            parts = line.strip().strip('"').split('","')
            if len(parts) >= 2 and parts[1].isdigit():
                pids.append(int(parts[1]))
    except subprocess.SubprocessError:
        pass  # Continue even if tasklist fails - taskkill will report actual result

    # Kill via taskkill /IM
    result = subprocess.run(
        ["taskkill", "/IM", name, "/F"],
        capture_output=True, text=True, timeout=10,
    )

    killed = [name] if result.returncode == 0 else []
    return {"killed": killed, "pids": pids, "output": result.stdout.strip()}


def _kill_by_window(title: str) -> dict:
    """Kill process(es) matching window title substring. Returns {killed: [...], pids: [...]}."""
    # Use PowerShell to find processes with matching window titles
    ps_cmd = (
        f'Get-Process | Where-Object {{$_.MainWindowTitle -like "*{title}*"}} '
        f'| Select-Object -Property Id, ProcessName, MainWindowTitle '
        f'| ConvertTo-Json -Compress'
    )
    result = subprocess.run(
        ["powershell.exe", "-NoProfile", "-Command", ps_cmd],
        capture_output=True, text=True, timeout=10,
    )

    import json
    try:
        data = json.loads(result.stdout.strip())
        if isinstance(data, dict):
            data = [data]
    except (json.JSONDecodeError, ValueError):
        data = []

    if not data:
        return {"killed": [], "pids": [], "output": f"No windows matching '{title}'"}

    killed = []
    pids = []
    for proc in data:
        pid = proc.get("Id")
        name = proc.get("ProcessName", "unknown")
        if pid:
            pids.append(pid)
            try:
                subprocess.run(
                    ["taskkill", "/F", "/PID", str(pid)],
                    capture_output=True, text=True, timeout=10,
                )
                killed.append(f"{name}.exe")
            except subprocess.SubprocessError:
                pass  # Continue killing other processes even if one fails

    return {"killed": killed, "pids": pids, "output": f"Killed {len(killed)} process(es)"}


def main() -> None:
    parser = argparse.ArgumentParser(description="Kill an application by process name or window title")
    parser.add_argument("--process", help="Process image name (e.g., 'OUTLOOK', 'notepad.exe')")
    parser.add_argument("--window", help="Window title substring to match")
    args = parser.parse_args()

    if not args.process and not args.window:
        error(SKILL_NAME, "Provide --process or --window")
        sys.exit(1)

    log_skill_call(SKILL_NAME, vars(args))

    start = time.perf_counter()
    try:
        if args.process:
            result = _kill_by_process(args.process)
        else:
            result = _kill_by_window(args.window)

        elapsed_ms = int((time.perf_counter() - start) * 1000)
        result["elapsed_ms"] = elapsed_ms

        if result["killed"]:
            log_skill_result(SKILL_NAME, True, f"Killed: {result['killed']}")
            success(SKILL_NAME, result)
        else:
            log_skill_result(SKILL_NAME, False, result.get("output", "No processes killed"))
            error(SKILL_NAME, result.get("output", "No matching processes found"))
    except subprocess.TimeoutExpired:
        log_skill_result(SKILL_NAME, False, "Command timed out")
        error(SKILL_NAME, "Kill command timed out")
    except FileNotFoundError:
        log_skill_result(SKILL_NAME, False, "Required executable not found")
        error(SKILL_NAME, "taskkill or PowerShell not found")
    except subprocess.SubprocessError as e:
        log_skill_result(SKILL_NAME, False, str(e))
        error(SKILL_NAME, f"Failed to kill: {e}")


if __name__ == "__main__":
    main()
