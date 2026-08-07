"""Tests for `cc-devthrottle dictionary add` (issue #2484).

The owner ruled that an agent may add words to the dictation dictionary with NO confirmation step,
and that the grant is ADD ONLY. These cases pin the decisions that would otherwise be reversed by a
later well-meaning edit:

  * add sends exactly `terms`, and never `mistranscriptions` - the Gateway refuses a session key on
    the wrong-spellings list, and a command that sent one would fail every call with a 403;
  * `remove` and `set` are NOT verbs on this command, so the tool never offers something the
    Gateway will refuse;
  * a Gateway refusal reaches the reader as the Gateway's own sentence, not a traceback;
  * a term the answer does not contain is reported as a failure, not as a success.

No HTTP happens: the Gateway post is stubbed.
"""

import sys
from pathlib import Path

import pytest
import typer

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from typer.testing import CliRunner  # noqa: E402

from src import dictionary_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()


@pytest.fixture
def posted(monkeypatch):
    """Capture what the command sends, and answer with a glossary containing every term sent."""
    calls = []

    def fake_post(path, body=None, timeout=30):
        calls.append({"path": path, "body": body})
        terms = (body or {}).get("terms") or []
        return {"vocabulary": list(terms), "commonMistranscriptions": {}, "profiles": {}}

    monkeypatch.setattr(dictionary_ops.gateway, "post_json", fake_post)
    return calls


def test_add_sends_the_terms_to_the_term_endpoint(posted):
    result = runner.invoke(app, ["dictionary", "add", "Kubernetes"])

    assert result.exit_code == 0
    assert posted == [{"path": "/ingest/dictionary/terms", "body": {"terms": ["Kubernetes"]}}]


def test_add_sends_several_terms_in_one_call(posted):
    result = runner.invoke(app, ["dictionary", "add", "Kubernetes", "Helm", "mindzie"])

    assert result.exit_code == 0
    assert posted[0]["body"] == {"terms": ["Kubernetes", "Helm", "mindzie"]}


def test_add_never_sends_wrong_spellings(posted):
    """The narrowing that makes the grant safe. A session key may add a term and may NOT touch the
    wrong-spellings list, so this command must never put one in the body - if it did, every call
    would come back 403 and the feature would be dead on arrival."""
    result = runner.invoke(app, ["dictionary", "add", "Kubernetes"])

    assert result.exit_code == 0
    assert "mistranscriptions" not in posted[0]["body"]


def test_there_is_no_remove_or_set_verb():
    """ADD ONLY, said by the command surface itself. Offering a verb the Gateway refuses would read
    as a broken tool rather than as the line the owner drew."""
    for verb in ("remove", "delete", "set", "replace", "list"):
        result = runner.invoke(app, ["dictionary", verb, "Kubernetes"])
        assert result.exit_code != 0, f"'dictionary {verb}' must not be a command"


# ---------- the read half: tracing and sweeping a bad entry ----------


TRAIL = [
    {"addedAtUtc": "2026-08-07T10:02:00Z", "term": "Kuberentes",
     "sessionId": "bbbbbbbb-2222-3333-4444-555555555555", "directorId": "director-b"},
    {"addedAtUtc": "2026-08-07T10:01:00Z", "term": "Kubernetees",
     "sessionId": "bbbbbbbb-2222-3333-4444-555555555555", "directorId": "director-b"},
    {"addedAtUtc": "2026-08-07T10:00:00Z", "term": "Kubernetes",
     "sessionId": "aaaaaaaa-1111-2222-3333-444444444444", "directorId": "director-a"},
]


@pytest.fixture
def trail(monkeypatch):
    """Answer the additions read with a trail holding one good term and one session's bad batch."""
    calls = []

    def fake_get(path, timeout=30):
        calls.append(path)
        return {"additions": list(TRAIL), "count": len(TRAIL)}

    monkeypatch.setattr(dictionary_ops.gateway, "get_json", fake_get)
    return calls


def test_additions_reads_the_trail_endpoint(trail, plain):
    result = runner.invoke(app, ["dictionary", "additions"])

    assert result.exit_code == 0
    assert trail == ["/ingest/dictionary/additions"]
    text = plain(result.output)
    assert "Kubernetes" in text
    assert "Kuberentes" in text


def test_additions_filters_to_one_session_by_id_prefix(trail, plain):
    """The sweep. Somebody chasing a bad entry has the short id from the roster, so a prefix has to
    work - and filtering must leave the OTHER session's good term out."""
    result = runner.invoke(app, ["dictionary", "additions", "--session", "bbbbbbbb"])

    assert result.exit_code == 0
    text = plain(result.output)
    assert "Kuberentes" in text
    assert "Kubernetees" in text
    assert "Kubernetes " not in text.replace("Kubernetees", "").replace("Kuberentes", "")


def test_additions_says_when_a_filter_matched_nothing(trail, plain):
    """'No agent has added anything' and 'your filter matched nothing' are different facts, and a
    reader chasing a bad entry needs to know which one they are looking at."""
    result = runner.invoke(app, ["dictionary", "additions", "--session", "ffffffff"])

    assert result.exit_code == 0
    assert "matching" in plain(result.output)


def test_additions_says_when_no_agent_has_added_anything(monkeypatch, plain):
    monkeypatch.setattr(dictionary_ops.gateway, "get_json",
                        lambda path, timeout=30: {"additions": [], "count": 0})

    result = runner.invoke(app, ["dictionary", "additions"])

    assert result.exit_code == 0
    assert "No agent has added a term" in plain(result.output)


def test_additions_tells_the_reader_it_cannot_remove(trail, plain):
    """Finding is not acting, said where the person reading the output will see it - otherwise the
    obvious next question ('so how do I delete it?') has no answer on screen."""
    result = runner.invoke(app, ["dictionary", "additions"])

    assert "Cockpit" in plain(result.output)


def test_additions_reports_a_gateway_refusal_as_its_sentence(monkeypatch):
    def refuse(path, timeout=30):
        raise dictionary_ops.GatewayError("the dictation dictionary is ADD ONLY for an agent")

    monkeypatch.setattr(dictionary_ops.gateway, "get_json", refuse)

    result = runner.invoke(app, ["dictionary", "additions"])

    assert result.exit_code == 1
    assert "ADD ONLY" in result.output


def test_the_help_says_add_only_and_warns_about_spelling(plain):
    result = runner.invoke(app, ["dictionary", "add", "--help"])

    text = plain(result.output).lower()
    assert "add only" in text
    assert "written down" in text


def test_a_gateway_refusal_is_reported_as_its_own_sentence(monkeypatch):
    """A 403 from the guard carries a sentence written for the agent that hit it. It must arrive
    intact - a traceback here would send the reader hunting a credential problem."""
    def refuse(path, body=None, timeout=30):
        raise dictionary_ops.GatewayError(
            "a session key may not call PUT /ingest/dictionary; the dictation dictionary is ADD ONLY"
        )

    monkeypatch.setattr(dictionary_ops.gateway, "post_json", refuse)

    result = runner.invoke(app, ["dictionary", "add", "Kubernetes"])

    assert result.exit_code == 1
    assert "ADD ONLY" in result.output


def test_a_term_missing_from_the_answer_is_a_failure(monkeypatch):
    """A 200 is not proof the word went in. Reporting success from the status code alone is the kind
    of false green that is only discovered by dictating the word and hearing it come out wrong."""
    def answer_without_it(path, body=None, timeout=30):
        return {"vocabulary": ["something else"], "commonMistranscriptions": {}, "profiles": {}}

    monkeypatch.setattr(dictionary_ops.gateway, "post_json", answer_without_it)

    result = runner.invoke(app, ["dictionary", "add", "Kubernetes"])

    assert result.exit_code == 1
    assert "Kubernetes" in result.output


def test_an_empty_term_is_refused_before_any_call(posted):
    result = runner.invoke(app, ["dictionary", "add", "   "])

    assert result.exit_code == 1
    assert posted == []


def test_a_pasted_block_of_prose_is_refused_before_any_call(posted):
    """One word or phrase per term. A multi-line paste is prose that landed in the wrong argument,
    and adding it would put a paragraph into the glossary as though it were a word."""
    result = runner.invoke(app, ["dictionary", "add", "Kubernetes\nis a container orchestrator"])

    assert result.exit_code == 1
    assert posted == []


def test_terms_are_trimmed(posted):
    result = runner.invoke(app, ["dictionary", "add", "  Kubernetes  "])

    assert result.exit_code == 0
    assert posted[0]["body"] == {"terms": ["Kubernetes"]}


def test_validate_terms_rejects_an_empty_list():
    with pytest.raises(ValueError):
        dictionary_ops.validate_terms([])


def test_the_action_is_discoverable():
    """An agent finds this through `cc-devthrottle actions`, which is the front door the fleet
    preamble points at. A capability nothing lists is a capability nobody uses."""
    import json

    result = runner.invoke(app, ["actions", "--json"])

    assert result.exit_code == 0
    actions = {a["id"]: a for a in json.loads(result.output)["actions"]}
    assert "dictionary-add" in actions
    assert "dictionary-additions" in actions
    assert actions["dictionary-add"]["mutatesState"] is True
    assert actions["dictionary-additions"]["mutatesState"] is False
    # The spelling instruction travels with the action, because the actions list is read by agents
    # that never open the skill.
    assert "written down" in actions["dictionary-add"]["description"].lower()
