"""Gateway workflow operations for cc-devthrottle (Workflows mission, phase 3).

A Workflow is a fleet-wide, cross-agent unit of conduct: markdown instructions plus optional
helper files, stored and versioned on the Gateway, authored mostly by agents through this
command group. The authoring loop round-trips a skill-like directory:

    <dir>/workflow.json      metadata + steps + outcome criteria
    <dir>/instructions.md    the authoritative conduct (markdown)
    <dir>/helpers/*          optional helper files
    <dir>/.workflow-hash     sidecar written by pull; sent as If-Match on push so a stale
                             copy is refused instead of clobbering a concurrent author
"""

from __future__ import annotations

import json
import os
import socket
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

import requests
import typer
from rich import box
from rich.console import Console
from rich.table import Table
from urllib.parse import urlparse

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared.config import CCDirectorConfig  # noqa: E402

LOOPBACK_DEFAULT = "http://127.0.0.1:7878"
TIMEOUT_SECONDS = 15
WORKFLOW_JSON = "workflow.json"
INSTRUCTIONS_MD = "instructions.md"
HELPERS_DIR = "helpers"
HASH_SIDECAR = ".workflow-hash"

console = Console()
err_console = Console(stderr=True)
gateway_override: Optional[str] = None


class GatewayError(Exception):
    """A handled, user-facing failure talking to the Gateway."""


def set_gateway_override(value: Optional[str]) -> None:
    global gateway_override
    gateway_override = value.rstrip("/") if value else None


def resolve_base_url() -> str:
    config = CCDirectorConfig().load()
    url = (config.gateway.url or "").strip()
    return url.rstrip("/") if url else LOOPBACK_DEFAULT


def _auth_token() -> str:
    config = CCDirectorConfig().load()
    return (config.gateway.token or "").strip()


def _is_loopback(url: str) -> bool:
    host = (urlparse(url).hostname or "").lower()
    return host in ("127.0.0.1", "localhost", "::1")


def default_authored_by() -> str:
    session_id = (os.environ.get("CC_SESSION_ID") or "").strip()
    if session_id:
        return f"session:{session_id}"
    return f"machine:{socket.gethostname()}"


class WorkflowClient:
    """Talks to one Gateway's workflow surface."""

    def __init__(self, base_url: Optional[str] = None) -> None:
        self.base_url = (base_url or resolve_base_url()).rstrip("/")
        self._token = _auth_token()
        if not self._token and not _is_loopback(self.base_url):
            raise GatewayError(
                f"Gateway URL {self.base_url} is remote but gateway.token is not set. "
                "Set it with 'cc-devthrottle settings set gateway.token <token>' "
                "(a loopback Gateway on this machine needs no token)."
            )

    def _headers(self, extra: Optional[Dict[str, str]] = None) -> Dict[str, str]:
        headers = {"Accept": "application/json"}
        if self._token:
            headers["Authorization"] = f"Bearer {self._token}"
        if extra:
            headers.update(extra)
        return headers

    def _request(
        self,
        method: str,
        path: str,
        json_body: Optional[Dict[str, Any]] = None,
        extra_headers: Optional[Dict[str, str]] = None,
    ) -> requests.Response:
        url = f"{self.base_url}{path}"
        try:
            return requests.request(
                method,
                url,
                json=json_body,
                headers=self._headers(extra_headers),
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

    def _json_or_raise(self, resp: requests.Response) -> Dict[str, Any]:
        if 200 <= resp.status_code < 300:
            if not resp.content:
                return {}
            return resp.json()
        raise GatewayError(self._gateway_message(resp))

    def _text_or_raise(self, resp: requests.Response) -> str:
        if 200 <= resp.status_code < 300:
            return resp.text
        raise GatewayError(self._gateway_message(resp))

    # ---- reads --------------------------------------------------------------------------------

    def list_workflows(self) -> List[Dict[str, Any]]:
        data = self._json_or_raise(self._request("GET", "/gateway/workflows"))
        return list(data.get("workflows", []))

    def get_workflow(self, workflow_id: str) -> Dict[str, Any]:
        return self._json_or_raise(self._request("GET", f"/gateway/workflows/{workflow_id}"))

    def list_versions(self, workflow_id: str) -> List[Dict[str, Any]]:
        data = self._json_or_raise(
            self._request("GET", f"/gateway/workflows/{workflow_id}/versions")
        )
        return list(data.get("versions", []))

    def workflow_exists(self, workflow_id: str) -> bool:
        """True when the workflow head exists at all - drafts included. GET /{id} cannot answer
        this (it 404s for a draft-only workflow), so existence goes through the versions route."""
        resp = self._request("GET", f"/gateway/workflows/{workflow_id}/versions")
        if resp.status_code == 404:
            return False
        self._json_or_raise(resp)
        return True

    def get_version_detail(self, workflow_id: str, version: int) -> Dict[str, Any]:
        return self._json_or_raise(
            self._request("GET", f"/gateway/workflows/{workflow_id}/versions/{version}")
        )

    def get_instructions(self, workflow_id: str, version: Optional[int]) -> str:
        path = f"/gateway/workflows/{workflow_id}/instructions"
        if version is not None:
            path += f"?version={version}"
        return self._text_or_raise(self._request("GET", path))

    # ---- writes -------------------------------------------------------------------------------

    def create(self, body: Dict[str, Any]) -> Dict[str, Any]:
        return self._json_or_raise(self._request("POST", "/gateway/workflows", body))

    def update_draft(
        self, workflow_id: str, body: Dict[str, Any], if_match: Optional[str]
    ) -> Dict[str, Any]:
        extra = {"If-Match": if_match} if if_match else None
        return self._json_or_raise(
            self._request("PUT", f"/gateway/workflows/{workflow_id}/draft", body, extra)
        )

    def publish(self, workflow_id: str) -> Dict[str, Any]:
        return self._json_or_raise(
            self._request("POST", f"/gateway/workflows/{workflow_id}/publish")
        )

    def reset(self, workflow_id: str) -> Dict[str, Any]:
        return self._json_or_raise(
            self._request("POST", f"/gateway/workflows/{workflow_id}/reset")
        )

    def delete(self, workflow_id: str) -> Dict[str, Any]:
        return self._json_or_raise(
            self._request("DELETE", f"/gateway/workflows/{workflow_id}")
        )


def _fail(message: str) -> None:
    err_console.print(f"[red]Error:[/red] {message}")
    raise typer.Exit(1)


def _client() -> WorkflowClient:
    return WorkflowClient(base_url=gateway_override)


def _pick_authoring_version(versions: List[Dict[str, Any]]) -> Optional[Dict[str, Any]]:
    """The version an author edits next: the draft when one exists, else the published head."""
    for row in versions:
        if (row.get("status") or "").lower() == "draft":
            return row
    for row in versions:
        if (row.get("status") or "").lower() == "published":
            return row
    return None


# ---- commands -------------------------------------------------------------------------------------


def list_workflows(json_output: bool) -> None:
    try:
        workflows = _client().list_workflows()
    except GatewayError as ex:
        _fail(str(ex))
        return

    if json_output:
        print(json.dumps({"workflows": workflows}, indent=2))
        return

    table = Table(box=box.SIMPLE)
    table.add_column("Id")
    table.add_column("Name")
    table.add_column("Version", justify="right")
    table.add_column("Kind")
    table.add_column("Draft?")
    table.add_column("Summary", overflow="fold")
    for wf in workflows:
        table.add_row(
            wf.get("id", ""),
            wf.get("name", ""),
            str(wf.get("version", "")),
            "built-in" if wf.get("isBuiltIn") else "custom",
            "yes" if wf.get("hasDraft") else "",
            wf.get("summary", ""),
        )
    console.print(table)
    console.print(
        "Read one with: cc-devthrottle workflow instructions <id>   "
        "(the raw conduct an agent follows)"
    )


def show_workflow(workflow_id: str, version: Optional[int], json_output: bool) -> None:
    try:
        client = _client()
        if version is not None:
            data = client.get_version_detail(workflow_id, version)
        else:
            data = client.get_workflow(workflow_id)
    except GatewayError as ex:
        _fail(str(ex))
        return

    if json_output:
        print(json.dumps(data, indent=2))
        return

    console.print(f"[bold]{data.get('name', workflow_id)}[/bold]  ({workflow_id})")
    if data.get("status"):
        console.print(f"Status: {data['status']}  Version: {data.get('version')}")
    else:
        console.print(
            f"Version: {data.get('version')}  "
            f"Kind: {'built-in' if data.get('isBuiltIn') else 'custom'}  "
            f"Draft waiting: {'yes' if data.get('hasDraft') else 'no'}"
        )
    console.print(f"Summary: {data.get('summary', '')}")
    if data.get("whenToUse"):
        console.print(f"When to use: {data['whenToUse']}")
    if data.get("humanCheckpoint"):
        console.print(f"Human checkpoint: {data['humanCheckpoint']}")
    steps = data.get("steps") or []
    if steps:
        console.print("Steps:")
        for step in steps:
            reviewer = step.get("reviewer") or "no review"
            console.print(
                f"  - {step.get('name')}: doer {step.get('doer')}, {reviewer}; "
                f"done when {step.get('done')}"
            )
    criteria = data.get("outcomeCriteria") or []
    if criteria:
        console.print("Outcome criteria:")
        for criterion in criteria:
            console.print(f"  - {criterion.get('criterionId')}: {criterion.get('description')}")
    files = data.get("files") or []
    if files:
        console.print("Helper files: " + ", ".join(f.get("fileName", "") for f in files))
    console.print(
        f"Instructions: cc-devthrottle workflow instructions {workflow_id}"
        + (f" --version {version}" if version is not None else "")
    )


def print_instructions(workflow_id: str, version: Optional[int]) -> None:
    """Print the raw conduct markdown, unmangled - this output goes into an agent's context."""
    try:
        markdown = _client().get_instructions(workflow_id, version)
    except GatewayError as ex:
        _fail(str(ex))
        return
    sys.stdout.write(markdown)
    if markdown and not markdown.endswith("\n"):
        sys.stdout.write("\n")


def list_versions(workflow_id: str, json_output: bool) -> None:
    try:
        versions = _client().list_versions(workflow_id)
    except GatewayError as ex:
        _fail(str(ex))
        return

    if json_output:
        print(json.dumps({"versions": versions}, indent=2))
        return

    table = Table(box=box.SIMPLE)
    table.add_column("Version", justify="right")
    table.add_column("Status")
    table.add_column("Authored by")
    table.add_column("Created (UTC)")
    table.add_column("Note", overflow="fold")
    for row in versions:
        table.add_row(
            str(row.get("version", "")),
            row.get("status", ""),
            row.get("authoredBy", ""),
            (row.get("createdUtc") or "").replace("T", " ")[:19],
            row.get("changeNote") or "",
        )
    console.print(table)


def pull_workflow(workflow_id: str, directory: str, version: Optional[int]) -> None:
    try:
        client = _client()
        if version is None:
            versions = client.list_versions(workflow_id)
            picked = _pick_authoring_version(versions)
            if picked is None:
                _fail(f"Workflow '{workflow_id}' has no versions to pull.")
                return
            version = int(picked["version"])
        detail = client.get_version_detail(workflow_id, version)
    except GatewayError as ex:
        _fail(str(ex))
        return

    target = Path(directory)
    target.mkdir(parents=True, exist_ok=True)

    metadata = {
        "id": detail.get("workflowId", workflow_id),
        "name": detail.get("name", ""),
        "summary": detail.get("summary", ""),
        "whenToUse": detail.get("whenToUse", ""),
        "humanCheckpoint": detail.get("humanCheckpoint", ""),
        "steps": detail.get("steps") or [],
        "outcomeCriteria": detail.get("outcomeCriteria") or [],
    }
    (target / WORKFLOW_JSON).write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    (target / INSTRUCTIONS_MD).write_text(
        detail.get("instructionsMarkdown") or "", encoding="utf-8"
    )
    files = detail.get("files") or []
    if files:
        helpers = target / HELPERS_DIR
        helpers.mkdir(exist_ok=True)
        for f in files:
            (helpers / f["fileName"]).write_text(f.get("content") or "", encoding="utf-8")
    (target / HASH_SIDECAR).write_text(detail.get("contentHash", ""), encoding="utf-8")

    console.print(
        f"Pulled '{workflow_id}' v{version} ({detail.get('status')}) into {target.resolve()}"
    )
    console.print(
        "Edit the files, then push with: "
        f"cc-devthrottle workflow push {workflow_id} --dir \"{target}\""
    )


def _read_directory(workflow_id: str, directory: str, note: Optional[str]) -> Dict[str, Any]:
    source = Path(directory)
    if not source.is_dir():
        raise GatewayError(f"Directory not found: {source}")

    metadata: Dict[str, Any] = {}
    metadata_path = source / WORKFLOW_JSON
    if metadata_path.is_file():
        try:
            metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
        except ValueError as exc:
            raise GatewayError(f"{WORKFLOW_JSON} is not valid JSON: {exc}") from exc
    declared_id = (metadata.get("id") or "").strip()
    if declared_id and declared_id != workflow_id:
        raise GatewayError(
            f"{WORKFLOW_JSON} declares id '{declared_id}' but the push targets '{workflow_id}'. "
            "Make them agree before pushing."
        )

    instructions_path = source / INSTRUCTIONS_MD
    instructions = (
        instructions_path.read_text(encoding="utf-8") if instructions_path.is_file() else ""
    )

    files: List[Dict[str, str]] = []
    helpers = source / HELPERS_DIR
    if helpers.is_dir():
        for path in sorted(helpers.iterdir()):
            if path.is_file():
                files.append(
                    {"fileName": path.name, "content": path.read_text(encoding="utf-8")}
                )

    return {
        "id": workflow_id,
        "name": metadata.get("name") or workflow_id,
        "summary": metadata.get("summary") or "",
        "whenToUse": metadata.get("whenToUse") or "",
        "humanCheckpoint": metadata.get("humanCheckpoint") or "",
        "steps": metadata.get("steps") or [],
        "outcomeCriteria": metadata.get("outcomeCriteria") or [],
        "instructionsMarkdown": instructions,
        "files": files,
        "authoredBy": default_authored_by(),
        "changeNote": note,
    }


def push_workflow(workflow_id: str, directory: str, note: Optional[str]) -> None:
    try:
        client = _client()
        body = _read_directory(workflow_id, directory, note)

        if not client.workflow_exists(workflow_id):
            result = client.create(body)
            verb = "Created"
        else:
            sidecar = Path(directory) / HASH_SIDECAR
            if_match = (
                sidecar.read_text(encoding="utf-8").strip() if sidecar.is_file() else None
            )
            result = client.update_draft(workflow_id, body, if_match)
            verb = "Updated"
    except GatewayError as ex:
        _fail(str(ex))
        return

    new_hash = result.get("contentHash", "")
    if new_hash:
        (Path(directory) / HASH_SIDECAR).write_text(new_hash, encoding="utf-8")
    console.print(
        f"{verb} draft v{result.get('version')} of '{workflow_id}'. "
        "Nothing changes for the fleet until it publishes: "
        f"cc-devthrottle workflow publish {workflow_id}"
    )


def publish_workflow(workflow_id: str) -> None:
    try:
        result = _client().publish(workflow_id)
    except GatewayError as ex:
        _fail(str(ex))
        return
    console.print(
        f"Published '{workflow_id}' v{result.get('version')}. "
        "It is now the version every machine and agent reads."
    )


def reset_workflow(workflow_id: str) -> None:
    try:
        result = _client().reset(workflow_id)
    except GatewayError as ex:
        _fail(str(ex))
        return
    console.print(
        f"Reset '{workflow_id}' to the shipped content as v{result.get('version')}. "
        "Earlier versions remain as history."
    )


def delete_workflow(workflow_id: str, yes: bool) -> None:
    if not yes:
        confirmed = typer.confirm(
            f"Archive workflow '{workflow_id}'? It leaves the catalog; its versions remain "
            "as pinned history."
        )
        if not confirmed:
            raise typer.Exit(0)
    try:
        _client().delete(workflow_id)
    except GatewayError as ex:
        _fail(str(ex))
        return
    console.print(f"Archived '{workflow_id}'.")


def materialize_workflow(workflow_id: str, version: Optional[int]) -> None:
    """Write a version's bundle to the per-machine cache and print the absolute paths, so an
    agent can run helper files with its own shell. Version-stamped and hash-keyed: a bundle
    already on disk with the right hash is not rewritten."""
    try:
        client = _client()
        if version is None:
            head = client.get_workflow(workflow_id)
            version = int(head["version"])
        detail = client.get_version_detail(workflow_id, version)
    except GatewayError as ex:
        _fail(str(ex))
        return

    status = (detail.get("status") or "").lower()
    if status == "draft":
        _fail(
            f"Version {version} of '{workflow_id}' is a draft. Only published history can be "
            "materialized - publish it first."
        )
        return

    local_app_data = os.environ.get("LOCALAPPDATA") or str(Path.home() / ".local" / "share")
    root = Path(local_app_data) / "cc-director" / "workflows" / workflow_id / str(version)
    hash_file = root / HASH_SIDECAR
    expected = detail.get("contentHash", "")
    if hash_file.is_file() and hash_file.read_text(encoding="utf-8").strip() == expected:
        console.print(f"Already materialized: {root}")
    else:
        root.mkdir(parents=True, exist_ok=True)
        (root / INSTRUCTIONS_MD).write_text(
            detail.get("instructionsMarkdown") or "", encoding="utf-8"
        )
        files = detail.get("files") or []
        if files:
            helpers = root / HELPERS_DIR
            helpers.mkdir(exist_ok=True)
            for f in files:
                (helpers / f["fileName"]).write_text(f.get("content") or "", encoding="utf-8")
        hash_file.write_text(expected, encoding="utf-8")
        console.print(f"Materialized '{workflow_id}' v{version} into {root}")

    console.print(f"Instructions: {root / INSTRUCTIONS_MD}")
    helpers_dir = root / HELPERS_DIR
    if helpers_dir.is_dir():
        for path in sorted(helpers_dir.iterdir()):
            console.print(f"Helper: {path}")
