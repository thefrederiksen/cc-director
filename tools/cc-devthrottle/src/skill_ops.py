"""Gateway skill operations for cc-devthrottle - the central skill library (devthrottle_internal
issue 995).

A Skill is a capability an agent reaches for mid-task: a markdown body plus optional supporting
files, held and versioned on the Gateway rather than copied onto every machine by the installer.

THE ONE RULE THIS MODULE EXISTS TO KEEP: discovery is cheap, use is what costs. Every session's
launch briefing carries one line per skill; `skill get` is the only command that pulls a body, and
it is run once, by a session that is about to use that skill.

There is NO offline fallback, deliberately. `skill get` resolves the current published version from
the Gateway every time; if the Gateway cannot be reached the command FAILS and says so. A stale
skill that looks current is worse than a missing one that announces itself - an agent acting on
withdrawn instructions is exactly the failure the central library exists to make impossible.

The authoring loop round-trips a directory, like workflows:

    <dir>/skill.json       metadata: id, name, summary, triggers
    <dir>/SKILL.md         the body an agent reads
    <dir>/files/*          optional supporting files
    <dir>/.skill-hash      sidecar written by pull; sent as If-Match on push so a stale copy is
                           refused instead of clobbering a concurrent author
"""

from __future__ import annotations

import json
import os
import shutil
import socket
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional
from urllib.parse import urlencode

import requests
import typer
from rich import box
from rich.console import Console
from rich.table import Table
from urllib.parse import quote as urllib_quote, urlparse

_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared.config import CCDirectorConfig  # noqa: E402

LOOPBACK_DEFAULT = "http://127.0.0.1:7878"
TIMEOUT_SECONDS = 15
SKILL_JSON = "skill.json"
SKILL_MD = "SKILL.md"
FILES_DIR = "files"
HASH_SIDECAR = ".skill-hash"

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


class SkillClient:
    """Talks to one Gateway's skill surface."""

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
                f"the Gateway at {self.base_url} could not be reached. Skills are held centrally "
                "and are NOT cached for offline use, so there is nothing local to fall back on. "
                "Fix the connection and run this again - do NOT proceed from memory of what this "
                "skill used to say."
            ) from exc
        except requests.exceptions.Timeout as exc:
            raise GatewayError(
                f"the Gateway at {self.base_url} did not respond within {TIMEOUT_SECONDS}s. "
                "Run this again once it is reachable - do NOT proceed from memory of what this "
                "skill used to say."
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

    # A 2XX IS NOT PROOF THE GATEWAY UNDERSTOOD THE REQUEST. The Gateway serves the Cockpit
    # single-page app at "/" and falls UNKNOWN page paths back to index.html, so a Gateway that has
    # never heard of skills answers GET /gateway/skills with HTTP 200, Content-Type text/html, and
    # ~800 bytes of the app shell - not a 404. That is the live state of every machine whose Gateway
    # has not been upgraded yet, which is precisely the window this command has to survive.
    #
    # Believed at face value it is worse than an error: `skill get` would print the HTML shell into
    # an agent's context AS THE SKILL'S INSTRUCTIONS, and `skill list` would raise a raw ValueError
    # traceback. So both read paths assert the content type they were PROMISED, and an app-shell
    # answer is reported as "this Gateway does not serve the skill library yet" - which is the true
    # statement, and the one that tells the user what to do about it.
    def _not_the_skill_library(self, saw: str) -> GatewayError:
        return GatewayError(
            f"the Gateway at {self.base_url} answered with {saw} instead of skill data, which means "
            "it does not serve the skill library yet - it is running a build from before the library "
            "existed, and the request fell through to its web app. Upgrade or redeploy that Gateway. "
            "Do NOT treat what it returned as a skill."
        )

    @staticmethod
    def _content_type(resp: requests.Response) -> str:
        return (resp.headers.get("Content-Type") or "").split(";")[0].strip().lower()

    def _json_or_raise(self, resp: requests.Response) -> Dict[str, Any]:
        if 200 <= resp.status_code < 300:
            if not resp.content:
                return {}
            if self._content_type(resp) != "application/json":
                raise self._not_the_skill_library(self._content_type(resp) or "an unlabelled body")
            try:
                return resp.json()
            except ValueError as exc:
                # Labelled JSON that is not JSON: a proxy or error page, never something to act on.
                raise GatewayError(
                    f"the Gateway at {self.base_url} returned a body labelled JSON that could not be "
                    f"parsed: {exc}"
                ) from exc
        raise GatewayError(self._gateway_message(resp))

    def _text_or_raise(self, resp: requests.Response) -> str:
        if 200 <= resp.status_code < 300:
            # The body and file routes promise markdown and plain text. HTML here is the app shell,
            # and printing it would put a web page into an agent's context dressed as instructions.
            if self._content_type(resp) == "text/html":
                raise self._not_the_skill_library("its web app page")
            return resp.text
        raise GatewayError(self._gateway_message(resp))

    # ---- reads --------------------------------------------------------------------------------

    def list_skills(self) -> List[Dict[str, Any]]:
        data = self._json_or_raise(self._request("GET", "/gateway/skills"))
        return list(data.get("skills", []))

    def get_skill(self, skill_id: str) -> Dict[str, Any]:
        return self._json_or_raise(self._request("GET", f"/gateway/skills/{skill_id}"))

    def get_body(self, skill_id: str, version: Optional[int]) -> str:
        path = f"/gateway/skills/{skill_id}/body"
        if version is not None:
            path += f"?version={version}"
        return self._text_or_raise(self._request("GET", path))

    def list_versions(self, skill_id: str) -> List[Dict[str, Any]]:
        data = self._json_or_raise(self._request("GET", f"/gateway/skills/{skill_id}/versions"))
        return list(data.get("versions", []))

    def skill_exists(self, skill_id: str) -> bool:
        """True when the skill head exists at all - drafts included. GET /{id} cannot answer this
        (it 404s for a draft-only skill), so existence goes through the versions route."""
        resp = self._request("GET", f"/gateway/skills/{skill_id}/versions")
        if resp.status_code == 404:
            return False
        self._json_or_raise(resp)
        return True

    def get_version_detail(self, skill_id: str, version: int) -> Dict[str, Any]:
        return self._json_or_raise(
            self._request("GET", f"/gateway/skills/{skill_id}/versions/{version}")
        )

    # ---- writes -------------------------------------------------------------------------------

    def create(self, body: Dict[str, Any]) -> Dict[str, Any]:
        return self._json_or_raise(self._request("POST", "/gateway/skills", body))

    def update_draft(
        self, skill_id: str, body: Dict[str, Any], if_match: Optional[str]
    ) -> Dict[str, Any]:
        extra = {"If-Match": if_match} if if_match else None
        return self._json_or_raise(
            self._request("PUT", f"/gateway/skills/{skill_id}/draft", body, extra)
        )

    def publish(self, skill_id: str) -> Dict[str, Any]:
        return self._json_or_raise(self._request("POST", f"/gateway/skills/{skill_id}/publish"))

    def clone(self, skill_id: str, new_id: str) -> Dict[str, Any]:
        query = urlencode({"newId": new_id, "by": default_authored_by()})
        return self._json_or_raise(
            self._request("POST", f"/gateway/skills/{skill_id}/clone?{query}")
        )

    def delete(self, skill_id: str) -> Dict[str, Any]:
        return self._json_or_raise(self._request("DELETE", f"/gateway/skills/{skill_id}"))

    def set_enabled(self, skill_id: str, enabled: bool) -> Dict[str, Any]:
        verb = "enable" if enabled else "disable"
        actor = urllib_quote(default_authored_by())
        return self._json_or_raise(
            self._request("POST", f"/gateway/skills/{skill_id}/{verb}?by={actor}")
        )


def _fail(message: str) -> None:
    err_console.print(f"[red]FAILED:[/red] {message}")
    raise typer.Exit(1)


def _client() -> SkillClient:
    return SkillClient(base_url=gateway_override)


def _safe_file_name(name: str) -> str:
    """Refuse any server-supplied file name that is not a bare name. The Gateway validates names on
    write, but this command must not trust that: a misconfigured, older, or hostile server must not
    be able to steer a write outside the pull or cache directory."""
    if (
        not name
        or name in (".", "..")
        or "/" in name
        or "\\" in name
        or ":" in name
        or name != name.strip()
    ):
        raise GatewayError(f"the Gateway returned an unsafe file name: '{name}'.")
    return name


def _write_exact(path: Path, text: str) -> None:
    """Write text with NO newline translation, so pull/push round-trips are value-faithful."""
    with path.open("w", encoding="utf-8", newline="") as handle:
        handle.write(text)


def _read_exact(path: Path) -> str:
    with path.open("r", encoding="utf-8", newline="") as handle:
        return handle.read()


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


def list_skills(json_output: bool) -> None:
    try:
        skills = _client().list_skills()
    except GatewayError as ex:
        _fail(str(ex))
        return

    if json_output:
        print(json.dumps({"skills": skills}, indent=2))
        return

    table = Table(box=box.SIMPLE)
    table.add_column("Id")
    table.add_column("Name")
    table.add_column("V", justify="right")
    table.add_column("Kind")
    table.add_column("State")
    table.add_column("Files", justify="right")
    table.add_column("What it does", overflow="fold")
    for skill in skills:
        # An older Gateway omits the enabled field; absent means available.
        state = "OFF" if skill.get("enabled") is False else "available"
        table.add_row(
            skill.get("id", ""),
            skill.get("name", ""),
            str(skill.get("version", "")),
            "built-in" if skill.get("isBuiltIn") else "yours",
            state,
            str(skill.get("fileCount") or ""),
            skill.get("summary", ""),
        )
    console.print(table)
    console.print(
        "Read one IN FULL only when you are about to use it: cc-devthrottle skill get <id>"
    )


def get_skill(skill_id: str, version: Optional[int]) -> None:
    """Fetch one skill and print its body verbatim - the command an agent runs at the moment it
    reaches for a skill. The body goes straight into the agent's context, so nothing is rendered or
    wrapped; supporting files are written to the machine's version-keyed cache and their absolute
    paths are printed after the body, because a script has to exist on disk to be run.

    The version is resolved from the Gateway on EVERY call, so what is printed is always the
    currently published content. There is no offline path: an unreachable Gateway fails the command.
    """
    try:
        client = _client()
        detail: Optional[Dict[str, Any]] = None
        if version is None:
            head = client.get_skill(skill_id)
            version = int(head["version"])
            file_count = int(head.get("fileCount") or 0)
        else:
            detail = client.get_version_detail(skill_id, version)
            file_count = len(detail.get("files") or [])
        body = client.get_body(skill_id, version)
    except GatewayError as ex:
        _fail(str(ex))
        return

    sys.stdout.write(body)
    if not body.endswith("\n"):
        sys.stdout.write("\n")

    if file_count == 0:
        return

    try:
        if detail is None:
            detail = _client().get_version_detail(skill_id, version)
        paths = _materialize(skill_id, int(version), detail)
    except GatewayError as ex:
        # The body already printed, so the agent has the instructions but not the files it was told
        # to run. Say exactly that rather than letting it discover a missing path itself.
        _fail(
            f"the body of '{skill_id}' printed above, but its supporting files could not be "
            f"fetched: {ex}"
        )
        return

    sys.stdout.write("\nFiles for this skill:\n")
    for path in paths:
        sys.stdout.write(f"  {path}\n")


def _materialize(skill_id: str, version: int, detail: Dict[str, Any]) -> List[Path]:
    """Write a version's supporting files to the per-machine cache and return their absolute paths.

    The cache is keyed by (skill, VERSION) and verified by content hash, so it can never serve one
    version's files as another's: a newly published version is a new directory, and a half-written
    or partly-deleted bundle is rewritten rather than reported as intact. It is a cache of what was
    fetched, never a substitute for fetching - `get_skill` always resolves the version from the
    Gateway first.
    """
    files = detail.get("files") or []
    for f in files:
        _safe_file_name(f["fileName"])

    local_app_data = os.environ.get("LOCALAPPDATA") or str(Path.home() / ".local" / "share")
    root = Path(local_app_data) / "cc-director" / "skills" / skill_id / str(version)
    hash_file = root / HASH_SIDECAR
    expected = detail.get("contentHash", "")

    intact = (
        hash_file.is_file()
        and hash_file.read_text(encoding="utf-8").strip() == expected
        and all((root / FILES_DIR / f["fileName"]).is_file() for f in files)
    )
    if not intact:
        root.mkdir(parents=True, exist_ok=True)
        files_dir = root / FILES_DIR
        if files_dir.is_dir():
            shutil.rmtree(files_dir)
        files_dir.mkdir()
        for f in files:
            _write_exact(files_dir / _safe_file_name(f["fileName"]), f.get("content") or "")
        hash_file.write_text(expected, encoding="utf-8")

    return [root / FILES_DIR / f["fileName"] for f in files]


def show_skill(skill_id: str, version: Optional[int], json_output: bool) -> None:
    try:
        client = _client()
        if version is not None:
            data = client.get_version_detail(skill_id, version)
        else:
            data = client.get_skill(skill_id)
    except GatewayError as ex:
        _fail(str(ex))
        return

    if json_output:
        print(json.dumps(data, indent=2))
        return

    console.print(f"[bold]{data.get('name', skill_id)}[/bold]  ({skill_id})")
    if data.get("status"):
        console.print(f"Status: {data['status']}  Version: {data.get('version')}")
    else:
        console.print(
            f"Version: {data.get('version')}  "
            f"Kind: {'built-in' if data.get('isBuiltIn') else 'yours'}  "
            f"Draft waiting: {'yes' if data.get('hasDraft') else 'no'}"
        )
    console.print(f"What it does: {data.get('summary', '')}")
    triggers = data.get("triggers") or []
    if triggers:
        console.print("Triggers: " + ", ".join(triggers))
    files = data.get("files")
    if files:
        console.print("Files: " + ", ".join(f.get("fileName", "") for f in files))
    elif data.get("fileCount"):
        console.print(f"Files: {data['fileCount']}")
    if data.get("isBuiltIn"):
        console.print(
            "Built in and read-only. To customize it: "
            f"cc-devthrottle skill clone {skill_id} <new-id>"
        )
    console.print(
        f"Read it in full: cc-devthrottle skill get {skill_id}"
        + (f" --version {version}" if version is not None else "")
    )


def list_versions(skill_id: str, json_output: bool) -> None:
    try:
        versions = _client().list_versions(skill_id)
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


def pull_skill(skill_id: str, directory: str, version: Optional[int]) -> None:
    try:
        client = _client()
        if version is None:
            versions = client.list_versions(skill_id)
            picked = _pick_authoring_version(versions)
            if picked is None:
                _fail(f"skill '{skill_id}' has no versions to pull.")
                return
            version = int(picked["version"])
        detail = client.get_version_detail(skill_id, version)
    except GatewayError as ex:
        _fail(str(ex))
        return

    target = Path(directory)
    target.mkdir(parents=True, exist_ok=True)

    metadata = {
        "id": detail.get("skillId", skill_id),
        "name": detail.get("name", ""),
        "summary": detail.get("summary", ""),
        "triggers": detail.get("triggers") or [],
    }
    (target / SKILL_JSON).write_text(json.dumps(metadata, indent=2) + "\n", encoding="utf-8")
    _write_exact(target / SKILL_MD, detail.get("bodyMarkdown") or "")
    # The files directory mirrors the SERVER exactly: clear it first, so a file another author
    # deleted on the Gateway does not survive locally and get resurrected by the next push.
    files_dir = target / FILES_DIR
    if files_dir.is_dir():
        shutil.rmtree(files_dir)
    files = detail.get("files") or []
    if files:
        files_dir.mkdir()
        for f in files:
            _write_exact(files_dir / _safe_file_name(f["fileName"]), f.get("content") or "")
    (target / HASH_SIDECAR).write_text(detail.get("contentHash", ""), encoding="utf-8")

    console.print(f"Pulled '{skill_id}' v{version} ({detail.get('status')}) into {target.resolve()}")
    console.print(
        f'Edit the files, then push with: cc-devthrottle skill push {skill_id} --dir "{target}"'
    )


def _read_directory(skill_id: str, directory: str, note: Optional[str]) -> Dict[str, Any]:
    source = Path(directory)
    if not source.is_dir():
        raise GatewayError(f"directory not found: {source}")

    metadata: Dict[str, Any] = {}
    metadata_path = source / SKILL_JSON
    if metadata_path.is_file():
        try:
            metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
        except ValueError as exc:
            raise GatewayError(f"{SKILL_JSON} is not valid JSON: {exc}") from exc
        if not isinstance(metadata, dict):
            raise GatewayError(
                f"{SKILL_JSON} must be a JSON object with the skill's metadata, "
                f"not {type(metadata).__name__}."
            )
    declared_id = (metadata.get("id") or "").strip()
    if declared_id and declared_id != skill_id:
        raise GatewayError(
            f"{SKILL_JSON} declares id '{declared_id}' but the push targets '{skill_id}'. "
            "Make them agree before pushing."
        )

    body_path = source / SKILL_MD
    body = _read_exact(body_path) if body_path.is_file() else ""

    files: List[Dict[str, str]] = []
    files_dir = source / FILES_DIR
    if files_dir.is_dir():
        for path in sorted(files_dir.iterdir()):
            if path.is_file():
                files.append({"fileName": path.name, "content": _read_exact(path)})

    return {
        "id": skill_id,
        "name": metadata.get("name") or skill_id,
        "summary": metadata.get("summary") or "",
        "triggers": metadata.get("triggers") or [],
        "bodyMarkdown": body,
        "files": files,
        "authoredBy": default_authored_by(),
        "changeNote": note,
    }


def push_skill(skill_id: str, directory: str, note: Optional[str], force: bool = False) -> None:
    try:
        client = _client()
        body = _read_directory(skill_id, directory, note)

        if not client.skill_exists(skill_id):
            result = client.create(body)
            verb = "Created"
        else:
            sidecar = Path(directory) / HASH_SIDECAR
            if_match = sidecar.read_text(encoding="utf-8").strip() if sidecar.is_file() else None
            if not if_match and not force:
                raise GatewayError(
                    f"no {HASH_SIDECAR} sidecar in {directory}, so this push cannot prove it builds "
                    "on the current content and could silently overwrite another author's edit. "
                    "Pull first (which writes the sidecar), or pass --force to overwrite "
                    "deliberately."
                )
            result = client.update_draft(skill_id, body, if_match)
            verb = "Updated"
    except GatewayError as ex:
        _fail(str(ex))
        return

    new_hash = result.get("contentHash", "")
    if new_hash:
        try:
            (Path(directory) / HASH_SIDECAR).write_text(new_hash, encoding="utf-8")
        except OSError as exc:
            _fail(
                f"the draft WAS updated on the Gateway (v{result.get('version')}), but the local "
                f"hash sidecar could not be written: {exc}. Run 'cc-devthrottle skill pull "
                f'{skill_id} --dir "{directory}"\' to resynchronize before the next push.'
            )
            return
    console.print(
        f"{verb} draft v{result.get('version')} of '{skill_id}'. "
        "No agent sees it until it publishes: "
        f"cc-devthrottle skill publish {skill_id}"
    )


def publish_skill(skill_id: str) -> None:
    try:
        result = _client().publish(skill_id)
    except GatewayError as ex:
        _fail(str(ex))
        return
    console.print(
        f"Published '{skill_id}' v{result.get('version')}. Every agent on every machine gets this "
        "version on its next fetch - nothing to deploy, nothing to update."
    )


def clone_skill(skill_id: str, new_id: str) -> None:
    try:
        result = _client().clone(skill_id, new_id)
    except GatewayError as ex:
        _fail(str(ex))
        return
    console.print(
        f"Cloned '{skill_id}' into '{result.get('id')}' v{result.get('version')}. "
        "The clone is yours: published, editable, and independent of the original."
    )


def set_skill_enabled(skill_id: str, enabled: bool) -> None:
    try:
        _client().set_enabled(skill_id, enabled)
    except GatewayError as ex:
        _fail(str(ex))
        return
    if enabled:
        console.print(
            f"'{skill_id}' is AVAILABLE again - back in every agent's briefing and fetchable."
        )
    else:
        console.print(
            f"'{skill_id}' is OFF - left out of every agent's briefing and its fetch refused. "
            "Nothing was deleted; switch it back on anytime with: "
            f"cc-devthrottle skill enable {skill_id}"
        )


def delete_skill(skill_id: str, yes: bool) -> None:
    if not yes:
        confirmed = typer.confirm(
            f"Archive skill '{skill_id}'? It leaves the register; its versions remain readable "
            "by explicit version."
        )
        if not confirmed:
            raise typer.Exit(0)
    try:
        _client().delete(skill_id)
    except GatewayError as ex:
        _fail(str(ex))
        return
    console.print(f"Archived '{skill_id}'.")
