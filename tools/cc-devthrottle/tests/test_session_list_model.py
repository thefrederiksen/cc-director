"""Tests for the MODEL column of `cc-devthrottle session list` (issue devthrottle_internal#1340).

The column exists because an agent driving this fleet had to parse `--json` to learn which model a session
was running, and a human reading the same table could not learn it at all - while it is the single fact
that drives both the cost and the quality of every session on the list.

What these pin is not "a model appears". It is that the table RENDERS the Gateway's fold and never rules:
the full recorded id when there is one, and two DIFFERENT sentences for the two absences, which mean
opposite things ("the first turn has not finished" against "this agent can never report one"). Printed the
same, they would leave a reader waiting for a value that is never coming.
"""

import sys
from pathlib import Path

import pytest
from rich.console import Console

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import session_ops  # noqa: E402

SID = "7a1b2c3d-0000-0000-0000-000000000001"


@pytest.fixture
def serve_fleet(monkeypatch):
    """Serve a chosen fleet roster from the Director, without any real HTTP.

    The console is widened for the duration: with stdout a pipe, Rich lays the table out at 80 columns and
    elides cells, so a narrow console would make these tests assert on terminal width rather than on what
    the column contains.
    """

    def serve(sessions):
        monkeypatch.delenv("CC_SESSION_ID", raising=False)
        monkeypatch.setattr(session_ops.director, "get_json", lambda path: sessions)
        monkeypatch.setattr(session_ops, "console", Console(width=200))

    return serve


def _row(**extra):
    row = {
        "sessionId": SID,
        "name": "a session",
        "machineName": "SOREN",
        "repoPath": r"D:\ReposFred\devthrottle",
        "agent": "ClaudeCode",
        "activityState": "Working",
    }
    row.update(extra)
    return row


def _listing(capsys):
    session_ops.list_sessions(json_output=False)
    return capsys.readouterr().out


def test_recorded_model_prints_the_full_id_not_the_shortened_badge(serve_fleet, capsys):
    # The rail shortens "claude-fable-5" to "fable-5" because a rail is narrow. A table is not, and a
    # truncated id is not a name anything else will match - so the column prints what the records spell.
    serve_fleet([_row(modelDisplay={"kind": "reported", "text": "fable-5", "modelId": "claude-fable-5"})])

    out = _listing(capsys)

    assert "claude-fable-5" in out
    assert "MODEL" in out


def test_not_recorded_yet_says_so_in_the_gateways_words(serve_fleet, capsys):
    # No model id, but a verdict: this session CAN report one and simply has not finished a turn.
    serve_fleet([_row(modelDisplay={"kind": "notRecordedYet", "text": "no model yet", "modelId": None})])

    assert "no model yet" in _listing(capsys)


def test_the_two_absences_do_not_print_the_same_string(serve_fleet, capsys):
    # The whole issue in one test.
    serve_fleet([_row(modelDisplay={"kind": "notReported", "text": "model not reported", "modelId": None})])
    never = _listing(capsys)

    serve_fleet([_row(modelDisplay={"kind": "notRecordedYet", "text": "no model yet", "modelId": None})])
    not_yet = _listing(capsys)

    assert "model not reported" in never
    assert "no model yet" in not_yet
    assert never != not_yet


def test_unfolded_row_falls_back_to_the_raw_recorded_model(serve_fleet, capsys):
    # A Gateway too old to stamp the fold still puts the raw model on the wire. Printing it is not ruling -
    # it is the same fact, unfolded.
    serve_fleet([_row(currentModel="gpt-5.6-sol")])

    assert "gpt-5.6-sol" in _listing(capsys)


def test_no_model_and_no_fold_reads_as_unknown_not_as_either_absence(serve_fleet, capsys):
    # A third case, and it must not borrow the fold's words: an old Gateway told us nothing, which is not
    # the same as being told there is no model yet, nor that there never will be one. An empty cell would
    # have quietly claimed one of those.
    serve_fleet([_row()])

    out = _listing(capsys)
    assert "(unknown)" in out
    assert "no model yet" not in out


def test_a_crashed_status_still_fits_beside_the_new_column(serve_fleet, capsys):
    # The cost of a new column is paid by the ones already there. Rich lays this table out at 80 columns
    # when stdout is a pipe, and a second new column pushed STATUS far enough to elide "(crashed)" - a fact
    # issue #1019 exists to keep readable. This pins the budget rather than trusting it.
    serve_fleet(
        [
            _row(
                activityState="Exited",
                crashed=True,
                modelDisplay={"kind": "reported", "text": "opus-5", "modelId": "claude-opus-5"},
            )
        ]
    )
    session_ops.list_sessions(json_output=False)
    wide = capsys.readouterr().out
    assert "crashed" in wide

    # And at the real 80-column width an agent actually reads it at.
    session_ops.console = Console(width=80)
    session_ops.list_sessions(json_output=False)
    assert "crashed" in capsys.readouterr().out
