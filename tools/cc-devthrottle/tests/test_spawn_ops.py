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
    """Capture the body posted to the Gateway, without any real HTTP.

    Remove-the-network-port mission, phase 2: the spawn goes to the Gateway now, and the path carries
    WHERE - /directors/{id}/sessions for this session's own Director, /machines/{name}/sessions for
    another computer. CC_DIRECTOR_ID is set here because that is how a session names "here" once the
    loopback port that used to mean it is gone. The captured body also records the path, so the tests
    below can assert the destination as well as the payload.
    """
    body = {}

    def fake_post_json(path, b):
        body.clear()
        body.update(b)
        body["_path"] = path
        return {"sessionId": "11111111-2222-3333-4444-555555555555", "name": "test"}

    monkeypatch.setenv("CC_DIRECTOR_ID", "my-director")
    monkeypatch.setattr(session_ops.gateway, "post_json", fake_post_json)
    return body


def _spawn(
    monkeypatch,
    *,
    cc_session=None,
    controlled_by=None,
    standalone=False,
    role=None,
    mission=None,
    roster=None,
):
    if cc_session is None:
        monkeypatch.delenv("CC_SESSION_ID", raising=False)
    else:
        monkeypatch.setenv("CC_SESSION_ID", cc_session)
    # Mission inheritance (issue #2387) reads the fleet roster to find the CONTROLLING session's
    # mission, so every spawn with a controller now consults it. Stubbed here - and defaulting to an
    # EMPTY roster - so these cases keep asserting what they were written to assert and no test
    # reaches the network to find out.
    monkeypatch.setattr(
        session_ops.gateway, "get_fleet", lambda: (list(roster or []), True, None, None)
    )
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
        mission=mission,
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


# ---- --director: naming ONE Director instead of a computer -------------------------------------
#
# A machine runs several named Director instances, so --machine lands on whichever the Gateway
# resolves first. --director names one.
#
# Remove-the-network-port mission, phase 2: WHERE a spawn lands is now carried by the PATH, not by a
# field in the body, so that is what these assert. The Director floor used to take a machine and a
# Director name and settle it locally; with the floor out of the path, "one named Director" has to be
# addressed by id at /directors/{id}/sessions - the Gateway's machine route picks a Director for
# itself and gives the caller no way to say which. That makes the name-to-id lookup this tool's job,
# and an ambiguous name is refused rather than guessed: silently picking one of two Directors called
# the same thing is how a session lands on the wrong computer and nobody notices.

DIRECTORS = [
    {"directorId": "dir-north-1", "displayName": "North build", "machineName": "SOREN_NORTH"},
    {"directorId": "dir-south-1", "displayName": "South build", "machineName": "SOREN_SOUTH"},
    {"directorId": "dir-north-2", "displayName": "Twin", "machineName": "SOREN_NORTH"},
    {"directorId": "dir-south-2", "displayName": "Twin", "machineName": "SOREN_SOUTH"},
]


@pytest.fixture
def directors(monkeypatch):
    monkeypatch.setattr(session_ops.gateway, "get_json", lambda path: DIRECTORS)


def test_a_named_director_is_addressed_by_its_own_id(monkeypatch, captured, directors):
    monkeypatch.delenv("CC_SESSION_ID", raising=False)
    session_ops.spawn_session(
        repo="C:/repo", agent="ClaudeCode", prompt=None, name="n", purpose=None,
        command=None, command_args=None, director_target="North build",
    )
    # No --machine needed: a named Director identifies its own machine.
    assert captured["_path"] == "directors/dir-north-1/sessions"


def test_a_machine_narrows_an_ambiguous_director_name(monkeypatch, captured, directors):
    """Two Directors share the name "Twin"; --machine says which one is meant."""
    monkeypatch.delenv("CC_SESSION_ID", raising=False)
    session_ops.spawn_session(
        repo="C:/repo", agent="ClaudeCode", prompt=None, name="n", purpose=None,
        command=None, command_args=None, machine="SOREN_SOUTH", director_target="Twin",
    )
    assert captured["_path"] == "directors/dir-south-2/sessions"


def test_an_ambiguous_director_name_is_refused_not_guessed(monkeypatch, captured, directors):
    monkeypatch.delenv("CC_SESSION_ID", raising=False)
    with pytest.raises(session_ops.gateway.GatewayError) as ex:
        session_ops.spawn_session(
            repo="C:/repo", agent="ClaudeCode", prompt=None, name="n", purpose=None,
            command=None, command_args=None, director_target="Twin",
        )
    assert "matches 2 Directors" in str(ex.value)
    assert "_path" not in captured  # nothing was started anywhere


def test_a_machine_alone_goes_to_the_machine_route(monkeypatch, captured):
    """No named Director: the Gateway picks one on that computer, launching one if none is running."""
    monkeypatch.delenv("CC_SESSION_ID", raising=False)
    session_ops.spawn_session(
        repo="C:/repo", agent="ClaudeCode", prompt=None, name="n", purpose=None,
        command=None, command_args=None, machine="SOREN_NORTH",
    )
    assert captured["_path"] == "machines/SOREN_NORTH/sessions"


def test_an_ordinary_spawn_lands_on_this_sessions_own_director(monkeypatch, captured):
    """"Here", named from what the session was told at launch rather than looked up.

    This is the case the loopback port used to answer for free, and it is the common one. It costs no
    round trip and cannot resolve to a neighbour: the id is the session's own.
    """
    _spawn(monkeypatch, cc_session=None)
    assert captured["_path"] == "directors/my-director/sessions"


def test_the_director_flag_reaches_spawn_session(monkeypatch, tmp_path):
    # The wiring itself: typer option -> spawn_session. Asserted through the CLI because the flag
    # name and the (deliberately differently-named) parameter are only connected in cli.py - a
    # mismatch there is invisible to every test that calls spawn_session directly.
    import src.cli as cli_module

    seen = {}
    monkeypatch.setattr(cli_module, "spawn_session", lambda *a, **k: seen.update(args=a, kwargs=k))
    result = runner.invoke(
        app, ["session", "spawn", str(tmp_path), "--name", "n", "--director", "North build"])
    assert result.exit_code == 0, result.output
    assert "North build" in seen["args"]
# --- Mission inheritance from the controlling session (issue #2387) ---
#
# A mission's shape is discovered as it runs, so attaching only at spawn grouped just the work
# somebody had already planned. The fleet ALREADY records who controls whom, so a spawned child
# inheriting its controller's mission costs nothing and would have grouped the release push that
# found this gap - a dozen sessions, none foreseeable at spawn - for free. Default ON, explicit
# --mission wins, --mission none opts out, and it is never silent.

MANAGER_ON_A_MISSION = {
    "sessionId": "sess-A",
    "name": "Release - Manager",
    "missionId": "aaaaaaaa-1111-2222-3333-444444444444",
    "missionName": "Release 1.9.4",
}


def test_spawn_inherits_the_controlling_sessions_mission(monkeypatch, captured):
    # The whole point: no --mission, but the controller is on one, so the child joins it.
    _spawn(monkeypatch, cc_session="sess-A", roster=[MANAGER_ON_A_MISSION])
    assert captured.get("missionId") == "aaaaaaaa-1111-2222-3333-444444444444"


def test_an_explicit_mission_wins_over_the_inherited_one(monkeypatch, captured):
    # Stated intent beats a default. Inheritance must never quietly override what was asked for.
    _spawn(
        monkeypatch,
        cc_session="sess-A",
        mission="bbbbbbbb-9999-8888-7777-666666666666",
        roster=[MANAGER_ON_A_MISSION],
    )
    assert captured.get("missionId") == "bbbbbbbb-9999-8888-7777-666666666666"


def test_mission_none_opts_out_of_inheritance(monkeypatch, captured):
    # The opt-out, spelled the same way --controlled-by none is: a deliberate child that is NOT
    # part of its controller's body of work.
    _spawn(monkeypatch, cc_session="sess-A", mission="none", roster=[MANAGER_ON_A_MISSION])
    assert "missionId" not in captured


def test_a_controller_on_no_mission_leaves_the_child_unattached(monkeypatch, captured):
    # Nothing to inherit is the ordinary case and must not invent an attachment.
    _spawn(
        monkeypatch,
        cc_session="sess-A",
        roster=[{"sessionId": "sess-A", "name": "Standalone seat"}],
    )
    assert "missionId" not in captured


def test_a_spawn_with_no_controller_inherits_nothing(monkeypatch, captured):
    # A human/desktop spawn has no controller, so there is no relationship to inherit along - even
    # when other sessions in the roster are on missions.
    _spawn(monkeypatch, cc_session=None, roster=[MANAGER_ON_A_MISSION])
    assert "missionId" not in captured


def test_standalone_inherits_nothing_because_it_has_no_controller(monkeypatch, captured):
    # --standalone drops the controller, and inheritance follows the controller. A deliberate peer
    # is not silently folded into the mission of the session that happened to start it.
    _spawn(monkeypatch, cc_session="sess-A", standalone=True, roster=[MANAGER_ON_A_MISSION])
    assert "missionId" not in captured


def test_inheritance_follows_an_explicit_controller_not_the_spawner(monkeypatch, captured):
    # --controlled-by names who SUPERVISES the child, and that is the relationship a mission is
    # inherited along. The spawner's own mission is deliberately NOT the answer here: the sharp
    # case is a session seating a worker under a DIFFERENT manager, where following the spawner
    # would file the worker under the wrong body of work.
    spawner = {
        "sessionId": "sess-A",
        "name": "Some other seat",
        "missionId": "cccccccc-0000-0000-0000-000000000000",
        "missionName": "Not this one",
    }
    _spawn(
        monkeypatch,
        cc_session="sess-A",
        controlled_by="sess-A-manager",
        roster=[spawner, dict(MANAGER_ON_A_MISSION, sessionId="sess-A-manager")],
    )
    assert captured.get("missionId") == "aaaaaaaa-1111-2222-3333-444444444444"


def test_inheritance_is_reported_not_silent(monkeypatch, captured, capsys, plain):
    # An attachment the caller did not ask for is only safe if they can SEE it happened. The line
    # has to name the mission and say how to undo it, or a wrong inheritance is invisible.
    # Asserted against the TEXT, not the rendering: Rich threads style codes through a version
    # number, so a plain substring check would fail wherever colour is on (issue #1082).
    _spawn(monkeypatch, cc_session="sess-A", roster=[MANAGER_ON_A_MISSION])
    out = plain(capsys.readouterr().out)
    assert "Release 1.9.4" in out
    assert "mission detach" in out


def test_an_unreadable_roster_is_reported_and_the_spawn_still_opens(
    monkeypatch, captured, capsys, plain
):
    # A roster this process cannot read is NOT the same as a controller with no mission, and must
    # not be reported as one. The session still opens - refusing to start work because an optional
    # grouping could not be looked up would be the worse failure - but the human is told why it is
    # unattached, so the missing mission is never a mystery.
    def boom():
        raise session_ops.gateway.GatewayError("Director not reachable")

    monkeypatch.setenv("CC_SESSION_ID", "sess-A")
    monkeypatch.setattr(session_ops.gateway, "get_fleet", boom)
    session_ops.spawn_session(
        repo="C:/repo",
        agent="ClaudeCode",
        prompt=None,
        name="n",
        purpose=None,
        command=None,
        command_args=None,
        controlled_by=None,
        args=None,
        standalone=False,
        role=None,
        mission=None,
    )

    assert "missionId" not in captured
    out = plain(capsys.readouterr().out)
    assert "no mission" in out
    assert "Opened" in out   # the control: the spawn itself still happened
