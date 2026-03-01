"""Wrapper for TrisightCli.exe subprocess calls.

Usage:
    from trisight_cli import run
    exit_code, stdout, stderr, elapsed_ms = run("detect", {"--window": "Notepad", "--screenshot": "path.png"})
"""
import subprocess
import time

from config import get_trisight_cli_path


def run(subcommand: str, args: dict[str, str | None] | None = None,
        timeout: int = 60) -> tuple[int, str, str, int]:
    """Run a TrisightCli subcommand.

    Args:
        subcommand: The TrisightCli subcommand to run (e.g., "detect", "ocr", "uia").
        args: Optional dict of arguments to pass to the subcommand.
        timeout: Maximum seconds to wait for the command to complete.

    Returns:
        Tuple of (exit_code, stdout, stderr, elapsed_ms).
    """
    cli_path = get_trisight_cli_path()
    cmd = [cli_path, subcommand]

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
        return 1, "", f"TrisightCli timed out after {timeout}s", elapsed
    except FileNotFoundError:
        elapsed = int((time.perf_counter() - start) * 1000)
        return 1, "", f"TrisightCli not found at: {cli_path}", elapsed
