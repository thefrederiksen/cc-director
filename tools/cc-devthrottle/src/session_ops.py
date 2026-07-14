"""Fleet/session operations for cc-devthrottle."""

from __future__ import annotations

import json
import os
import sys
import tempfile
import time
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import typer
from rich import box
from rich.console import Console
from rich.table import Table


# --- ASCII-only output (project house rule): Rich truncates an overflowing table cell with the
# Unicode ellipsis U+2026; emit ASCII "..." instead. Patched once at import. cc-devthrottle's cli
# imports this module eagerly, so the global patch is in place before any table renders. ---
def _install_ascii_truncation():
    import rich.text
    from rich.cells import set_cell_size
    _orig = rich.text.Text.truncate
    if getattr(_orig, "_ascii_ellipsis", False):
        return
    def _truncate(self, max_width, *, overflow=None, pad=False):
        _orig(self, max_width, overflow=overflow, pad=pad)
        if "\u2026" in self.plain:
            self.plain = set_cell_size(self.plain.replace("\u2026", ""), max(0, max_width - 3)) + "..."
            if pad and len(self.plain) < max_width:
                self.plain += " " * (max_width - len(self.plain))
    _truncate._ascii_ellipsis = True
    rich.text.Text.truncate = _truncate


_install_ascii_truncation()


# --- ASCII-only output: Typer renders its --help and error panels with Rich's default ROUNDED
# (Unicode) box; force them to the ASCII box so panels stay ASCII even when the console is UTF-8.
# Guarded so a non-Typer environment is a harmless no-op. ---
def _install_ascii_typer_panels():
    try:
        import typer.rich_utils as _tru
        from rich import box as _rbox
    except Exception:
        return
    if getattr(_tru.Panel, "_ascii_box", False):
        return
    _OrigPanel = _tru.Panel

    class _AsciiPanel(_OrigPanel):
        _ascii_box = True

        def __init__(self, *args, **kwargs):
            kwargs.setdefault("box", _rbox.ASCII)
            super().__init__(*args, **kwargs)

    _tru.Panel = _AsciiPanel


_install_ascii_typer_panels()

# Make cc_shared importable when running from source, matching the existing cc-* tools.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import director  # noqa: E402

for _stream in (sys.stdout, sys.stderr):
    try:
        _stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
    except (AttributeError, ValueError):
        pass

console = Console()
SELFTEST_MARKER = "FLEETPONG"


def _repo_name(repo: str) -> str:
    return repo.replace("\\", "/").rstrip("/").split("/")[-1] if repo else "-"


def _get_sessions() -> List[Dict[str, Any]]:
    try:
        sessions = director.get_json("fleet/sessions") or []
    except director.DirectorError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)
    return sessions


def _resolve_target(target: str, *, command_name: str) -> Dict[str, Any]:
    sessions = _get_sessions()
    # Issue #821: the shared resolver now understands the three-digit session number (#820) as a
    # first-class target, preferring it over id-prefix / name matching, so message send / ask and
    # session rename all address a session by its number through this one call.
    matches = director.resolve_target(sessions, target)
    if not matches:
        console.print(
            f"[red]No session matches '{target}'.[/red] "
            "Run cc-devthrottle session list to see the fleet."
        )
        raise typer.Exit(1)
    if len(matches) > 1:
        console.print(f"[yellow]'{target}' is ambiguous - {len(matches)} matches:[/yellow]")
        for s in matches:
            sid = director.field(s, "sessionId", "SessionId")
            name = director.field(s, "name", "Name") or "(unnamed)"
            machine = director.field(s, "machineName", "MachineName") or "-"
            console.print(f"  {director.short_id(sid)}  {name}  ({machine})")
        console.print(f"Re-run {command_name} with a longer id prefix.")
        raise typer.Exit(1)
    return matches[0]


def resolve_target_or_current(target: Optional[str]) -> str:
    """Return the requested session id, defaulting to this session."""
    if target is None or not target.strip():
        sid = director.session_id()
        if not sid:
            console.print(
                "[red]Error:[/red] no target was provided and CC_SESSION_ID is not set."
            )
            raise typer.Exit(1)
        return sid

    chosen = _resolve_target(target, command_name="cc-devthrottle session rename")
    return director.field(chosen, "sessionId", "SessionId")


def list_sessions(json_output: bool) -> None:
    """List every session running across the fleet."""
    sessions = _get_sessions()

    if json_output:
        # Plain print, not console.print: Rich wraps to 80 columns when stdout is not a TTY and
        # injects newlines into long values, producing invalid JSON for agents/pipes.
        print(json.dumps(sessions, indent=2))
        return

    if not sessions:
        console.print("No sessions are running in the fleet.")
        return

    table = Table(show_header=True, header_style="bold", box=box.ASCII)
    table.add_column("NO.")
    table.add_column("ID")
    table.add_column("NAME")
    table.add_column("MACHINE")
    table.add_column("REPOSITORY")
    table.add_column("STATUS")

    me = director.session_id()
    for s in sessions:
        sid = director.field(s, "sessionId", "SessionId")
        number = director.field(s, "number", "Number")
        number_text = str(number) if number is not None else "-"
        name = director.field(s, "name", "Name") or "(unnamed)"
        machine = director.field(s, "machineName", "MachineName") or "-"
        repo = director.field(s, "repoPath", "RepoPath")
        status = director.field(s, "activityState", "ActivityState") or "-"
        marker = " (you)" if me and sid.lower() == me.lower() else ""
        table.add_row(number_text, director.short_id(sid) + marker, name, machine, _repo_name(repo), status)

    console.print(table)


def whoami() -> None:
    """Show this session's own fleet identity."""
    sid = director.session_id()
    if not sid:
        console.print(
            "[red]Error:[/red] CC_SESSION_ID is not set. "
            "cc-devthrottle session whoami only works inside a DevThrottle session."
        )
        raise typer.Exit(1)

    short = director.short_id(sid)
    sessions = _get_sessions()
    me = next(
        (s for s in sessions if director.field(s, "sessionId", "SessionId").lower() == sid.lower()),
        None,
    )
    if me is None:
        console.print(f"You are session {short} (id {sid}).")
    else:
        name = director.field(me, "name", "Name") or "(unnamed)"
        machine = director.field(me, "machineName", "MachineName") or "this machine"
        repo = director.field(me, "repoPath", "RepoPath")
        number = director.field(me, "number", "Number")
        number_text = f"number {number}, " if number is not None else ""
        console.print(f'You are session {number_text}{short} ("{name}") on {machine}, repo {_repo_name(repo)}.')

    console.print('To message another session:  cc-devthrottle message send <id> "<message>"')
    console.print('To message everyone:         cc-devthrottle message send all "<message>"')
    console.print("To see all sessions:         cc-devthrottle session list")


def rename_session(target: Optional[str], new_name: str) -> Dict[str, Any]:
    """Rename a target session, defaulting to the current session."""
    name = new_name.strip()
    if not name:
        console.print("[red]Error:[/red] the new session name cannot be blank.")
        raise typer.Exit(1)

    sid = resolve_target_or_current(target)
    try:
        # Tunnel-only floor (#1490): rename goes through the Director's loopback POST /fleet/rename, which
        # renames a local session directly or relays a remote one via the Gateway over the tunnel. The old
        # PATCH /sessions/{sid} route was removed from the Director in the tunnel-only cut.
        resp = director.post_json("fleet/rename", {"toSessionId": sid, "name": name})
    except director.DirectorError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    if not isinstance(resp, dict):
        console.print("[red]Error:[/red] the Director did not return the renamed session.")
        raise typer.Exit(1)

    actual = director.field(resp, "name", "Name") or name
    actual_sid = director.field(resp, "sessionId", "SessionId") or sid
    console.print(f'[green]Renamed[/green] {director.short_id(actual_sid)} to "{actual}".')
    return resp


def prompt_session(target: str, text: str, no_submit: bool = False) -> Dict[str, Any]:
    """Send raw text into a session - what a human typing into it would produce.

    Unlike `message send`, this does NOT frame the text with a sender. Restores the old
    POST /sessions/{sid}/prompt.
    """
    if not text.strip():
        console.print("[red]Error:[/red] the prompt text cannot be blank.")
        raise typer.Exit(1)
    sid = resolve_target_or_current(target)
    try:
        resp = director.post_json(
            "fleet/prompt", {"toSessionId": sid, "text": text, "appendEnter": not no_submit}
        )
    except director.DirectorError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)
    console.print(f"[green]Sent[/green] prompt to {director.short_id(sid)}.")
    return resp if isinstance(resp, dict) else {}


def interrupt_session(target: Optional[str]) -> Dict[str, Any]:
    """Stop what a session is currently doing. Restores the old POST /sessions/{sid}/interrupt."""
    sid = resolve_target_or_current(target)
    try:
        resp = director.post_json("fleet/interrupt", {"toSessionId": sid})
    except director.DirectorError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)
    console.print(f"[green]Interrupted[/green] {director.short_id(sid)}.")
    return resp if isinstance(resp, dict) else {}


def hold_session(target: Optional[str], release: bool = False, minutes: Optional[int] = None) -> Dict[str, Any]:
    """Park a session, or release it. Restores the old POST /sessions/{sid}/hold.

    A hold asked for while the session is still working is DEFERRED: it applies when the turn
    settles, and the response's pending flag says so. A held session that starts working again
    always takes itself off hold.
    """
    sid = resolve_target_or_current(target)
    body: Dict[str, Any] = {"toSessionId": sid, "onHold": not release}
    if minutes is not None:
        body["snoozeMinutes"] = minutes
    try:
        resp = director.post_json("fleet/hold", body)
    except director.DirectorError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    short = director.short_id(sid)
    if release:
        console.print(f"[green]Released[/green] {short} - no longer held.")
    elif isinstance(resp, dict) and director.field(resp, "pending", "Pending"):
        console.print(f"[green]Hold queued[/green] {short} is still working; it parks when it finishes.")
    else:
        for_text = f" for {minutes} minutes" if minutes else ""
        console.print(f"[green]Held[/green] {short}{for_text}.")
    return resp if isinstance(resp, dict) else {}


def read_session_buffer(target: Optional[str]) -> None:
    """Print what a session's terminal is showing. Restores the old GET /sessions/{sid}/buffer."""
    sid = resolve_target_or_current(target)
    try:
        resp = director.get_json(f"fleet/buffer?sessionId={sid}")
    except director.DirectorError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    # The buffer verb returns the terminal text under one of a couple of shapes depending on the
    # path it came back through; print whichever carries the text rather than guessing one.
    text = None
    if isinstance(resp, dict):
        text = director.field(resp, "text", "Text") or director.field(resp, "buffer", "Buffer")
    elif isinstance(resp, str):
        text = resp
    if text is None:
        console.print("[red]Error:[/red] the Director did not return the session's buffer.")
        raise typer.Exit(1)
    console.print(text)


def set_session_role(target: Optional[str], role: Optional[str]) -> Dict[str, Any]:
    """Declare a session's explicit role, defaulting to the current session.

    Restores the set-role verb the tunnel-only cut removed with POST /sessions/{sid}/role, which
    left a running session stuck with the role it was born with. Architect cannot be derived from
    the spawn graph, so this is the only way to make one after birth. An empty role clears the
    explicit role and reverts the session to auto-derivation.
    """
    sid = resolve_target_or_current(target)
    wanted = (role or "").strip()
    try:
        resp = director.post_json("fleet/role", {"toSessionId": sid, "role": wanted})
    except director.DirectorError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    if not isinstance(resp, dict):
        console.print("[red]Error:[/red] the Director did not return the session's role.")
        raise typer.Exit(1)

    actual_sid = director.field(resp, "sessionId", "SessionId") or sid
    explicit = director.field(resp, "explicitRole", "ExplicitRole")
    short = director.short_id(actual_sid)
    # Only the explicit role is reported: Worker/Manager derivation needs the fleet-wide spawn graph, which
    # lives in the Gateway, so the effective role is read from `session list`, not returned here.
    if explicit:
        console.print(f"[green]Role set[/green] {short} is now explicitly {explicit}.")
    else:
        console.print(f"[green]Role cleared[/green] {short} reverts to automatic role derivation.")
    return resp


def mark_done(target: Optional[str], reason: Optional[str]) -> Dict[str, Any]:
    """Flag a session for deletion, defaulting to the current session.

    The session is not killed synchronously - it is flagged, and the owning Director's
    deletion reaper removes it within about a minute, once a short grace has elapsed and the
    session is no longer working. This is how an unattended run tears ITSELF down when it has
    nothing left for the user, instead of lingering as a dead session in the fleet.
    """
    sid = resolve_target_or_current(target)
    body: Dict[str, Any] = {"toSessionId": sid}
    if reason and reason.strip():
        body["reason"] = reason.strip()
    try:
        # Tunnel-only floor (#1490): self-reap goes through the Director's loopback POST /fleet/done, which
        # flags a local session directly or relays a remote one via the Gateway. The old
        # POST /sessions/{sid}/request-deletion route was removed from the Director in the tunnel-only cut.
        resp = director.post_json("fleet/done", body)
    except director.DirectorError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    console.print(
        f"[green]Marked[/green] {director.short_id(sid)} for deletion; "
        "the Director will reap it shortly."
    )
    return resp if isinstance(resp, dict) else {}


def _report_delivery(resp: Any, who: str) -> None:
    accepted = False
    count = 0
    err: Optional[str] = None
    warning: Optional[str] = None
    if isinstance(resp, dict):
        accepted = bool(resp.get("accepted", resp.get("Accepted", False)))
        count = int(resp.get("deliveredCount", resp.get("DeliveredCount", 0)) or 0)
        err = resp.get("error") or resp.get("Error")
        warning = resp.get("warning") or resp.get("Warning")
    if accepted:
        console.print(f"[green]Delivered[/green] to {who} ({count} session(s)).")
        if warning:
            console.print(f"[yellow]Note:[/yellow] {warning}")
    else:
        console.print(f"[red]Not delivered:[/red] {err or 'unknown error'}")
        raise typer.Exit(1)


def send_message(
    target: str,
    message: str,
    everyone: bool = False,
    reason: str | None = None,
    grant: str | None = None,
) -> None:
    """Send a message to one session, or broadcast with target 'all'.

    A plain 'all' reaches only the sender's team (its Mission, or - solo - the same repository on the
    same machine). --everyone asks to reach the whole fleet, which the Gateway Hub gates on a human
    grant plus a reason (issue #1229)."""
    me = director.session_id()

    if target.strip().lower() == "all":
        body = {"text": message, "fromSessionId": me}
        if everyone:
            body["everyone"] = True
            if reason:
                body["reason"] = reason
            if grant:
                body["grantId"] = grant
        try:
            resp = director.post_json("fleet/broadcast", body)
        except director.DirectorError as err:
            console.print(f"[red]Error:[/red] {err}")
            raise typer.Exit(1)
        _report_delivery(resp, "the whole fleet" if everyone else "your team")
        return

    chosen = _resolve_target(target, command_name="cc-devthrottle message send")
    target_sid = director.field(chosen, "sessionId", "SessionId")
    try:
        resp = director.post_json(
            "fleet/send", {"toSessionId": target_sid, "text": message, "fromSessionId": me}
        )
    except director.DirectorError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    name = director.field(chosen, "name", "Name") or director.short_id(target_sid)
    _report_delivery(resp, f'{name} ({director.short_id(target_sid)})')


def ask_session(target: str, question: str, timeout_ms: int) -> None:
    """Ask one session a question and print its answer."""
    if target.strip().lower() == "all":
        console.print(
            "[red]message ask targets a single session.[/red] "
            "Use cc-devthrottle message send all for a broadcast."
        )
        raise typer.Exit(1)

    me = director.session_id()
    chosen = _resolve_target(target, command_name="cc-devthrottle message ask")
    target_sid = director.field(chosen, "sessionId", "SessionId")

    http_timeout = max(30.0, timeout_ms / 1000.0 + 15.0)
    try:
        resp = director.post_json(
            "fleet/ask",
            {
                "toSessionId": target_sid,
                "question": question,
                "fromSessionId": me,
                "timeoutMs": timeout_ms,
            },
            timeout=http_timeout,
        )
    except director.DirectorError as err:
        console.print(f"[red]{err}[/red]")
        raise typer.Exit(1)

    answer = (director.field(resp, "answer", "Answer") if isinstance(resp, dict) else "").strip()
    name = director.field(chosen, "name", "Name") or director.short_id(target_sid)
    console.print(f"[dim]-- answer from {name} ({director.short_id(target_sid)}) --[/dim]")
    console.print(answer if answer else "(the target produced no output)")


def spawn_session(
    repo: str,
    agent: str,
    prompt: Optional[str],
    name: Optional[str],
    purpose: Optional[str],
    command: Optional[str],
    command_args: Optional[str],
    controlled_by: Optional[str] = None,
    args: Optional[str] = None,
    standalone: bool = False,
    role: Optional[str] = None,
    machine: Optional[str] = None,
    mission: Optional[str] = None,
) -> None:
    """Open a new session on the local Director, or on another computer when a remote --machine is given."""
    # Automatic roles: a SESSION-initiated spawn (CC_SESSION_ID present) DEFAULTS to a Worker controlled by
    # the spawner, so it stays quiet and reports to its manager instead of nagging the human. The opt-out
    # (guard 1) is --standalone / --controlled-by none: a deliberate human-facing PEER with no controller. A
    # human/desktop spawn (no CC_SESSION_ID) is unaffected. An explicit --controlled-by <id> or 'self' wins.
    # (The handover / move-session flow does NOT come through here - it uses POST /handover, which never sets
    # a controller - so a moved session keeps its red visible to the human, guard 2 by construction.)
    controller_session_id: Optional[str] = None
    cc_session = os.environ.get("CC_SESSION_ID")
    opt_out = standalone or (controlled_by is not None and controlled_by.strip().lower() == "none")
    if opt_out:
        controller_session_id = None
    elif controlled_by:
        if controlled_by.strip().lower() == "self":
            controller_session_id = cc_session
            if not controller_session_id:
                console.print(
                    "[red]Error:[/red] --controlled-by self requires CC_SESSION_ID to be set, but it "
                    "is not. Run this from inside a session, or pass an explicit controlling session id."
                )
                raise typer.Exit(1)
        else:
            controller_session_id = controlled_by
    elif cc_session:
        controller_session_id = cc_session
    # Issue #800: always name your session. On this fleet many sessions run in the same
    # checkout, so a session with neither a name nor a purpose still gets an auto-composed
    # name from the Director, but it reads better when you describe what it is FOR.
    if not name and not purpose:
        console.print(
            "[yellow]Warning:[/yellow] no --name or --purpose given; the session will get an "
            "auto-composed name. Pass --purpose \"<what it is for>\" so it is easy to tell apart."
        )

    body: Dict[str, Any] = {"repoPath": repo, "agent": agent}
    # Issue #1017: with no --args, the Director applies the SAME default agent settings (permission
    # mode preset, default model) the desktop New Session dialog uses, so a spawned session is
    # usable for unattended work without hand-fixing permissions. Passing --args overrides that
    # default with an explicit command line for this session only.
    if args is not None:
        body["args"] = args
    if name:
        body["name"] = name
    if purpose:
        body["purpose"] = purpose
    if prompt:
        body["prePrompt"] = prompt
    if command:
        body["command"] = command
    if command_args:
        body["commandArgs"] = command_args
    if controller_session_id:
        body["controllerSessionId"] = controller_session_id
    # Automatic session roles: forward an explicit --role VERBATIM to the Director, which validates it
    # against Standalone/Manager/Worker/Architect and rejects an unknown value (never a silent drop).
    if role:
        body["role"] = role
    # Mission attach at spawn: forward the Mission id; the Director resolves+validates it (unknown -> 400).
    if mission:
        body["missionId"] = mission

    # "Start a session on another computer": both local and remote spawns now go through the Director's
    # loopback POST /fleet/spawn (issue #1490). With no --machine (or a --machine naming THIS machine) the
    # floor spawns LOCALLY (CreateLocalSessionAsync); a remote machine name is forwarded via the Gateway to a
    # Director on that machine (first available, auto-launched if none is running). An off/unreachable machine
    # fails loudly (DirectorError -> red error + exit 1) with NO local fallback. The old POST /sessions route
    # was removed from the Director floor in the tunnel-only cut.
    target_machine = machine.strip() if machine else ""

    try:
        resp = director.post_json("fleet/spawn", {"machine": target_machine, **body})
    except director.DirectorError as err:
        console.print(f"[red]Error:[/red] {err}")
        raise typer.Exit(1)

    sid = director.field(resp, "sessionId", "SessionId")
    if not sid:
        console.print("[red]Error:[/red] the Director did not return a session id.")
        raise typer.Exit(1)

    short = director.short_id(sid)
    # The Director names the session at birth (issue #800), so the response carries the final name.
    label = director.field(resp, "name", "Name") or name or short
    console.print(f"[green]Opened[/green] session {short} ({label}).")
    console.print(f"id: {sid}")
    console.print(
        f'Message it:  cc-devthrottle message send {short} "<message>"'
        f'   |   Ask it:  cc-devthrottle message ask {short} "<question>"'
    )


def _spawn_selftest(repo: str, command_args: str, name: str) -> str:
    resp = director.post_json(
        "sessions",
        {"repoPath": repo, "agent": "RawCli", "command": "cmd", "commandArgs": command_args},
    )
    sid = director.field(resp, "sessionId", "SessionId")
    if not sid:
        raise director.DirectorError("the Director did not return a session id when spawning.")
    try:
        director.patch_json(f"sessions/{sid}", {"name": name})
    except director.DirectorError:
        pass
    return sid


def _fleet_ids() -> List[str]:
    sessions = director.get_json("fleet/sessions") or []
    return [director.field(s, "sessionId", "SessionId") for s in sessions]


def selftest(timeout_ms: int) -> None:
    """Run the fleet messaging self-test against the local Director."""
    repo = tempfile.gettempdir()
    results: List[Tuple[str, bool, str]] = []
    responder: Optional[str] = None
    recipient: Optional[str] = None

    def record(step: str, ok: bool, detail: str = "") -> None:
        results.append((step, ok, detail))
        mark = "[green]PASS[/green]" if ok else "[red]FAIL[/red]"
        console.print(f"  {mark}  {step}{('  - ' + detail) if detail else ''}")

    try:
        responder = _spawn_selftest(repo, f"/k prompt {SELFTEST_MARKER}$G", "selftest-responder")
        recipient = _spawn_selftest(repo, "/k", "selftest-recipient")
        record(
            "spawn two sessions",
            True,
            f"responder={director.short_id(responder)} recipient={director.short_id(recipient)}",
        )
        time.sleep(2)

        ids = _fleet_ids()
        listed = responder in ids and recipient in ids
        record("session list includes both", listed)

        send = director.post_json(
            "fleet/send",
            {"toSessionId": recipient, "text": "fleet self-test message", "fromSessionId": responder},
        )
        accepted = bool(isinstance(send, dict) and send.get("accepted", send.get("Accepted", False)))
        record("message send delivers", accepted, str(director.field(send, "error", "Error") or ""))

        ask = director.post_json(
            "fleet/ask",
            {
                "toSessionId": responder,
                "question": "selftest ping",
                "fromSessionId": recipient,
                "timeoutMs": timeout_ms,
            },
            timeout=timeout_ms / 1000.0 + 15.0,
        )
        answer = director.field(ask, "answer", "Answer") if isinstance(ask, dict) else ""
        got_marker = SELFTEST_MARKER in answer
        record(
            "message ask returns the answer",
            got_marker,
            "marker found" if got_marker else f"status={director.field(ask, 'status', 'Status')}",
        )

    except director.DirectorError as err:
        record("fleet messaging reachable", False, str(err))
    finally:
        for sid in (responder, recipient):
            if sid:
                try:
                    director.delete(f"sessions/{sid}")
                except director.DirectorError:
                    pass
        try:
            time.sleep(1)
            remaining = _fleet_ids()
            leaked = [s for s in (responder, recipient) if s and s in remaining]
            record("throwaway sessions cleaned up", not leaked, "" if not leaked else f"leaked {len(leaked)}")
        except director.DirectorError as err:
            record("throwaway sessions cleaned up", False, str(err))

    passed = sum(1 for _, ok, _ in results if ok)
    total = len(results)
    if passed == total and total > 0:
        console.print(f"[green]PASS[/green] - fleet messaging self-test: {passed}/{total} checks passed.")
        raise typer.Exit(0)
    console.print(f"[red]FAIL[/red] - fleet messaging self-test: {passed}/{total} checks passed.")
    raise typer.Exit(1)
