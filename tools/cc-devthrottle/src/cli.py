"""CLI for cc-devthrottle - unified DevThrottle command surface."""

from __future__ import annotations

import json
from typing import List, Optional

import typer
from rich.console import Console
from rich.table import Table

from . import __version__
from . import diag_ops
from . import email_ops
from . import mission_ops
from . import schedule_ops
from . import settings_ops
from . import setup_ops
from . import workflow_ops
from .session_ops import (
    ask_session,
    hold_session,
    interrupt_session,
    list_sessions,
    mark_done,
    prompt_session,
    read_session_buffer,
    rename_session,
    set_session_role,
    selftest as run_selftest,
    send_message,
    spawn_session,
    whoami as show_whoami,
)

app = typer.Typer(
    name="cc-devthrottle",
    help="Unified DevThrottle command-line surface.",
    add_completion=False,
    no_args_is_help=True,
)
session_app = typer.Typer(help="Manage running sessions.", add_completion=False)
mission_app = typer.Typer(
    help="Create and list Missions (the unit of work sessions attach to).",
    add_completion=False,
    no_args_is_help=True,
)
message_app = typer.Typer(help="Send messages between sessions.", add_completion=False)
settings_app = typer.Typer(
    help="Read and write CC Director settings.", add_completion=False, no_args_is_help=True
)
schedule_app = typer.Typer(
    help="Manage Gateway schedules.", add_completion=False, no_args_is_help=True
)
workflow_app = typer.Typer(
    help="Read and author fleet Workflows (cross-agent conduct stored on the Gateway).",
    add_completion=False,
    no_args_is_help=True,
)
setup_app = typer.Typer(
    help="Install, update, and repair DevThrottle.", add_completion=False, no_args_is_help=True
)
email_app = typer.Typer(
    help="Send email to the account owner.", add_completion=False, no_args_is_help=True
)
diag_app = typer.Typer(
    help="Run network diagnostics (Tailscale direct-vs-relay, speed results).",
    add_completion=False,
    no_args_is_help=True,
)
app.add_typer(session_app, name="session")
app.add_typer(mission_app, name="mission")
app.add_typer(message_app, name="message")
app.add_typer(settings_app, name="settings")
app.add_typer(schedule_app, name="schedule")
app.add_typer(workflow_app, name="workflow")
app.add_typer(setup_app, name="setup")
app.add_typer(email_app, name="email")
app.add_typer(diag_app, name="diag")
console = Console()

_ACTIONS = [
    {
        "id": "session-list",
        "description": "List every session in the fleet.",
        "command": "cc-devthrottle session list",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "session-whoami",
        "description": "Show this session's id, name, machine, and repository.",
        "command": "cc-devthrottle session whoami",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "session-rename-current",
        "description": "Rename the current session using CC_SESSION_ID.",
        "command": 'cc-devthrottle session rename "<new name>"',
        "mutatesState": True,
        "args": [{"name": "new_name", "required": True}],
    },
    {
        "id": "session-rename-target",
        "description": "Rename a session selected by full id, id prefix, or exact name.",
        "command": 'cc-devthrottle session rename <target> "<new name>"',
        "mutatesState": True,
        "args": [
            {"name": "target", "required": True},
            {"name": "new_name", "required": True},
        ],
    },
    {
        "id": "session-spawn",
        "description": "Open a new session on the local Director.",
        "command": "cc-devthrottle session spawn <repo>",
        "mutatesState": True,
        "args": [{"name": "repo", "required": True}],
    },
    {
        "id": "session-hold",
        "description": (
            "Park a session so it stops asking for attention, for a set number of minutes. Defaults "
            "to THIS session. A session holding ITSELF is always mid-turn, so the hold is deferred "
            "automatically and lands when the turn ends - there is no separate verb for that, and the "
            "reply says 'pending' when it deferred. Only the owner lifts a hold: releasing it, typing "
            "or speaking into the session, or the timer expiring. Another agent's message does not."
        ),
        "command": "cc-devthrottle session hold [target] --minutes <n>",
        "mutatesState": True,
        "args": [
            {"name": "target", "required": False},
            {"name": "minutes", "required": False},
        ],
    },
    {
        "id": "session-hold-release",
        "description": "Release a hold, bringing a parked session back into the normal roster.",
        "command": "cc-devthrottle session hold [target] --release",
        "mutatesState": True,
        "args": [{"name": "target", "required": False}],
    },
    {
        "id": "message-send",
        "description": "Send a one-way message to a session, or broadcast to all sessions.",
        "command": 'cc-devthrottle message send <target|all> "<message>"',
        "mutatesState": True,
        "args": [
            {"name": "target", "required": True},
            {"name": "message", "required": True},
        ],
    },
    {
        "id": "message-ask",
        "description": "Ask one session a question and print its answer.",
        "command": 'cc-devthrottle message ask <target> "<question>"',
        "mutatesState": True,
        "args": [
            {"name": "target", "required": True},
            {"name": "question", "required": True},
        ],
    },
    {
        "id": "fleet-selftest",
        "description": "Run an end-to-end fleet messaging smoke test.",
        "command": "cc-devthrottle selftest",
        "mutatesState": True,
        "args": [],
    },
    {
        "id": "settings-show",
        "description": "Display current CC Director settings.",
        "command": "cc-devthrottle settings show",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "settings-get",
        "description": "Get a CC Director setting by dotted key.",
        "command": "cc-devthrottle settings get <key>",
        "mutatesState": False,
        "args": [{"name": "key", "required": True}],
    },
    {
        "id": "settings-set",
        "description": "Set a CC Director setting by dotted key.",
        "command": "cc-devthrottle settings set <key> <value>",
        "mutatesState": True,
        "args": [
            {"name": "key", "required": True},
            {"name": "value", "required": True},
        ],
    },
    {
        "id": "settings-list",
        "description": "List all available CC Director setting keys.",
        "command": "cc-devthrottle settings list",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "settings-path",
        "description": "Show the local CC Director config file path.",
        "command": "cc-devthrottle settings path",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "schedule-list",
        "description": "List Gateway schedules.",
        "command": "cc-devthrottle schedule list",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "schedule-get",
        "description": "Show one Gateway schedule in full.",
        "command": "cc-devthrottle schedule get <id>",
        "mutatesState": False,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "schedule-runs",
        "description": "Show run history for a Gateway schedule.",
        "command": "cc-devthrottle schedule runs <id>",
        "mutatesState": False,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "schedule-create",
        "description": "Create a Gateway schedule.",
        "command": "cc-devthrottle schedule create --name <name> --machine <machine> --repo <repo> --cron <expr> --tz <tz> --seed <prompt>",
        "mutatesState": True,
        "args": [
            {"name": "name", "required": True},
            {"name": "machine", "required": True},
            {"name": "repo", "required": True},
            {"name": "cron_or_at", "required": True},
            {"name": "tz", "required": True},
            {"name": "seed_or_worklist", "required": True},
        ],
    },
    {
        "id": "schedule-run",
        "description": "Fire a Gateway schedule immediately.",
        "command": "cc-devthrottle schedule run <id>",
        "mutatesState": True,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "schedule-enable",
        "description": "Enable a Gateway schedule.",
        "command": "cc-devthrottle schedule enable <id>",
        "mutatesState": True,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "schedule-disable",
        "description": "Disable a Gateway schedule.",
        "command": "cc-devthrottle schedule disable <id>",
        "mutatesState": True,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "schedule-delete",
        "description": "Delete a Gateway schedule.",
        "command": "cc-devthrottle schedule delete <id>",
        "mutatesState": True,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "schedule-endpoint",
        "description": "Show the Gateway endpoint used by schedule commands.",
        "command": "cc-devthrottle schedule endpoint",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "workflow-list",
        "description": "List the fleet's Workflows (cross-agent conduct stored on the Gateway).",
        "command": "cc-devthrottle workflow list",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "workflow-instructions",
        "description": "Print a Workflow's raw instruction markdown - fetch this and FOLLOW it as your conduct.",
        "command": "cc-devthrottle workflow instructions <id>",
        "mutatesState": False,
        "args": [{"name": "id", "required": True}, {"name": "version", "required": False}],
    },
    {
        "id": "workflow-show",
        "description": "Show one Workflow's metadata, steps, and outcome criteria.",
        "command": "cc-devthrottle workflow show <id>",
        "mutatesState": False,
        "args": [{"name": "id", "required": True}, {"name": "version", "required": False}],
    },
    {
        "id": "workflow-versions",
        "description": "Show a Workflow's version history.",
        "command": "cc-devthrottle workflow versions <id>",
        "mutatesState": False,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "workflow-pull",
        "description": "Pull a Workflow into a directory (workflow.json + instructions.md + helpers/) for editing.",
        "command": 'cc-devthrottle workflow pull <id> --dir "<dir>"',
        "mutatesState": False,
        "args": [{"name": "id", "required": True}, {"name": "dir", "required": True}],
    },
    {
        "id": "workflow-push",
        "description": "Push an edited Workflow directory to the Gateway as a draft (creates the Workflow if new).",
        "command": 'cc-devthrottle workflow push <id> --dir "<dir>" [--note "<what changed>"]',
        "mutatesState": True,
        "args": [{"name": "id", "required": True}, {"name": "dir", "required": True}],
    },
    {
        "id": "workflow-publish",
        "description": "Publish a Workflow's draft - it becomes the version every machine and agent reads.",
        "command": "cc-devthrottle workflow publish <id>",
        "mutatesState": True,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "workflow-materialize",
        "description": "Write a Workflow's instructions and helper files to this machine's cache and print the paths.",
        "command": "cc-devthrottle workflow materialize <id>",
        "mutatesState": True,
        "args": [{"name": "id", "required": True}, {"name": "version", "required": False}],
    },
    {
        "id": "workflow-reset",
        "description": "Reset a built-in Workflow to the shipped content (published as a new version).",
        "command": "cc-devthrottle workflow reset <id>",
        "mutatesState": True,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "workflow-delete",
        "description": "Archive a custom Workflow (built-ins can never be deleted; history remains).",
        "command": "cc-devthrottle workflow delete <id> --yes",
        "mutatesState": True,
        "args": [{"name": "id", "required": True}],
    },
    {
        "id": "email-owner",
        "description": "Email the account owner (single recipient); optional file attachments. The escalation channel for unattended runs.",
        "command": 'cc-devthrottle email owner --subject "<subject>" --body "<text>" [--attach <file>]',
        "mutatesState": True,
        "args": [
            {"name": "subject", "required": True},
            {"name": "body", "required": False},
            {"name": "html", "required": False},
            {"name": "attach", "required": False},
        ],
    },
    {
        "id": "setup-status",
        "description": "Show local DevThrottle setup status.",
        "command": "cc-devthrottle setup status",
        "mutatesState": False,
        "args": [],
    },
    {
        "id": "setup-install",
        "description": "Install or repair DevThrottle from the latest GitHub release.",
        "command": "cc-devthrottle setup install",
        "mutatesState": True,
        "args": [],
    },
    {
        "id": "setup-update",
        "description": "Update DevThrottle through the setup engine.",
        "command": "cc-devthrottle setup update",
        "mutatesState": True,
        "args": [],
    },
    {
        "id": "setup-repair",
        "description": "Repair DevThrottle through the setup engine.",
        "command": "cc-devthrottle setup repair",
        "mutatesState": True,
        "args": [],
    },
    {
        "id": "setup-doctor",
        "description": "Show local DevThrottle setup diagnostics and repair guidance.",
        "command": "cc-devthrottle setup doctor --json",
        "mutatesState": False,
        "args": [],
    },
]


def _version_callback(value: bool) -> None:
    if value:
        console.print(f"cc-devthrottle v{__version__}")
        raise typer.Exit()


@app.callback()
def main(
    version: bool = typer.Option(
        False, "--version", "-v", callback=_version_callback, is_eager=True, help="Show version."
    ),
) -> None:
    """Unified DevThrottle command-line surface."""


@app.command()
def actions(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """List agent-discoverable actions."""
    if json_output:
        print(json.dumps({"actions": _ACTIONS}, indent=2))
        return

    table = Table(show_header=True, header_style="bold")
    table.add_column("ACTION")
    table.add_column("COMMAND")
    for action in _ACTIONS:
        table.add_row(str(action["id"]), str(action["command"]))
    console.print(table)


@session_app.command("list")
def session_list(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output raw JSON."),
) -> None:
    """List every session running across the fleet."""
    list_sessions(json_output)


@session_app.command()
def whoami() -> None:
    """Show this session's own fleet identity."""
    show_whoami()


@session_app.command()
def rename(
    target_or_name: str = typer.Argument(
        ..., help="New name for this session, or a target when NEW_NAME is also provided."
    ),
    new_name: Optional[str] = typer.Argument(
        None, help="New name when an explicit target is provided."
    ),
) -> None:
    """Rename a session, defaulting to the current session."""
    if new_name is None:
        rename_session(None, target_or_name)
    else:
        rename_session(target_or_name, new_name)


@session_app.command()
def prompt(
    target: str = typer.Argument(..., help="Session to prompt (id prefix, number, or exact name)."),
    text: str = typer.Argument(..., help="The text to send."),
    no_submit: bool = typer.Option(
        False, "--no-submit", help="Type the text but do not press Enter - leave it in the composer."
    ),
) -> None:
    """Send raw text into a session, as if you had typed it.

    Unlike `message send`, the text is NOT framed with a sender - the session sees exactly what
    you passed. Use `message send` for agent-to-agent messages, and this to drive a session.
    """
    prompt_session(target, text, no_submit=no_submit)


@session_app.command()
def interrupt(
    target: Optional[str] = typer.Argument(
        None, help="Session to interrupt. Defaults to THIS session (CC_SESSION_ID)."
    ),
) -> None:
    """Stop what a session is currently doing."""
    interrupt_session(target)


@session_app.command()
def hold(
    target: Optional[str] = typer.Argument(
        None, help="Session to hold. Defaults to THIS session (CC_SESSION_ID)."
    ),
    release: bool = typer.Option(False, "--release", help="Release the hold instead of applying one."),
    minutes: Optional[int] = typer.Option(
        None, "--minutes", help="Hold for this many minutes, then surface it again."
    ),
) -> None:
    """Park a session so it stops asking for you, or release it.

    Hold THIS session when you have nothing left to report and do not want to keep asking for
    attention: `cc-devthrottle session hold --minutes 720`. You do not need a special verb for
    that. A hold asked for while the session is still working - which is always the case when a
    session holds ITSELF, since it is mid-turn - is DEFERRED automatically: it applies the moment
    the turn finishes, and the reply tells you so with `pending`.

    ONLY THE OWNER LIFTS A HOLD - by releasing it, by typing or speaking into the session, or by
    the --minutes timer running out. Another agent messaging the session, or the terminal simply
    repainting, no longer un-holds it, so a hold you set actually lasts as long as you asked for.
    """
    hold_session(target, release=release, minutes=minutes)


@session_app.command()
def buffer(
    target: Optional[str] = typer.Argument(
        None, help="Session to read. Defaults to THIS session (CC_SESSION_ID)."
    ),
) -> None:
    """Print what a session's terminal is showing - how you see what a session is actually doing."""
    read_session_buffer(target)


@session_app.command()
def role(
    role_or_target: str = typer.Argument(
        ...,
        help="Role for this session (Standalone, Manager, Worker, Architect), or a target when ROLE is "
             "also provided. Pass 'none' to clear the explicit role.",
    ),
    role_value: Optional[str] = typer.Argument(
        None, help="Role when an explicit target is provided."
    ),
) -> None:
    """Declare a session's explicit role, defaulting to the current session.

    Valid roles: Standalone, Manager, Worker, Architect (case-insensitive). Pass 'none' to clear
    the explicit role and revert to automatic derivation.

    Worker and Manager are normally derived from the fleet: a controlled session with a live
    controller is a Worker; a session controlling a live session is a Manager. Architect cannot be
    inferred from the spawn graph, so declaring it here is the only way to make one after birth.
    An explicit role is sticky and wins over derivation.
    """
    if role_value is None:
        target, wanted = None, role_or_target
    else:
        target, wanted = role_or_target, role_value
    # "none" is the CLI's way to say "clear it" - the endpoint clears on an empty role.
    set_session_role(target, "" if wanted.strip().lower() == "none" else wanted)


@session_app.command()
def done(
    target: Optional[str] = typer.Argument(
        None, help="Session to mark for deletion. Defaults to THIS session (CC_SESSION_ID)."
    ),
    reason: Optional[str] = typer.Option(
        None, "--reason", help="Short reason, shown while the session winds down."
    ),
) -> None:
    """Flag a session for deletion (defaults to the current session).

    Does NOT kill the session now - it is flagged, and the owning Director's reaper removes it
    within about a minute once the grace window passes and it is no longer working. Use this at
    the end of an unattended run that has nothing left for the user, so the session tears itself
    down instead of lingering in the fleet.
    """
    mark_done(target, reason)


@session_app.command()
def spawn(
    repo: str = typer.Argument(..., help="Absolute path to the repository / working directory."),
    agent: str = typer.Option(
        "ClaudeCode",
        "--agent",
        help="Agent CLI: ClaudeCode, Pi, Codex, Gemini, OpenCode, Grok, Copilot, RawCli.",
    ),
    prompt: Optional[str] = typer.Option(
        None, "--prompt", help="First prompt to send once the session is ready."
    ),
    name: Optional[str] = typer.Option(None, "--name", help="Custom display name for the session."),
    purpose: Optional[str] = typer.Option(
        None,
        "--purpose",
        help="Short description of what the session is FOR (e.g. 'implement #799'); used to "
        "build the session name when no --name is given.",
    ),
    command: Optional[str] = typer.Option(
        None, "--command", help="For --agent RawCli: the executable to run (e.g. cmd, pwsh)."
    ),
    command_args: Optional[str] = typer.Option(
        None, "--command-args", help="For --agent RawCli: arguments for the command."
    ),
    args: Optional[str] = typer.Option(
        None,
        "--args",
        help="Override the agent's command-line arguments for this session (issue #1017). "
        "When omitted, the session inherits the same default agent settings (permission mode, "
        "default model) the desktop New Session dialog applies.",
    ),
    controlled_by: Optional[str] = typer.Option(
        None,
        "--controlled-by",
        help="Controlling session for the new session (issue #815 / automatic roles). By DEFAULT a "
        "session-initiated spawn (CC_SESSION_ID set) becomes a Worker controlled by the spawner, so it "
        "stays quiet and reports to its manager. Pass an explicit session id to be controlled by a "
        "different session, 'self' for this session, or 'none' (same as --standalone) to spawn a "
        "human-facing PEER with no controller.",
    ),
    standalone: bool = typer.Option(
        False,
        "--standalone",
        help="Spawn a human-facing PEER, not a subordinate Worker: force NO controller even when run "
        "from inside a session. The opt-out for the automatic-worker default.",
    ),
    role: Optional[str] = typer.Option(
        None,
        "--role",
        help="Explicit session role (automatic session roles): Standalone, Manager, Worker, or Architect "
        "(case-insensitive). Sticky, and wins over auto-derivation - the way to declare an Architect. An "
        "unknown value is rejected by the Director.",
    ),
    machine: Optional[str] = typer.Option(
        None,
        "--machine",
        help="Start the session on ANOTHER computer. Omit (or name this machine) to spawn locally, "
        "unchanged. A remote machine name routes the spawn through the Gateway to a Director on that "
        "machine (first available, auto-launched if none is running); an off/unreachable machine fails "
        "loudly with no local fallback.",
    ),
    mission: Optional[str] = typer.Option(
        None,
        "--mission",
        help="Attach the new session to a Mission by its id at spawn (mission-as-first-class-unit-of-work). "
        "The Mission must already exist (create one with 'cc-devthrottle mission create'); an unknown "
        "Mission is rejected by the Director.",
    ),
) -> None:
    """Open a new session on the local Director, or on another computer with --machine, and print its id."""
    spawn_session(
        repo, agent, prompt, name, purpose, command, command_args, controlled_by, args, standalone, role,
        machine, mission,
    )


@mission_app.command("create")
def mission_create(
    name: str = typer.Argument(..., help="Human-friendly name for the Mission."),
    parent: Optional[str] = typer.Option(
        None, "--parent", help="Parent Mission id to nest this Mission under (a tree of Missions)."
    ),
) -> None:
    """Create a Mission record on the Gateway and print its id."""
    mission_ops.create_mission(name, parent)


@mission_app.command("list")
def mission_list(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output raw JSON."),
) -> None:
    """List every Mission record on the Gateway."""
    mission_ops.list_missions(json_output)


@diag_app.command("network")
def diag_network(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output raw JSON."),
) -> None:
    """Server-side network check: per connected device, direct-vs-DERP-relay + latency, plus UDP/NAT.

    Runs on the Gateway with no phone and no app open - the check an agent uses to tell "warming up on
    the relay" apart from "genuinely slow".
    """
    diag_ops.show_network(json_output)


@diag_app.command("results")
def diag_results(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output raw JSON."),
) -> None:
    """Recent speed-test results users submitted from the app or Cockpit (newest first)."""
    diag_ops.show_results(json_output)


@message_app.command("send")
def message_send(
    target: str = typer.Argument(..., help="Target session id, id prefix, or name - or 'all' for your team."),
    message: str = typer.Argument(..., help="The message text to send."),
    everyone: bool = typer.Option(
        False,
        "--everyone",
        help="Broadcast to the WHOLE fleet, not just your own team. Every message interrupts the "
        "receiving agent, so this is gated by the Gateway Hub: it needs a human-issued grant (--grant) "
        "and a --reason, and is refused otherwise (issue #1229). Without this flag, 'all' reaches only "
        "your team - the sessions in your Mission, or (solo) the same repository on the same machine.",
    ),
    reason: Optional[str] = typer.Option(
        None,
        "--reason",
        help="Why a fleet-wide broadcast is warranted. Required with --everyone; logged by the Hub.",
    ),
    grant: Optional[str] = typer.Option(
        None,
        "--grant",
        help="A human-issued broadcast grant id authorizing a fleet-wide broadcast (--everyone).",
    ),
) -> None:
    """Send a message to one session, or to your team when TARGET is 'all' (add --everyone for the whole fleet)."""
    send_message(target, message, everyone=everyone, reason=reason, grant=grant)


@message_app.command("ask")
def message_ask(
    target: str = typer.Argument(..., help="Target session id, id prefix, or name (single session)."),
    question: str = typer.Argument(..., help="The question to ask."),
    timeout_ms: int = typer.Option(
        120000, "--timeout-ms", help="How long to wait for the answer, in milliseconds."
    ),
) -> None:
    """Ask TARGET the QUESTION and print the answer."""
    ask_session(target, question, timeout_ms)


@app.command()
def selftest(
    timeout_ms: int = typer.Option(
        25000, "--timeout-ms", help="How long the ask step waits for the responder."
    ),
) -> None:
    """Run the fleet messaging self-test against the local Director."""
    run_selftest(timeout_ms)


@settings_app.command("show")
def settings_show(
    section: Optional[str] = typer.Argument(
        None, help="Section name to show, e.g. screenshots, vault, or llm."
    ),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Display current settings."""
    settings_ops.show(section, json_output)


@settings_app.command("get")
def settings_get(
    key: str = typer.Argument(..., help="Setting key, e.g. screenshots.source_directory."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Get a specific setting value."""
    settings_ops.get(key, json_output)


@settings_app.command("set")
def settings_set(
    key: str = typer.Argument(..., help="Setting key, e.g. screenshots.source_directory."),
    value: str = typer.Argument(..., help="New value."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Set a configuration value."""
    settings_ops.set_config_value(key, value, json_output)


@settings_app.command("list")
def settings_list(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """List all setting keys."""
    settings_ops.list_settings(json_output)


@settings_app.command("path")
def settings_path(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show the config file location."""
    settings_ops.path(json_output)


@schedule_app.callback()
def schedule_main(
    gateway: Optional[str] = typer.Option(
        None,
        "--gateway",
        help="Override the Gateway base URL.",
    ),
) -> None:
    """Manage Gateway schedules."""
    schedule_ops.set_gateway_override(gateway)


@workflow_app.callback()
def workflow_main(
    gateway: Optional[str] = typer.Option(
        None,
        "--gateway",
        help="Override the Gateway base URL.",
    ),
) -> None:
    """Read and author fleet Workflows (cross-agent conduct stored on the Gateway)."""
    workflow_ops.set_gateway_override(gateway)


@workflow_app.command("list")
def workflow_list(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """List every Workflow the fleet can run."""
    workflow_ops.list_workflows(json_output)


@workflow_app.command("show")
def workflow_show(
    workflow_id: str = typer.Argument(..., help="The workflow id (e.g. mission)."),
    version: Optional[int] = typer.Option(
        None, "--version", "-v", help="A specific version instead of the published one."
    ),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show one Workflow's metadata, steps, and outcome criteria."""
    workflow_ops.show_workflow(workflow_id, version, json_output)


@workflow_app.command("instructions")
def workflow_instructions(
    workflow_id: str = typer.Argument(..., help="The workflow id (e.g. mission)."),
    version: Optional[int] = typer.Option(
        None, "--version", "-v", help="A specific pinned version instead of the published one."
    ),
) -> None:
    """Print the Workflow's raw instruction markdown - fetch this and FOLLOW it as your conduct."""
    workflow_ops.print_instructions(workflow_id, version)


@workflow_app.command("versions")
def workflow_versions(
    workflow_id: str = typer.Argument(..., help="The workflow id."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show a Workflow's version history, newest first."""
    workflow_ops.list_versions(workflow_id, json_output)


@workflow_app.command("pull")
def workflow_pull(
    workflow_id: str = typer.Argument(..., help="The workflow id."),
    directory: str = typer.Option(..., "--dir", "-d", help="Directory to write the workflow into."),
    version: Optional[int] = typer.Option(
        None, "--version", "-v", help="A specific version (default: the draft if one exists, else the published version)."
    ),
) -> None:
    """Pull a Workflow into a directory (workflow.json + instructions.md + helpers/) for editing."""
    workflow_ops.pull_workflow(workflow_id, directory, version)


@workflow_app.command("push")
def workflow_push(
    workflow_id: str = typer.Argument(..., help="The workflow id."),
    directory: str = typer.Option(..., "--dir", "-d", help="Directory holding the workflow files."),
    note: Optional[str] = typer.Option(None, "--note", help="One line describing what changed."),
) -> None:
    """Push a Workflow directory to the Gateway as a draft (creates the Workflow if new)."""
    workflow_ops.push_workflow(workflow_id, directory, note)


@workflow_app.command("publish")
def workflow_publish(
    workflow_id: str = typer.Argument(..., help="The workflow id."),
) -> None:
    """Publish the draft - it becomes the version every machine and agent reads."""
    workflow_ops.publish_workflow(workflow_id)


@workflow_app.command("materialize")
def workflow_materialize(
    workflow_id: str = typer.Argument(..., help="The workflow id."),
    version: Optional[int] = typer.Option(
        None, "--version", "-v", help="A specific published version (default: the current one)."
    ),
) -> None:
    """Write the Workflow's instructions and helper files to this machine's cache and print the paths."""
    workflow_ops.materialize_workflow(workflow_id, version)


@workflow_app.command("reset")
def workflow_reset(
    workflow_id: str = typer.Argument(..., help="The built-in workflow id (e.g. mission)."),
) -> None:
    """Reset a built-in Workflow to the shipped content (published as a new version)."""
    workflow_ops.reset_workflow(workflow_id)


@workflow_app.command("delete")
def workflow_delete(
    workflow_id: str = typer.Argument(..., help="The workflow id."),
    yes: bool = typer.Option(False, "--yes", "-y", help="Skip the confirmation prompt."),
) -> None:
    """Archive a custom Workflow (built-ins can never be deleted; version history remains)."""
    workflow_ops.delete_workflow(workflow_id, yes)


@schedule_app.command("list")
def schedule_list(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """List every schedule on the Gateway."""
    schedule_ops.list_jobs(json_output)


@schedule_app.command("get")
def schedule_get(
    job_id: str = typer.Argument(..., help="The schedule id."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show one schedule in full."""
    schedule_ops.get_job(job_id, json_output)


@schedule_app.command("runs")
def schedule_runs(
    job_id: str = typer.Argument(..., help="The schedule id."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show run history for a schedule."""
    schedule_ops.list_runs(job_id, json_output)


@schedule_app.command("create")
def schedule_create(
    name: str = typer.Option(..., "--name", help="Human-readable label for the schedule."),
    machine: str = typer.Option(..., "--machine", help="Target machine name."),
    repo: str = typer.Option(..., "--repo", help="Working directory the fired session runs in."),
    at: Optional[str] = typer.Option(None, "--at", help="One-off local timestamp."),
    cron: Optional[str] = typer.Option(None, "--cron", help="Recurring 5-field cron expression."),
    tz: str = typer.Option(..., "--tz", help="IANA/Windows time zone id."),
    seed: Optional[str] = typer.Option(None, "--seed", help="Skill or prompt the session runs."),
    worklist: Optional[str] = typer.Option(None, "--worklist", help="Named work list to drain."),
    notify_on: str = typer.Option(
        schedule_ops.NOTIFY_NONE,
        "--notify-on",
        help="Run-complete notification: none, always, or failure.",
    ),
    notify_webhook: Optional[str] = typer.Option(
        None,
        "--notify-webhook",
        help="Optional outbound webhook URL.",
    ),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output the created schedule as JSON."),
) -> None:
    """Create a schedule, one-off with --at or recurring with --cron."""
    schedule_ops.create_job(
        name,
        machine,
        repo,
        at,
        cron,
        tz,
        seed,
        worklist,
        notify_on,
        notify_webhook,
        json_output,
    )


@schedule_app.command("run")
def schedule_run(
    job_id: str = typer.Argument(..., help="The schedule id."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output the run record as JSON."),
) -> None:
    """Fire a schedule immediately."""
    schedule_ops.run_now(job_id, json_output)


@schedule_app.command("enable")
def schedule_enable(job_id: str = typer.Argument(..., help="The schedule id.")) -> None:
    """Enable a schedule so it fires on schedule again."""
    schedule_ops.enable_job(job_id)


@schedule_app.command("disable")
def schedule_disable(job_id: str = typer.Argument(..., help="The schedule id.")) -> None:
    """Disable a schedule while keeping its definition."""
    schedule_ops.disable_job(job_id)


@schedule_app.command("delete")
def schedule_delete(job_id: str = typer.Argument(..., help="The schedule id.")) -> None:
    """Delete a schedule from the Gateway."""
    schedule_ops.delete_job(job_id)


@schedule_app.command("endpoint")
def schedule_endpoint(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show the Gateway base URL used by schedule commands."""
    schedule_ops.endpoint(json_output)


@setup_app.command("status")
def setup_status(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show local DevThrottle setup status."""
    setup_ops.status(json_output)


@setup_app.command("install")
def setup_install(
    role: str = typer.Option(
        "workstation", "--role", help="Install role: workstation or gateway."
    ),
    dry_run: bool = typer.Option(False, "--dry-run", help="Plan only; do not apply changes."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Install DevThrottle from the latest GitHub release."""
    setup_ops.install(role, dry_run, json_output)


@setup_app.command("update")
def setup_update(
    role: str = typer.Option(
        "workstation", "--role", help="Install role: workstation or gateway."
    ),
    dry_run: bool = typer.Option(False, "--dry-run", help="Plan only; do not apply changes."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Update DevThrottle from the latest GitHub release."""
    setup_ops.update(role, dry_run, json_output)


@setup_app.command("repair")
def setup_repair(
    role: str = typer.Option(
        "workstation", "--role", help="Install role: workstation or gateway."
    ),
    dry_run: bool = typer.Option(False, "--dry-run", help="Plan only; do not apply changes."),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Repair the local DevThrottle install."""
    setup_ops.repair(role, dry_run, json_output)


@setup_app.command("doctor")
def setup_doctor(
    json_output: bool = typer.Option(False, "--json", "-j", help="Output as JSON."),
) -> None:
    """Show setup diagnostics."""
    setup_ops.doctor(json_output)


@email_app.callback()
def email_main(
    gateway: Optional[str] = typer.Option(
        None,
        "--gateway",
        help="Override the Gateway base URL.",
    ),
) -> None:
    """Send email to the account owner."""
    email_ops.set_gateway_override(gateway)


@email_app.command("owner")
def email_owner(
    subject: str = typer.Option(..., "--subject", help="The email subject line."),
    body: Optional[str] = typer.Option(
        None, "--body", help="Plain-text body. Provide --body, --html, and/or --attach."
    ),
    html: Optional[str] = typer.Option(
        None, "--html", help="HTML body. Provide --body, --html, and/or --attach."
    ),
    attach: Optional[List[str]] = typer.Option(
        None, "--attach", help="Attach a file (repeatable), e.g. an HTML report to read offline."
    ),
    json_output: bool = typer.Option(False, "--json", "-j", help="Output the send result as JSON."),
) -> None:
    """Send ONE email to the account owner (single recipient - no way to address anyone else).

    Passes only a subject, body, and any attachments to the Gateway, which relays it to the cloud
    with your account token; the cloud resolves the owner and sends. Use it to escalate from an
    unattended or scheduled run, or to send yourself a report to read offline.
    """
    email_ops.send_owner(subject, body, html, attach, json_output)


if __name__ == "__main__":
    app()
