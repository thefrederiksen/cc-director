"""Mission operations for cc-devthrottle.

A Mission is a first-class persisted record on the local Director that sessions attach to
(see docs/new_architecture/mission-as-first-class-unit-of-work.md). These commands create and
list Mission records via the Director Control API (POST /missions, GET /missions), mirroring the
session/schedule command style.
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


def create_mission(name: str, parent: Optional[str]) -> None:
    """Create a Mission record on the local Director and print its id."""
    body: Dict[str, Any] = {"missionName": name}
    if parent:
        body["parentMissionId"] = parent

    try:
        resp = director.post_json("missions", body)
    except director.DirectorError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    mid = director.field(resp, "missionId", "MissionId")
    if not mid:
        console.print("[red]Error:[/red] the Director did not return a mission id.")
        raise typer.Exit(1)

    label = director.field(resp, "missionName", "MissionName") or name
    console.print(f"[green]Created[/green] mission ({label}).")
    console.print(f"id: {mid}")
    console.print(
        f'Attach a session at spawn:  cc-devthrottle session spawn <repo> --mission {mid}'
    )


def list_missions(json_output: bool) -> None:
    """List every Mission record on the local Director."""
    try:
        missions = director.get_json("missions") or []
    except director.DirectorError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    if json_output:
        print(json.dumps(missions, indent=2))
        return

    if not missions:
        console.print(
            "No missions on this Director yet. Create one with 'cc-devthrottle mission create <name>'."
        )
        return

    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    table.add_column("Id")
    table.add_column("Name")
    table.add_column("Parent")

    for mission in missions:
        mid = director.field(mission, "missionId", "MissionId")
        name = director.field(mission, "missionName", "MissionName") or "-"
        parent = director.field(mission, "parentMissionId", "ParentMissionId") or "-"
        table.add_row(director.short_id(mid) if mid else "-", name, parent)
    console.print(table)
