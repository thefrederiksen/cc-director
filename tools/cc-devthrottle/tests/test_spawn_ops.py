"""Tests for `cc-devthrottle session spawn`: the automatic-worker default + its two guards + --type removal.

The auto-controller default (a session-initiated spawn becomes a Worker of the spawner), the --standalone /
--controlled-by none opt-out (guard 1), an explicit controller, and the human/desktop no-controller case are
asserted by capturing the request body sent to the Director. The dead --type option is asserted gone via the
CLI. (Guard 2 - handover/move-session never auto-sets a controller - holds by construction: that flow does
not route through spawn_session at all; POST /handover creates its target with no controller.)
"""

import sys
from pathlib import Path

import pytest
from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import session_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()


@pytest.fixture
def captured(monkeypatch):
    """Capture the body posted to the Director, without any real HTTP."""
    body = {}

    def fake_post_json(path, b):
        body.clear()
        body.update(b)
        return {"sessionId": "11111111-2222-3333-4444-555555555555", "name": "test"}

    monkeypatch.setattr(session_ops.director, "post_json", fake_post_json)
    return body


def _spawn(monkeypatch, *, cc_session=None, controlled_by=None, standalone=False, role=None):
    if cc_session is None:
        monkeypatch.delenv("CC_SESSION_ID", raising=False)
    else:
        monkeypatch.setenv("CC_SESSION_ID", cc_session)
    session_ops.spawn_session(
        repo="C:/repo",
        agent="ClaudeCode",
        prompt=None,
        name="n",
        purpose=None,
        command=None,
        command_args=None,
        controlled_by=controlled_by,
        args=None,
        standalone=standalone,
        role=role,
    )


def test_session_initiated_spawn_defaults_to_worker(monkeypatch, captured):
    # CC_SESSION_ID present + no explicit controller -> auto-controlled by the spawner (a Worker).
    _spawn(monkeypatch, cc_session="sess-A")
    assert captured.get("controllerSessionId") == "sess-A"


def test_standalone_forces_no_controller_even_inside_a_session(monkeypatch, captured):
    # Guard 1: --standalone opts out of the auto-worker default -> a human-facing peer, no controller.
    _spawn(monkeypatch, cc_session="sess-A", standalone=True)
    assert "controllerSessionId" not in captured


def test_controlled_by_none_forces_no_controller(monkeypatch, captured):
    # Guard 1 alias: --controlled-by none is the same opt-out as --standalone.
    _spawn(monkeypatch, cc_session="sess-A", controlled_by="none")
    assert "controllerSessionId" not in captured


def test_explicit_controlled_by_id_wins(monkeypatch, captured):
    _spawn(monkeypatch, cc_session="sess-A", controlled_by="explicit-manager-id")
    assert captured.get("controllerSessionId") == "explicit-manager-id"


def test_controlled_by_self_resolves_to_cc_session_id(monkeypatch, captured):
    _spawn(monkeypatch, cc_session="sess-A", controlled_by="self")
    assert captured.get("controllerSessionId") == "sess-A"


def test_human_or_desktop_spawn_has_no_controller(monkeypatch, captured):
    # No CC_SESSION_ID (a human/desktop create) -> no auto-controller, unchanged behavior.
    _spawn(monkeypatch, cc_session=None)
    assert "controllerSessionId" not in captured


def test_role_is_forwarded_verbatim(monkeypatch, captured):
    # --role forwards to the Director body verbatim (the Director validates + normalizes / rejects it).
    _spawn(monkeypatch, cc_session=None, role="Architect")
    assert captured.get("role") == "Architect"


def test_no_role_omits_the_field(monkeypatch, captured):
    _spawn(monkeypatch, cc_session=None, role=None)
    assert "role" not in captured


def test_type_option_is_removed(monkeypatch):
    # The dead --type option is gone: the CLI rejects it before running the command (no HTTP happens).
    result = runner.invoke(app, ["session", "spawn", "C:/repo", "--type", "Developer"])
    assert result.exit_code != 0
    assert "No such option" in result.output or "--type" in result.output


# --- Session origin and lineage (devthrottle_internal issue #982) ---
# This process is the only place that can tell a session-initiated spawn from a human one:
# CC_SESSION_ID is injected into a session's environment at birth and is absent from a human's
# own shell, so its presence IS the answer. The Director sees an identical HTTP request either
# way and would have to guess, which is why the CLI states it.


def test_session_initiated_spawn_records_the_agent_origin_and_its_parent(monkeypatch, captured):
    _spawn(monkeypatch, cc_session="sess-A")
    assert captured.get("origin") == "agent"
    assert captured.get("parentSessionId") == "sess-A"
    assert captured.get("originSurface") == "cli"


def test_human_spawn_records_the_human_origin_and_no_parent(monkeypatch, captured):
    _spawn(monkeypatch, cc_session=None)
    assert captured.get("origin") == "human"
    assert "parentSessionId" not in captured
    assert captured.get("originSurface") == "cli"


def test_standalone_keeps_the_lineage_even_though_it_drops_the_controller(monkeypatch, captured):
    # The case that separates lineage from supervision. --standalone deliberately creates a
    # human-facing PEER with no controller, so nothing about the running session says an agent
    # made it. It is still an agent-started session, and that is exactly what is being counted.
    _spawn(monkeypatch, cc_session="sess-A", standalone=True)
    assert "controllerSessionId" not in captured
    assert captured.get("origin") == "agent"
    assert captured.get("parentSessionId") == "sess-A"


def test_an_explicit_controller_does_not_change_who_made_the_call(monkeypatch, captured):
    # --controlled-by names who SUPERVISES the new session; the parent is who ASKED for it. A
    # session seating its worker under a different manager is still the session that spawned it.
    _spawn(monkeypatch, cc_session="sess-A", controlled_by="explicit-manager-id")
    assert captured.get("controllerSessionId") == "explicit-manager-id"
    assert captured.get("parentSessionId") == "sess-A"
