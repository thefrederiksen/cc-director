"""Tests for `cc-devthrottle session list`: a crashed session does not read as one that finished on purpose.

Issue #1019. The Director's roster used to hide every session in ActivityState.Exited, which is the state a
CRASHED session sits in - a crash was never modelled as its own state, it lives on the separate
Session.Crashed fact. Hiding those rows is what made the reported ghost card unremovable: `session done`
resolves its target against this listing, so a row it could not show was a row no verb could name.

Those rows are listed now. That fixes the reap, and it creates a second-order risk this file pins: the
STATUS column reads activityState, so a crashed session would print "Exited" - byte-identical to a clean
exit. That is the exact misreading issue #959 was filed for twice. So the raw crash fact is surfaced.

Rendering the fact is not ruling on it: the CLI prints what the Director put on the wire and never decides
what a state MEANS - that fold belongs to the Gateway.
"""

import sys
from pathlib import Path

import pytest

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import session_ops  # noqa: E402

CRASHED_ID = "3c09c008-fcf9-49f0-bd4e-787465654a6e"
CLEAN_ID = "11111111-2222-3333-4444-555555555555"


@pytest.fixture
def serve_fleet(monkeypatch):
    """Serve a chosen fleet roster from the Director, without any real HTTP."""

    def serve(sessions):
        monkeypatch.delenv("CC_SESSION_ID", raising=False)
        monkeypatch.setattr(session_ops.gateway, "get_json", lambda path: sessions)

    return serve


def _row(sid, name, *, activity_state, crashed):
    return {
        "sessionId": sid,
        "name": name,
        "machineName": "SOREN",
        "repoPath": r"D:\ReposFred\_demo\flask",
        "activityState": activity_state,
        "crashed": crashed,
    }


def test_crashed_session_is_marked_crashed_not_just_exited(serve_fleet, capsys):
    # The reported card: held by the Director, no process behind it, and it must not look like a clean exit.
    serve_fleet([_row(CRASHED_ID, "flask - routing audit", activity_state="Exited", crashed=True)])

    session_ops.list_sessions(json_output=False)

    # Assert on the short id and the marker, not the full name: with stdout a pipe, Rich renders the table
    # at 80 columns and elides the NAME cell, so asserting on the name would pin terminal width, not behaviour.
    out = capsys.readouterr().out
    assert "crashed" in out
    assert "3c09c008" in out


def test_cleanExit_isNotLabelledCrashed(serve_fleet, capsys):
    # The other half of the contract: a session that finished on purpose must not gain a crash marker.
    serve_fleet([_row(CLEAN_ID, "done on purpose", activity_state="Exited", crashed=False)])

    session_ops.list_sessions(json_output=False)

    out = capsys.readouterr().out
    assert "crashed" not in out
    assert "Exited" in out


def test_missing_crash_fact_is_not_treated_as_crashed(serve_fleet, capsys):
    # An older Director, or any roster row that simply carries no crash fact, must not be reported as a
    # crash. Absent is not true.
    serve_fleet([{
        "sessionId": CLEAN_ID,
        "name": "no crash fact on the wire",
        "activityState": "Working",
    }])

    session_ops.list_sessions(json_output=False)

    assert "crashed" not in capsys.readouterr().out


def test_crashed_row_is_listed_at_all_soItCanBeNamedAndReaped(serve_fleet, capsys):
    # The whole point of #1019: the id must reach the operator, because `session done <id>` is how the card
    # gets cleared and the id is what they need to type.
    serve_fleet([_row(CRASHED_ID, "flask - routing audit", activity_state="Exited", crashed=True)])

    session_ops.list_sessions(json_output=False)

    assert "3c09c008" in capsys.readouterr().out
