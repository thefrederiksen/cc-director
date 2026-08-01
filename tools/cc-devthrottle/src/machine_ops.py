"""Machines: find what is installed on another computer, find a file on it, and start something there.

Backed by the Director's /fleet/machines relays, which forward to the Gateway's machine routes and on to
that machine's cc-launcher. Every call is scoped to the calling account by the Gateway, so a machine name
only ever reaches a machine this account registered.

Read the search commands as questions and `launch` as an instruction: `machine apps` and `machine files`
change nothing, while `machine launch` starts a program on a computer you may not be sitting at.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

import typer
from rich import box
from rich.console import Console
from rich.table import Table

# Make cc_shared importable when running from source, matching the existing cc-* tools.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import director  # noqa: E402

console = Console()


def _fail(message: str) -> None:
    console.print(f"[red]Error:[/red] {message}")
    raise typer.Exit(1)


def _call(path: str) -> Any:
    try:
        payload = director.get_json(path)
    except director.DirectorError as err:
        _fail(str(err))
    if isinstance(payload, dict) and payload.get("error"):
        _fail(str(payload["error"]))
    return payload


def _size(size: Any) -> str:
    try:
        count = int(size)
    except (TypeError, ValueError):
        return "-"
    if count >= 1_073_741_824:
        return f"{count / 1_073_741_824:.1f}G"
    if count >= 1_048_576:
        return f"{count / 1_048_576:.1f}M"
    if count >= 1024:
        return f"{count / 1024:.0f}K"
    return str(count)


def list_machines(json_output: bool) -> None:
    """Every machine this account can search and start things on."""
    rows: List[Dict[str, Any]] = _call("fleet/machines") or []
    if json_output:
        print(json.dumps(rows, indent=2))
        return

    if not rows:
        console.print(
            "No machines are registered. A machine appears here once cc-launcher is running on it "
            "and has registered with the Gateway."
        )
        return

    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    for column in ("MACHINE", "PORT", "ADDRESS", "VERSION", "LAST SEEN"):
        table.add_column(column)
    for row in rows:
        table.add_row(
            str(director.field(row, "machineName", "MachineName") or "-"),
            str(director.field(row, "port", "Port") or "-"),
            str(director.field(row, "networkAddress", "NetworkAddress") or "(same machine)"),
            str(director.field(row, "version", "Version") or "-"),
            str(director.field(row, "lastSeenUtc", "LastSeenUtc") or "-"),
        )
    console.print(table)
    console.print(f"{len(rows)} machines")


def list_apps(machine: str, query: Optional[str], limit: int, json_output: bool) -> None:
    """What is installed on one machine."""
    path = f"fleet/machines/{machine}/apps?q={query or ''}&limit={limit}"
    payload: Dict[str, Any] = _call(path) or {}
    apps: List[Dict[str, Any]] = payload.get("apps") or payload.get("Apps") or []

    if json_output:
        print(json.dumps(payload, indent=2))
        return

    if not apps:
        console.print(f"Nothing on {machine} matches {query or '(everything)'}.")
        return

    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    for column in ("APPLICATION", "SOURCE", "PATH"):
        table.add_column(column)
    for app in apps:
        table.add_row(
            str(director.field(app, "name", "Name") or "-"),
            str(director.field(app, "source", "Source") or "-"),
            str(director.field(app, "path", "Path") or "-"),
        )
    console.print(table)

    total = payload.get("totalMatches", payload.get("TotalMatches", len(apps)))
    line = f"{len(apps)} of {total} on {machine}"
    if payload.get("truncated") or payload.get("Truncated"):
        line += " - more matched than were returned; narrow the search or raise --count"
    console.print(line)

    # An unreadable directory means the catalogue is short by an unknown amount. Say so: a quietly
    # incomplete list looks exactly like a machine with less installed on it.
    skipped = payload.get("skipped") or payload.get("Skipped") or []
    if skipped:
        console.print(f"[yellow]{len(skipped)} directories could not be read, so this list may be incomplete.[/yellow]")


def search_files(machine: str, query: str, limit: int, timeout_seconds: int, json_output: bool) -> None:
    """Find files by name on one machine."""
    path = (
        f"fleet/machines/{machine}/files?q={query}"
        f"&limit={limit}&timeoutMilliseconds={timeout_seconds * 1000}"
    )
    payload: Dict[str, Any] = _call(path) or {}
    files: List[Dict[str, Any]] = payload.get("files") or payload.get("Files") or []

    if json_output:
        print(json.dumps(payload, indent=2))
        return

    if not files:
        console.print(f"No file on {machine} matches {query}.")
    else:
        table = Table(show_header=True, header_style="bold", box=box.ASCII)
        for column in ("FILE", "SIZE", "MODIFIED", "PATH"):
            table.add_column(column)
        for hit in files:
            modified = str(director.field(hit, "modifiedUtc", "ModifiedUtc") or "-")
            table.add_row(
                str(director.field(hit, "name", "Name") or "-"),
                _size(hit.get("sizeBytes", hit.get("SizeBytes"))),
                modified[:19].replace("T", " "),
                str(director.field(hit, "path", "Path") or "-"),
            )
        console.print(table)

    elapsed = payload.get("elapsedMilliseconds", payload.get("ElapsedMilliseconds", 0))
    visited = payload.get("directoriesVisited", payload.get("DirectoriesVisited", 0))
    console.print(f"{len(files)} files - searched {visited} directories on {machine} in {elapsed} ms")

    # The whole point of the truncation fields: a partial answer must never read as a complete one, and the
    # advice differs by reason - a ceiling wants a narrower search, a deadline wants more time.
    if payload.get("truncated") or payload.get("Truncated"):
        reason = payload.get("truncationReason") or payload.get("TruncationReason") or "unknown"
        if reason == "limit":
            console.print(
                "[yellow]Stopped at the result limit - this is NOT the whole answer. "
                "Narrow the search or raise --count.[/yellow]"
            )
        elif reason == "timeout":
            console.print(
                "[yellow]Stopped at the time limit - this is NOT the whole answer. "
                "Narrow the search or raise --seconds.[/yellow]"
            )
        else:
            console.print(f"[yellow]Stopped early ({reason}) - this is NOT the whole answer.[/yellow]")

    unreadable = payload.get("unreadableDirectories", payload.get("UnreadableDirectories", 0))
    if unreadable:
        console.print(f"[yellow]{unreadable} directories could not be read and were not searched.[/yellow]")


def launch(machine: str, app: Optional[str], path: Optional[str], args: Optional[str],
           cwd: Optional[str], headless: bool, json_output: bool) -> None:
    """Start an application on one machine, by catalogue name or by absolute path."""
    if not app and not path:
        _fail("Name what to start: --app \"Chrome\" or --path \"C:\\\\Tools\\\\thing.exe\".")

    # confirmProtected carries this command's explicit intent through the relay: the Gateway refuses any
    # launch without it (tenant-boundary hardening, CR-5). Typing `machine launch` IS the confirmation -
    # the flag exists to stop programs being started as a side effect of something else.
    body = {"app": app, "path": path, "args": args, "cwd": cwd, "headless": headless,
            "confirmProtected": True}
    try:
        payload = director.post_json(f"fleet/machines/{machine}/launch", body, timeout=60)
    except director.DirectorError as err:
        _fail(str(err))

    if json_output:
        print(json.dumps(payload, indent=2))
        return

    if isinstance(payload, dict) and payload.get("error"):
        _fail(str(payload["error"]))

    console.print(f"Started {app or path} on {machine}.")
