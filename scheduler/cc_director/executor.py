"""Job execution via subprocess."""

import subprocess
import sys
import time
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Optional


@dataclass
class RunResult:
    """Result of a job execution."""
    exit_code: Optional[int]
    stdout: str
    stderr: str
    duration_seconds: float
    timed_out: bool
    started_at: datetime
    ended_at: datetime


def execute_job(
    command: str,
    working_dir: Optional[str] = None,
    timeout_seconds: int = 300,
) -> RunResult:
    """
    Execute a job command in a subprocess.

    Args:
        command: Command to execute
        working_dir: Working directory for the command
        timeout_seconds: Maximum runtime before killing the process

    Returns:
        RunResult with execution details
    """
    started_at = datetime.now()
    timed_out = False
    exit_code = None
    stdout = ""
    stderr = ""

    # Resolve working directory
    cwd = Path(working_dir).resolve() if working_dir else None

    # Build command for subprocess
    # On Windows, we need shell=True for commands like "python script.py"
    # On Unix, we can use shell=False with shlex.split for security
    use_shell = sys.platform == "win32"

    try:
        process = subprocess.Popen(
            command,
            shell=use_shell,
            cwd=cwd,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
        )

        try:
            stdout, stderr = process.communicate(timeout=timeout_seconds)
            exit_code = process.returncode
        except subprocess.TimeoutExpired:
            timed_out = True
            _kill_process(process)
            # Try to capture any output that was written before timeout
            try:
                stdout, stderr = process.communicate(timeout=5)
            except subprocess.TimeoutExpired:
                stdout = ""
                stderr = "Process killed due to timeout"
            exit_code = None

    except FileNotFoundError as e:
        stderr = f"Command not found: {e}"
        exit_code = 127
    except PermissionError as e:
        stderr = f"Permission denied: {e}"
        exit_code = 126
    except OSError as e:
        stderr = f"OS error: {e}"
        exit_code = 1

    ended_at = datetime.now()
    duration_seconds = (ended_at - started_at).total_seconds()

    return RunResult(
        exit_code=exit_code,
        stdout=stdout,
        stderr=stderr,
        duration_seconds=duration_seconds,
        timed_out=timed_out,
        started_at=started_at,
        ended_at=ended_at,
    )


def _kill_process(process: subprocess.Popen) -> None:
    """
    Kill a process, trying graceful termination first.

    Args:
        process: The subprocess to kill
    """
    # Try SIGTERM first (graceful)
    process.terminate()

    # Give it a moment to terminate gracefully
    try:
        process.wait(timeout=5)
    except subprocess.TimeoutExpired:
        # Force kill if it didn't terminate
        process.kill()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            pass  # Process is stuck, nothing more we can do
