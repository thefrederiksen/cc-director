"""Gateway schedule operations for cc-devthrottle."""

from __future__ import annotations

import json
import os
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

from cc_shared import gateway  # noqa: E402
from cc_shared.config import get_config_path  # noqa: E402

TIMEOUT_SECONDS = 10
SCHEDULE_RECURRING = "recurring"
SCHEDULE_ONE_OFF = "oneOff"
NOTIFY_NONE = "none"
NOTIFY_ALWAYS = "always"
NOTIFY_FAILURE = "failure"
NOTIFY_CHOICES = (NOTIFY_NONE, NOTIFY_ALWAYS, NOTIFY_FAILURE)

console = Console()
err_console = Console(stderr=True)
gateway_override: Optional[str] = None


#: THE SAME CLASS THE SHARED TRANSPORT RAISES, not a look-alike beside it.
#:
#: This used to be its own `class GatewayError(Exception)`, while `gateway.gateway_base_url()` and
#: `gateway.session_key()` - called directly from this module - raise `cc_shared.gateway.GatewayError`.
#: Every `except GatewayError` here therefore missed the no-Gateway failure entirely, and the command
#: died with a Rich traceback. The owner accepted "no Gateway means no agent tooling" on the promise of
#: a CLEAR SENTENCE naming the remedy; a stack trace is not that sentence.
#:
#: Aliasing rather than catching both is deliberate: two names for one idea is what caused this, and a
#: second except clause on every handler would leave the trap in place for the next handler written.
GatewayError = gateway.GatewayError


def set_gateway_override(value: Optional[str]) -> None:
    global gateway_override
    gateway_override = value.rstrip("/") if value else None


def resolve_base_url() -> str:
    """The Gateway this SESSION was told to call. See mission_ops for the reasoning."""
    return gateway.gateway_base_url()


def _auth_token() -> str:
    """This session's own Gateway key, replacing the account-wide token this path used to present."""
    return gateway.session_key()


class ScheduleClient:
    """Talks to one Gateway's cron/schedule surface."""

    def __init__(self, base_url: Optional[str] = None) -> None:
        self.base_url = (base_url or resolve_base_url()).rstrip("/")
        # A session key is REQUIRED, and _auth_token raises with the remedy when there is none.
        # The old exemption - "a loopback Gateway on this machine needs no token" - is deliberately
        # gone: the credential identifies WHICH SESSION is calling, and that is as necessary on this
        # machine as on any other. It was the address, never the caller, that made loopback special.
        self._token = _auth_token()

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

    def _ok_or_raise(self, resp: requests.Response) -> Dict[str, Any]:
        if 200 <= resp.status_code < 300:
            if not resp.content:
                return {}
            return resp.json()
        raise GatewayError(self._gateway_message(resp))

    def list_jobs(self) -> List[Dict[str, Any]]:
        data = self._ok_or_raise(self._request("GET", "/cron/jobs"))
        return list(data.get("jobs", []))

    def get_job(self, job_id: str) -> Dict[str, Any]:
        return self._ok_or_raise(self._request("GET", f"/cron/jobs/{job_id}"))

    def create_job(self, job: Dict[str, Any]) -> Dict[str, Any]:
        return self._ok_or_raise(self._request("POST", "/cron/jobs", job))

    def update_job(self, job_id: str, job: Dict[str, Any]) -> Dict[str, Any]:
        return self._ok_or_raise(self._request("PUT", f"/cron/jobs/{job_id}", job))

    def delete_job(self, job_id: str) -> Dict[str, Any]:
        return self._ok_or_raise(self._request("DELETE", f"/cron/jobs/{job_id}"))

    def run_now(self, job_id: str) -> Dict[str, Any]:
        return self._ok_or_raise(self._request("POST", f"/cron/jobs/{job_id}/run"))

    def list_runs(self, job_id: str) -> List[Dict[str, Any]]:
        data = self._ok_or_raise(self._request("GET", f"/cron/jobs/{job_id}/runs"))
        return list(data.get("runs", []))

    def set_enabled(self, job_id: str, enabled: bool) -> Dict[str, Any]:
        job = self.get_job(job_id)
        job["enabled"] = enabled
        return self.update_job(job_id, job)


def _fail(message: str) -> None:
    err_console.print(f"[red]Error:[/red] {message}")
    raise typer.Exit(1)


def _director_ports_in_this_root() -> List[int]:
    """The Control API ports of the Directors registered in the root we are about to read.

    Each data root records one <director-id>.port file per Director it owns, so this is the
    cheapest local way to ask "does the Director the caller is pointing at actually belong
    to the configuration I am about to use?".
    """
    # config.json's own directory IS the root's config dir, so this follows
    # CC_DIRECTOR_ROOT exactly as the Gateway address and token do.
    ports_dir = get_config_path().parent / "director" / "ports"
    if not ports_dir.is_dir():
        return []
    found: List[int] = []
    for entry in ports_dir.glob("*.port"):
        try:
            found.append(int(entry.read_text(encoding="utf-8").strip()))
        except (OSError, ValueError):
            continue
    return found


def assert_scope_is_unambiguous() -> None:
    """Refuse when the caller is pointed at a Director this configuration does not own.

    Issue #2201. Two different environment variables steer two halves of cc-devthrottle,
    and neither implies the other:

      CC_DIRECTOR_API   the Director Control API - session, message and mission commands
      CC_DIRECTOR_ROOT  the data root, and so config.json - which is where the SCHEDULE
                        commands read the Gateway address and, critically, the Gateway
                        TOKEN that decides WHICH TENANT is written

    So `CC_DIRECTOR_API=<an isolated Director> cc-devthrottle schedule create ...` scopes
    the session half and silently leaves the schedule half reading the DEFAULT root - which
    on a normal machine is the owner's real fleet. It then SUCCEEDS, returns a real id, and
    prints a confirmation. That is how two jobs were written to the owner's live fleet on
    2026-07-26 while working against an isolated demo Director.

    THE TEST IS A MISMATCH, NOT MERE PRESENCE. Every agent running inside a DevThrottle
    session has CC_DIRECTOR_API set, pointing at the owner's own Director, and that is the
    normal case which must keep working. So this refuses only on POSITIVE EVIDENCE that the
    two disagree: this root records which Directors it owns, and the port the caller is
    aimed at is not one of them. When the root records no ports at all there is nothing to
    contradict, and we do not block on an absence of evidence.

    Failing here is deliberate. A scheduled job runs an agent unattended in a working
    directory, so landing one on the wrong tenant is not a display bug, and there is no safe
    default to guess once the caller has expressed two different intentions.

    Note that `--gateway` does NOT resolve the ambiguity: it overrides the ADDRESS while the
    token still comes from config, and on the shared hosted Gateway it is the token that
    selects the tenant. So the guard is checked regardless of any override.
    """
    api = os.environ.get("CC_DIRECTOR_API", "").strip()
    if not api:
        return
    port = urlparse(api).port
    if port is None:
        return
    known = _director_ports_in_this_root()
    if not known or port in known:
        return

    root = os.environ.get("CC_DIRECTOR_ROOT", "").strip() or "the default root"
    _fail(
        f"CC_DIRECTOR_API points at a Director on port {port}, but {root} owns "
        f"{', '.join(str(p) for p in sorted(known))}.\n\n"
        "  Schedule commands read the Gateway address and token from config.json, which "
        "lives under CC_DIRECTOR_ROOT - CC_DIRECTOR_API does not redirect them. Running "
        "anyway would write to whichever account THIS root is signed in as, which is not "
        "the Director you are pointing at.\n\n"
        "  Set CC_DIRECTOR_ROOT to that Director's own data root and run it again, for "
        "example:\n"
        "      CC_DIRECTOR_ROOT=D:\\demo\\_root cc-devthrottle schedule list\n\n"
        "  If you did mean this root's fleet, unset CC_DIRECTOR_API for this command."
    )


def _client() -> ScheduleClient:
    assert_scope_is_unambiguous()
    return ScheduleClient(base_url=gateway_override)


def _fmt(value: Optional[str]) -> str:
    return value if value else "-"


def _schedule_label(job: dict) -> str:
    kind = (job.get("scheduleKind") or "").lower()
    if kind == SCHEDULE_RECURRING.lower():
        return f"cron {_fmt(job.get('cronExpression'))}"
    return f"once @ {_fmt(job.get('runAt'))}"


def _runs_label(job: dict) -> str:
    action = job.get("action") or {}
    work_list = action.get("workListName")
    if work_list:
        return f"work list {work_list}"
    return f"skill {_fmt(action.get('seed'))}"


def _notify_label(job: dict) -> str:
    policy = (job.get("notifyOn") or NOTIFY_NONE).lower()
    if policy == NOTIFY_NONE:
        return "off"
    webhook = job.get("notifyWebhookUrl")
    base = "always (success + failure)" if policy == NOTIFY_ALWAYS else "on failure"
    return f"{base} + webhook {webhook}" if webhook else base


def list_jobs(json_output: bool) -> None:
    try:
        jobs = _client().list_jobs()
    except GatewayError as ex:
        _fail(str(ex))
        return

    if json_output:
        print(json.dumps(jobs, indent=2))
        return

    if not jobs:
        console.print(
            "No schedules on the Gateway yet. Create one with 'cc-devthrottle schedule create'."
        )
        return

    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    table.add_column("Id")
    table.add_column("Name")
    table.add_column("Machine")
    table.add_column("Runs")
    table.add_column("Schedule")
    table.add_column("Next run (UTC)")
    table.add_column("Enabled")

    for job in jobs:
        target = job.get("target") or {}
        table.add_row(
            _fmt(job.get("id")),
            _fmt(job.get("name")),
            _fmt(target.get("machine")),
            _runs_label(job),
            _schedule_label(job),
            _fmt(job.get("nextRunUtc")),
            "yes" if job.get("enabled") else "no",
        )
    console.print(table)


def get_job(job_id: str, json_output: bool) -> None:
    try:
        job = _client().get_job(job_id)
    except GatewayError as ex:
        _fail(str(ex))
        return

    if json_output:
        print(json.dumps(job, indent=2))
        return

    target = job.get("target") or {}
    console.print(f"[bold]{_fmt(job.get('name'))}[/bold]  ({_fmt(job.get('id'))})")
    console.print(f"  Enabled:    {'yes' if job.get('enabled') else 'no'}")
    console.print(f"  Machine:    {_fmt(target.get('machine'))}")
    console.print(f"  Runs:       {_runs_label(job)}")
    console.print(f"  Schedule:   {_schedule_label(job)}  ({_fmt(job.get('timeZoneId'))})")
    console.print(f"  Notify:     {_notify_label(job)}")
    console.print(f"  Next run:   {_fmt(job.get('nextRunUtc'))} UTC")
    console.print(f"  Last fired: {_fmt(job.get('lastFiredUtc'))}  ({_fmt(job.get('lastStatus'))})")
    console.print(f"  Created:    {_fmt(job.get('createdUtc'))} UTC")


def list_runs(job_id: str, json_output: bool) -> None:
    try:
        history = _client().list_runs(job_id)
    except GatewayError as ex:
        _fail(str(ex))
        return

    if json_output:
        print(json.dumps(history, indent=2))
        return

    if not history:
        console.print("No runs recorded yet for this schedule.")
        return

    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    table.add_column("Scheduled (UTC)")
    table.add_column("Fired (UTC)")
    table.add_column("Target")
    table.add_column("Session")
    table.add_column("Infra")
    table.add_column("Task")

    for run in history:
        table.add_row(
            _fmt(run.get("scheduledUtc")),
            _fmt(run.get("firedUtc")),
            _fmt(run.get("targetDirectorId")),
            _fmt(run.get("sessionId")),
            _fmt(run.get("infraStatus")),
            _fmt(run.get("taskStatus")),
        )
    console.print(table)


def create_job(
    name: str,
    machine: str,
    repo: str,
    at: Optional[str],
    cron: Optional[str],
    tz: str,
    seed: Optional[str],
    worklist: Optional[str],
    notify_on: str,
    notify_webhook: Optional[str],
    json_output: bool,
) -> None:
    if bool(at) == bool(cron):
        _fail("specify exactly one of --at (one-off) or --cron (recurring).")
        return
    if not seed and not worklist:
        _fail("specify what to run: either --seed <text> or --worklist <name>.")
        return
    if seed and worklist:
        _fail("specify only one of --seed or --worklist, not both.")
        return

    notify_value = (notify_on or NOTIFY_NONE).strip().lower()
    if notify_value not in NOTIFY_CHOICES:
        _fail(f"--notify-on must be one of {', '.join(NOTIFY_CHOICES)}.")
        return

    job = {
        "name": name,
        "enabled": True,
        "scheduleKind": SCHEDULE_ONE_OFF if at else SCHEDULE_RECURRING,
        "cronExpression": cron if cron else None,
        "runAt": at if at else None,
        "timeZoneId": tz,
        "target": {"machine": machine},
        "action": {
            "repoPath": repo,
            "seed": seed or "",
            "workListName": worklist if worklist else None,
        },
        "preventOverlap": True,
        "notifyOn": notify_value,
        "notifyWebhookUrl": notify_webhook if notify_webhook else None,
    }

    try:
        created = _client().create_job(job)
    except GatewayError as ex:
        _fail(str(ex))
        return

    if json_output:
        print(json.dumps(created, indent=2))
        return

    console.print("[green]Created schedule.[/green]")
    console.print(f"  Id:        {_fmt(created.get('id'))}")
    console.print(f"  Name:      {_fmt(created.get('name'))}")
    console.print(f"  Next run:  {_fmt(created.get('nextRunUtc'))} UTC")
    # Say WHERE it landed. A scheduled job runs an agent unattended, so "which fleet did
    # that just go to" must be answerable from this output rather than by cross-checking
    # `schedule list` afterwards and recognising somebody else's jobs (issue #2201).
    console.print(f"  Gateway:   {gateway_override or resolve_base_url()}")


def run_now(job_id: str, json_output: bool) -> None:
    try:
        record = _client().run_now(job_id)
    except GatewayError as ex:
        _fail(str(ex))
        return

    if json_output:
        print(json.dumps(record, indent=2))
        return

    console.print("[green]Fired the schedule.[/green]")
    console.print(f"  Fired:   {_fmt(record.get('firedUtc'))} UTC")
    console.print(f"  Target:  {_fmt(record.get('targetDirectorId'))}")
    console.print(f"  Session: {_fmt(record.get('sessionId'))}")
    console.print(f"  Infra:   {_fmt(record.get('infraStatus'))}")
    console.print(f"  Task:    {_fmt(record.get('taskStatus'))}")


def enable_job(job_id: str) -> None:
    try:
        job = _client().set_enabled(job_id, True)
    except GatewayError as ex:
        _fail(str(ex))
        return
    console.print(f"[green]Enabled[/green] {_fmt(job.get('name'))} ({_fmt(job.get('id'))}).")


def disable_job(job_id: str) -> None:
    try:
        job = _client().set_enabled(job_id, False)
    except GatewayError as ex:
        _fail(str(ex))
        return
    console.print(f"[yellow]Disabled[/yellow] {_fmt(job.get('name'))} ({_fmt(job.get('id'))}).")


def delete_job(job_id: str) -> None:
    try:
        _client().delete_job(job_id)
    except GatewayError as ex:
        _fail(str(ex))
        return
    console.print(f"[green]Deleted[/green] schedule {job_id}.")


def endpoint(json_output: bool) -> None:
    base = gateway_override or resolve_base_url()
    if json_output:
        print(json.dumps({"base_url": base}, indent=2))
    else:
        console.print(base)
