"""Repository and worktree listing - the fleet fact any agent can ask for in one call.

Backed by the Gateway's /repositories and /worktrees, which aggregate every machine in the account.
Read-only - reaping always runs on the owning Director with a live re-verify.

Remove-the-network-port mission, phase 2: this used to go through the Director's /fleet/* relay on the
same machine, which forwarded here. The middleman is gone; there is no standalone answer any more,
because there is no local path - a machine with no Gateway has no fleet tooling, which is the cost the
mission accepts and the error message names.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any, Dict, List

import typer
from rich import box
from rich.console import Console
from rich.table import Table

# Make cc_shared importable when running from source, matching the existing cc-* tools.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import gateway  # noqa: E402

console = Console()


def _get(path: str) -> List[Dict[str, Any]]:
    try:
        rows = gateway.get_json(path) or []
    except gateway.GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)
    if isinstance(rows, dict) and rows.get("error"):
        console.print(f"[red]Error:[/red] {rows['error']}")
        raise typer.Exit(1)
    return rows


def _raw(dto: Dict[str, Any], *keys: str) -> Any:
    """First present key's RAW value (gateway.field stringifies - wrong for bools/ints/lists)."""
    for key in keys:
        if key in dto and dto[key] is not None:
            return dto[key]
    return None


def _int(dto: Dict[str, Any], *keys: str) -> int:
    try:
        return int(_raw(dto, *keys) or 0)
    except (TypeError, ValueError):
        return 0


def _gb(size: Any) -> str:
    try:
        bytes_ = int(size)
    except (TypeError, ValueError):
        return "-"
    if bytes_ >= 1_073_741_824:
        return f"{bytes_ / 1_073_741_824:.1f}G"
    if bytes_ >= 1_048_576:
        return f"{bytes_ / 1_048_576:.0f}M"
    return f"{bytes_ / 1024:.0f}K" if bytes_ > 0 else "0"


def list_repositories(json_output: bool, dirty_only: bool = False) -> None:
    rows = _get("repositories")
    if dirty_only:
        rows = [r for r in rows if not _raw(r, "isClean", "IsClean")]
    if json_output:
        print(json.dumps(rows, indent=2))
        return

    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    for col in ("REPOSITORY", "MACHINE", "PROVIDER", "BRANCH", "STATE", "WORKTREES", "SIZE(WT)"):
        table.add_column(col)
    for r in rows:
        clean = bool(_raw(r, "isClean", "IsClean"))
        uncommitted = _int(r, "uncommittedCount", "UncommittedCount")
        state = "clean" if clean else f"{uncommitted} uncommitted"
        wt_total = _int(r, "worktreeCount", "WorktreeCount")
        wt_safe = _int(r, "worktreesSafeToReap", "WorktreesSafeToReap")
        wt = "-" if not wt_total else f"{wt_total} ({wt_safe} safe)"
        table.add_row(
            str(gateway.field(r, "name", "Name") or "-"),
            str(gateway.field(r, "machineName", "MachineName") or "-"),
            str(gateway.field(r, "provider", "Provider") or "-"),
            str(gateway.field(r, "branch", "Branch") or "-"),
            state,
            wt,
            _gb(_raw(r, "worktreeBytes", "WorktreeBytes")),
        )
    console.print(table)
    safe_total = sum(_int(r, "worktreesSafeToReap", "WorktreesSafeToReap") for r in rows)
    console.print(f"{len(rows)} repositories - {safe_total} worktrees safe to reap")


def list_worktrees(json_output: bool, repo: str | None = None, state: str | None = None) -> None:
    path = "worktrees"
    rows = _get(path)
    if repo:
        rows = [w for w in rows if str(gateway.field(w, "repoName", "RepoName") or "").lower() == repo.lower()]
    if state:
        rows = [w for w in rows if str(gateway.field(w, "state", "State") or "").lower() == state.lower()]
    if json_output:
        print(json.dumps(rows, indent=2))
        return

    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    for col in ("REPO", "BRANCH", "MACHINE", "STATE", "SESSION", "SIZE", "REASON"):
        table.add_column(col)
    reclaim = 0
    for w in rows:
        w_state = str(gateway.field(w, "state", "State") or "-")
        sessions = _raw(w, "sessionLabels", "SessionLabels") or []
        if w_state == "safe-to-reap":
            reclaim += _int(w, "sizeBytes", "SizeBytes")
        table.add_row(
            str(gateway.field(w, "repoName", "RepoName") or "-"),
            str(gateway.field(w, "branch", "Branch") or "(detached)"),
            str(gateway.field(w, "machineName", "MachineName") or "-"),
            w_state,
            ", ".join(sessions) if sessions else "-",
            _gb(_raw(w, "sizeBytes", "SizeBytes")),
            str(gateway.field(w, "reason", "Reason") or "-"),
        )
    console.print(table)
    safe = sum(1 for w in rows if str(gateway.field(w, "state", "State")) == "safe-to-reap")
    console.print(
        f"{len(rows)} worktrees - {safe} safe ({_gb(reclaim)} reclaimable) - "
        "reap runs on the owning Director"
    )
