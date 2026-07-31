"""Unit tests for the shared Director-API helpers used by the fleet-messaging tools (issue #705)."""

import io
import json
import sys
import urllib.error
from pathlib import Path

# Make cc_shared importable when tests run from the tools/ tree.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

import pytest  # noqa: E402

from cc_shared import director  # noqa: E402


def _session(sid, name=None, machine="machine-A", number=None):
    s = {"sessionId": sid, "name": name, "machineName": machine}
    if number is not None:
        s["number"] = number
    return s


SESSIONS = [
    _session("4c810000-1111-2222-3333-444444444444", "feature-work", number=412),
    _session("9b2f0000-aaaa-bbbb-cccc-dddddddddddd", "docs", number=305),
    _session("9b2f9999-eeee-ffff-0000-111111111111", "docs-helper", number=777),
]


def test_short_id_truncates_to_eight():
    assert director.short_id("4c810000-1111") == "4c810000"
    assert director.short_id("abc") == "abc"


def test_field_tolerates_camel_and_pascal_case():
    assert director.field({"sessionId": "x"}, "sessionId", "SessionId") == "x"
    assert director.field({"SessionId": "y"}, "sessionId", "SessionId") == "y"
    assert director.field({}, "name", "Name", default="-") == "-"


def test_resolve_target_exact_full_id_wins():
    full = "9b2f0000-aaaa-bbbb-cccc-dddddddddddd"
    matches = director.resolve_target(SESSIONS, full)
    assert len(matches) == 1
    assert director.field(matches[0], "sessionId", "SessionId") == full


def test_resolve_target_unique_prefix_matches_one():
    matches = director.resolve_target(SESSIONS, "4c81")
    assert len(matches) == 1
    assert director.field(matches[0], "name", "Name") == "feature-work"


def test_resolve_target_ambiguous_prefix_returns_all_candidates():
    matches = director.resolve_target(SESSIONS, "9b2f")
    assert len(matches) == 2  # caller must refuse and list these


def test_resolve_target_by_exact_name():
    matches = director.resolve_target(SESSIONS, "docs")
    assert len(matches) == 1
    assert director.field(matches[0], "name", "Name") == "docs"


def test_resolve_target_no_match_returns_empty():
    assert director.resolve_target(SESSIONS, "zzzz") == []


# --- Issue #821: address a session by its three-digit number ---------------------------------


def test_resolve_target_by_three_digit_number():
    matches = director.resolve_target(SESSIONS, "412")
    assert len(matches) == 1
    assert director.field(matches[0], "name", "Name") == "feature-work"


def test_resolve_target_number_selects_exact_session():
    matches = director.resolve_target(SESSIONS, "305")
    assert len(matches) == 1
    assert director.field(matches[0], "sessionId", "SessionId") == "9b2f0000-aaaa-bbbb-cccc-dddddddddddd"


def test_resolve_target_unused_number_returns_empty():
    # A three-digit token no active session holds yields the standard no-match (empty) result,
    # not a crash and not a wrong-session match.
    assert director.resolve_target(SESSIONS, "999") == []


def test_resolve_target_number_takes_precedence_over_id_prefix():
    # "412" is the number of feature-work; even if it coincided with an id prefix, the number wins.
    matches = director.resolve_target(SESSIONS, "412")
    assert len(matches) == 1
    assert director.field(matches[0], "name", "Name") == "feature-work"


def test_resolve_target_three_digit_falls_back_to_id_prefix_when_no_number():
    # A three-digit token that no session holds as a number still resolves by id prefix.
    sessions = [_session("305abcde-1111-2222-3333-444444444444", "by-id-prefix", number=601)]
    matches = director.resolve_target(sessions, "305")
    assert len(matches) == 1
    assert director.field(matches[0], "name", "Name") == "by-id-prefix"


def test_resolve_target_id_prefix_unchanged_with_numbers_present():
    # Regression: id-prefix addressing still works when sessions carry numbers.
    matches = director.resolve_target(SESSIONS, "4c81")
    assert len(matches) == 1
    assert director.field(matches[0], "name", "Name") == "feature-work"


def test_resolve_target_name_unchanged_with_numbers_present():
    # Regression: exact-name addressing still works when sessions carry numbers.
    matches = director.resolve_target(SESSIONS, "docs")
    assert len(matches) == 1
    assert director.field(matches[0], "name", "Name") == "docs"


# --- Issue #1062: the Director's sentence must reach the user, not the status code -------------
#
# The refusal sentence #1050 added is written by the Director, survives HTTP intact, and was then
# discarded by this client because the extractor read only "error" while an unhandled exception
# answers in ASP.NET problem-details, which carries it in "detail". These tests pin BOTH shapes, so
# neither can be traded for the other, and the last two drive the real raise site rather than the
# helper alone - a helper that returns the right string proves nothing if nobody calls it.

# The body from the issue, verbatim: what POST /fleet/spawn actually returns for an unresolvable
# agent command. Trimmed only of newlines.
SPAWN_SENTENCE = (
    'Claude Code could not be started: the command "definitely-not-an-agent-cli-9f3a" is not a '
    "file on this machine and was not found on this Director's PATH. Set this agent's executable "
    "path in Settings, Agents - or install Claude Code - and start the session again."
)
PROBLEM_DETAILS = json.dumps({
    "type": "https://tools.ietf.org/html/rfc9110#section-15.6.1",
    "title": "An error occurred while processing your request.",
    "status": 500,
    "detail": SPAWN_SENTENCE,
})


def test_error_message_reads_problem_details_detail():
    assert director._error_message(PROBLEM_DETAILS, 500) == SPAWN_SENTENCE


def test_error_message_still_reads_the_directors_own_error_shape():
    # The two shapes coexist in this API; fixing one must not cost the other.
    body = json.dumps({"error": "toSessionId is required"})
    assert director._error_message(body, 400) == "toSessionId is required"


def test_error_message_prefers_error_over_detail_when_a_body_carries_both():
    body = json.dumps({"error": "the specific one", "detail": "the generic one"})
    assert director._error_message(body, 500) == "the specific one"


def test_error_message_accepts_pascal_case_detail():
    assert director._error_message(json.dumps({"Detail": "a sentence"}), 500) == "a sentence"


def test_error_message_shows_a_generic_title_beside_the_code_not_instead_of_it():
    # problem-details "title" says less than the status code, so promoting it would make the
    # message worse. It is additive only.
    body = json.dumps({"title": "Not Found", "status": 404})
    assert director._error_message(body, 404) == "HTTP 404 from the Director: Not Found"


@pytest.mark.parametrize("body", [
    "",
    "<html>a proxy error page</html>",
    json.dumps(["not", "an", "object"]),
    json.dumps({"status": 500}),
    json.dumps({"error": "   ", "detail": ""}),  # present but empty: not a sentence
])
def test_error_message_falls_back_to_the_status_when_the_body_carries_no_sentence(body):
    assert director._error_message(body, 500) == "HTTP 500 from the Director"


def _http_error(code: int, body: str) -> urllib.error.HTTPError:
    return urllib.error.HTTPError(
        url="http://127.0.0.1:1/fleet/spawn", code=code, msg="Internal Server Error",
        hdrs=None, fp=io.BytesIO(body.encode("utf-8")),
    )


def test_request_surfaces_the_sentence_from_a_problem_details_response(monkeypatch):
    """End to end through _request: this is the path every cc-devthrottle command takes."""
    monkeypatch.setenv("CC_DIRECTOR_API", "http://127.0.0.1:1")
    monkeypatch.setattr(director, "_token", lambda: "v1.cli..signature")
    monkeypatch.setattr(director.urllib.request, "urlopen",
                        lambda *a, **k: (_ for _ in ()).throw(_http_error(500, PROBLEM_DETAILS)))

    with pytest.raises(director.DirectorError) as caught:
        director.post_json("fleet/spawn", {"repoPath": "C:/x", "agent": "ClaudeCode"})

    assert str(caught.value) == SPAWN_SENTENCE
    assert "HTTP 500" not in str(caught.value)


def test_request_still_reports_the_status_when_the_body_carries_no_sentence(monkeypatch):
    monkeypatch.setenv("CC_DIRECTOR_API", "http://127.0.0.1:1")
    monkeypatch.setattr(director, "_token", lambda: "v1.cli..signature")
    monkeypatch.setattr(director.urllib.request, "urlopen",
                        lambda *a, **k: (_ for _ in ()).throw(_http_error(502, "")))

    with pytest.raises(director.DirectorError) as caught:
        director.get_json("fleet/sessions")

    assert str(caught.value) == "HTTP 502 from the Director"
