"""Wrapper for cc-click.exe subprocess calls.

Usage:
    from cc_click import run
    exit_code, stdout, stderr, elapsed_ms = run("list-windows")
    exit_code, stdout, stderr, elapsed_ms = run("click", {"-w": "Notepad", "--name": "File"})
"""
import subprocess
import time

from config import get_cc-click_path


def run(subcommand: str, args: dict[str, str | None] | None = None,
        timeout: int = 30) -> tuple[int, str, str, int]:
    """Run a cc-click subcommand.

    Args:
        subcommand: The cc-click subcommand to run (e.g., "click", "list-windows").
        args: Optional dict of arguments to pass to the subcommand.
        timeout: Maximum seconds to wait for the command to complete.

    Returns:
        Tuple of (exit_code, stdout, stderr, elapsed_ms).
    """
    cc-click = get_cc-click_path()
    cmd = [cc-click, subcommand]

    if args:
        for k, v in args.items():
            cmd.append(k)
            if v is not None:
                cmd.append(str(v))

    start = time.perf_counter()
    try:
        proc = subprocess.run(
            cmd,
            capture_output=True,
            text=True,
            timeout=timeout,
        )
        elapsed = int((time.perf_counter() - start) * 1000)
        return proc.returncode, proc.stdout.strip(), proc.stderr.strip(), elapsed
    except subprocess.TimeoutExpired:
        elapsed = int((time.perf_counter() - start) * 1000)
        return 1, "", f"cc-click timed out after {timeout}s", elapsed
    except FileNotFoundError:
        elapsed = int((time.perf_counter() - start) * 1000)
        return 1, "", f"cc-click.exe not found at: {cc-click}", elapsed
