"""Automation-browser operations for cc-devthrottle.

These verbs manage DevThrottle's OWN drivable browsers - the signed-in-once Chromium instances an
agent attaches to through browser-harness. They act on the LOCAL Director (CC_DIRECTOR_API): a
browser's debug port is loopback and its data directory is on this machine, so browsers are
machine-local and only an agent on the same machine can drive them.

All rendering decisions the user sees (status label, account, the attach environment) are FOLDED by
the Director and returned on the DTO; this module only lays them out.
"""

from __future__ import annotations

import json
from typing import Any, Dict, List, Optional

import typer
from rich import box
from rich.console import Console
from rich.table import Table

# session_ops installs the ASCII-only Rich patch at import; cli imports it eagerly, so tables here
# render with plain ASCII too. cc_shared.director is the loopback Control-API client.
from cc_shared import director

console = Console()


def _browsers() -> List[Dict[str, Any]]:
    """Every automation browser on this machine, each already folded (status/account/attach)."""
    payload = director.get_json("browsers") or {}
    return payload.get("browsers", payload.get("Browsers", [])) or []


def _resolve(target: str) -> Dict[str, Any]:
    """Resolve a user-typed id or name to its browser DTO, or exit with a helpful list.

    Resolving here (rather than passing a raw name into the URL) keeps every subsequent call on the
    URL-safe slug id and lets us name the available browsers when nothing matches.
    """
    browsers = _browsers()
    key = target.strip().lower()
    for b in browsers:
        if director.field(b, "id", "Id").lower() == key:
            return b
    for b in browsers:
        if director.field(b, "name", "Name").lower() == key:
            return b

    if browsers:
        names = ", ".join(f'"{director.field(b, "name", "Name")}"' for b in browsers)
        console.print(f'[red]No automation browser matching "{target}".[/red] On this machine: {names}.')
    else:
        console.print(
            f'[red]No automation browser matching "{target}".[/red] '
            "There are none on this machine yet - create one with "
            "'cc-devthrottle browser create --name \"...\" --browser chrome'."
        )
    raise typer.Exit(code=1)


def list_browsers(json_output: bool) -> None:
    """List the automation browsers on this machine."""
    browsers = _browsers()

    if json_output:
        print(json.dumps(browsers, indent=2))
        return

    if not browsers:
        console.print(
            "No automation browsers on this machine yet. Create one with:\n"
            '  cc-devthrottle browser create --name "Center Consulting" --browser chrome'
        )
        return

    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    table.add_column("NAME")
    table.add_column("BROWSER")
    table.add_column("STATUS")
    table.add_column("ACCOUNT")
    for b in browsers:
        name = director.field(b, "name", "Name") or "(unnamed)"
        kind = director.field(b, "browser", "Browser") or "-"
        status = director.field(b, "statusLabel", "StatusLabel") or director.field(b, "status", "Status") or "-"
        account = director.field(b, "account", "Account") or "-"
        table.add_row(name, kind, status, account)

    console.print(table)


def create_browser(name: str, browser: str, json_output: bool) -> None:
    """Register a new drivable browser (does not launch it)."""
    dto = director.post_json("browsers", {"name": name, "browser": browser})
    if json_output:
        print(json.dumps(dto, indent=2))
        return
    bname = director.field(dto, "name", "Name")
    kind = director.field(dto, "browser", "Browser")
    console.print(
        f'[green]Created[/green] browser "{bname}" ({kind}).\n'
        f'Sign it in once with:  cc-devthrottle browser signin "{bname}"'
    )


def signin_browser(target: str, done: bool, json_output: bool) -> None:
    """Open the account page for a one-time human sign-in, or (with --done) record it complete."""
    browser = _resolve(target)
    bid = director.field(browser, "id", "Id")
    dto = director.post_json(f"browsers/{bid}/signin", {"done": bool(done)})
    if json_output:
        print(json.dumps(dto, indent=2))
        return

    bname = director.field(dto, "name", "Name")
    if done:
        console.print(f'[green]Recorded[/green]: "{bname}" is signed in and ready to drive.')
    else:
        console.print(
            f'Opened the sign-in page in "{bname}". Sign in BY HAND in that window (credentials are '
            "never automated).\n"
            f'When the account is signed in, run:  cc-devthrottle browser signin "{bname}" --done'
        )


def start_browser(target: str, json_output: bool) -> None:
    """Launch the browser if it is down, then print how to attach to it."""
    browser = _resolve(target)
    bid = director.field(browser, "id", "Id")
    dto = director.post_json(f"browsers/{bid}/start", {})
    if json_output:
        print(json.dumps(dto, indent=2))
        return

    bname = director.field(dto, "name", "Name")
    status = director.field(dto, "statusLabel", "StatusLabel")
    bu_name = director.field(dto, "buName", "BuName")
    bu_url = director.field(dto, "buCdpUrl", "BuCdpUrl")
    console.print(f'[green]Started[/green] "{bname}" ({status}). Attach the harness with:')
    console.print(f'  eval "$(cc-devthrottle browser attach \'{bname}\')"')
    console.print(f"    BU_NAME={bu_name}")
    console.print(f"    BU_CDP_URL={bu_url}")


def attach_browser(target: str) -> None:
    """Print ONLY the two export lines, so `eval "$(... attach 'X')"` points the harness at it."""
    browser = _resolve(target)
    bid = director.field(browser, "id", "Id")
    info = director.get_json(f"browsers/{bid}/attach")
    bu_name = director.field(info, "buName", "BuName")
    bu_url = director.field(info, "buCdpUrl", "BuCdpUrl")
    # Plain print, no Rich: this output is meant to be eval'd by a shell.
    print(f"export BU_NAME={bu_name}")
    print(f"export BU_CDP_URL={bu_url}")


def rename_browser(target: str, to: str, json_output: bool) -> None:
    """Rename a browser's label (its id, port, and folder are unchanged)."""
    browser = _resolve(target)
    bid = director.field(browser, "id", "Id")
    dto = director.post_json(f"browsers/{bid}/rename", {"name": to})
    if json_output:
        print(json.dumps(dto, indent=2))
        return
    console.print(f'[green]Renamed[/green] to "{director.field(dto, "name", "Name")}".')


def remove_browser(target: str, json_output: bool) -> None:
    """Stop the browser, delete its folder, and drop it from the registry."""
    browser = _resolve(target)
    bid = director.field(browser, "id", "Id")
    bname = director.field(browser, "name", "Name")
    result = director.delete(f"browsers/{bid}")
    if json_output:
        print(json.dumps(result, indent=2))
        return
    console.print(f'[green]Removed[/green] "{bname}".')
