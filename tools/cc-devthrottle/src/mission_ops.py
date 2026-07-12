"""Mission operations for cc-devthrottle.

A Mission is a first-class persisted record that a pod of sessions is collectively chartered to
accomplish (see docs/new_architecture/mission-as-first-class-unit-of-work.md). Missions are a
FLEET-level concept - they span Directors and machines and nest - so their source of truth lives at
the GATEWAY, like fleet messaging and scheduling, not on any one Director (Gateway Cleanup mission,
Wave 4b). These commands create and list Mission records via the Gateway Control API
(POST /missions, GET /missions), mirroring the schedule command style.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional
from urllib.parse import urlparse

import requests
import typer
from rich import box
from rich.console import Console
from rich.table import Table

# Make cc_shared importable when running from source, matching the existing cc-* tools.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared.config import CCDirectorConfig  # noqa: E402

console = Console()
err_console = Console(stderr=True)

LOOPBACK_DEFAULT = "http://127.0.0.1:7878"
TIMEOUT_SECONDS = 10


class GatewayError(Exception):
    """A handled, user-facing failure talking to the Gateway."""


def resolve_base_url() -> str:
    config = CCDirectorConfig().load()
    url = (config.gateway.url or "").strip()
    return url.rstrip("/") if url else LOOPBACK_DEFAULT


def _auth_token() -> str:
    config = CCDirectorConfig().load()
    return (config.gateway.token or "").strip()


def _is_loopback(url: str) -> bool:
    """True when the URL targets this machine (a loopback Gateway needs no token)."""
    host = (urlparse(url).hostname or "").lower()
    return host in ("127.0.0.1", "localhost", "::1")


class MissionClient:
    """Talks to one Gateway's mission surface (POST/GET /missions)."""

    def __init__(self, base_url: Optional[str] = None) -> None:
        self.base_url = (base_url or resolve_base_url()).rstrip("/")
        self._token = _auth_token()
        # Make the auth requirement explicit instead of silently issuing an unauthenticated request to a
        # remote Gateway: a loopback Gateway on this machine needs no token, but a remote one does.
        if not self._token and not _is_loopback(self.base_url):
            raise GatewayError(
                f"Gateway URL {self.base_url} is remote but gateway.token is not set. "
                "Set it with 'cc-devthrottle settings set gateway.token <token>' "
                "(a loopback Gateway on this machine needs no token)."
            )

    def _headers(self) -> Dict[str, str]:
        headers = {"Accept": "application/json"}
        if self._token:
            headers["Authorization"] = f"Bearer {self._token}"
        return headers

    def _request(
        self, method: str, path: str, json_body: Optional[Dict[str, Any]] = None
    ) -> requests.Response:
        url = f"{self.base_url}{path}"
        try:
            return requests.request(
                method,
                url,
                json=json_body,
                headers=self._headers(),
                timeout=TIMEOUT_SECONDS,
            )
        except requests.exceptions.ConnectionError as exc:
            raise GatewayError(
                f"Gateway not reachable at {self.base_url}. "
                "Is the Gateway tray app running on this machine? "
                "If you target a remote Gateway, set gateway.url with "
                "'cc-devthrottle settings set gateway.url <url>'."
            ) from exc
        except requests.exceptions.Timeout as exc:
            raise GatewayError(
                f"Gateway at {self.base_url} did not respond within {TIMEOUT_SECONDS}s."
            ) from exc

    @staticmethod
    def _gateway_message(resp: requests.Response) -> str:
        try:
            data = resp.json()
            if isinstance(data, dict) and data.get("error"):
                return str(data["error"])
        except ValueError:
            pass
        text = (resp.text or "").strip()
        return text if text else f"Gateway returned HTTP {resp.status_code}"

    def _ok_or_raise(self, resp: requests.Response) -> Any:
        if 200 <= resp.status_code < 300:
            if not resp.content:
                return {}
            return resp.json()
        raise GatewayError(self._gateway_message(resp))

    def create(self, name: str, parent: Optional[str]) -> Dict[str, Any]:
        body: Dict[str, Any] = {"missionName": name}
        if parent:
            body["parentMissionId"] = parent
        return self._ok_or_raise(self._request("POST", "/missions", body))

    def list_all(self) -> List[Dict[str, Any]]:
        data = self._ok_or_raise(self._request("GET", "/missions"))
        return list(data) if isinstance(data, list) else []


def _field(record: Dict[str, Any], *names: str) -> Optional[str]:
    """First present, non-empty value among the given key spellings (camel/Pascal case)."""
    for name in names:
        value = record.get(name)
        if value:
            return str(value)
    return None


def _short_id(value: Optional[str]) -> str:
    if not value:
        return "-"
    return value.split("-")[0] if "-" in value else value


def create_mission(name: str, parent: Optional[str]) -> None:
    """Create a Mission record on the Gateway and print its id."""
    try:
        resp = MissionClient().create(name, parent)
    except GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    mid = _field(resp, "missionId", "MissionId")
    if not mid:
        console.print("[red]Error:[/red] the Gateway did not return a mission id.")
        raise typer.Exit(1)

    label = _field(resp, "missionName", "MissionName") or name
    console.print(f"[green]Created[/green] mission ({label}).")
    console.print(f"id: {mid}")
    console.print(
        f'Attach a session at spawn:  cc-devthrottle session spawn <repo> --mission {mid}'
    )


def list_missions(json_output: bool) -> None:
    """List every Mission record on the Gateway."""
    try:
        missions = MissionClient().list_all()
    except GatewayError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    if json_output:
        print(json.dumps(missions, indent=2))
        return

    if not missions:
        console.print(
            "No missions on the Gateway yet. Create one with 'cc-devthrottle mission create <name>'."
        )
        return

    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    table.add_column("Id")
    table.add_column("Name")
    table.add_column("Parent")

    for mission in missions:
        mid = _field(mission, "missionId", "MissionId")
        name = _field(mission, "missionName", "MissionName") or "-"
        parent = _field(mission, "parentMissionId", "ParentMissionId") or "-"
        table.add_row(_short_id(mid) if mid else "-", name, parent)
    console.print(table)
