"""Setting up a Session Rule from the command line - the surface an agent uses.

The owner's framing, on 3 September 2026: point a coding agent at a session, tell it the problem, and
let it author the rule. That only works if these commands are discoverable and if the two properties
that keep a rule honest survive the trip:

  1. `rule add` READS THE SESSION'S SCREEN first, and sends it with the sentence. Written blind, the
     trigger words are the model's guess at what a screen says - measured against the live model,
     describing a usage-limit rule from memory produced one watching for "hit its limit" and "when it
     comes back", the person's own phrasing, which appear on no screen anywhere. It would have sat in
     the list looking correct and never fired.
  2. NOTHING HERE ARMS A RULE. Everything stores in dry run, and there is no promote command, so an
     agent cannot put a rule into a state where it types into somebody's sessions unattended.
"""

import json
import sys
from pathlib import Path

from typer.testing import CliRunner

sys.path.insert(0, str(Path(__file__).parent.parent))
sys.path.insert(0, str(Path(__file__).parent.parent.parent))

from src import rule_ops  # noqa: E402
from src.cli import app  # noqa: E402

runner = CliRunner()


def flat(output: str) -> str:
    """The output as TEXT rather than as rendering.

    Rich wraps to the console width, so a sentence that is plainly on the screen arrives with a line
    break - and therefore a double space - in the middle of it. Asserting on the rendering makes a test
    that depends on the terminal width of whoever runs it.
    """
    import re

    return re.sub(r"\s+", " ", output)


A_REAL_LIMIT_SCREEN = (
    "> keep going with the refactor\n\n"
    "Claude usage limit reached. Your limit will reset at 11:50pm.\n\n> "
)

A_DRAFTED_RULE = {
    "instruction": "when the limit hits, wait until it resets and carry on",
    "screenDescription": "The session has stopped on a usage limit notice with a reset time.",
    "triggerWords": ["Claude usage limit reached", "Your limit will reset at"],
    "checks": [],
    "scope": "all-sessions",
    "cooldownSeconds": 600,
    "dailyCap": 5,
}

A_STORED_RULE = dict(
    A_DRAFTED_RULE,
    id="11111111-1111-1111-1111-111111111111",
    state="dry_run",
    promotedBy="",
    scope={"agent": None, "repository": None, "machine": None, "mission": None},
    checks=[],
)


class FakeClient:
    """Stands in for the Gateway. Records what it was asked, so the test can assert on the REQUEST."""

    def __init__(self, draft_answer=None, screen=A_REAL_LIMIT_SCREEN):
        self.screen_text = screen
        self.draft_answer = draft_answer or {
            "readBack": "When a session stops on a limit notice I will wait and then tell it to carry on.",
            "rule": A_DRAFTED_RULE,
            "exampleScreen": A_REAL_LIMIT_SCREEN,
        }
        self.screen_calls = []
        self.draft_calls = []
        self.created = []

    def screen(self, session_id, lines=60):
        self.screen_calls.append((session_id, lines))
        return self.screen_text

    def draft(self, said, screen, session_agent="", session_machine="", all_agents=False):
        self.draft_calls.append((said, screen, session_agent, session_machine, all_agents))
        return self.draft_answer

    def create(self, rule_body):
        self.created.append(rule_body)
        return A_STORED_RULE

    def rules(self):
        return [A_STORED_RULE]

    def rule(self, rule_id):
        return A_STORED_RULE

    def firings(self, rule_id):
        return []

    def delete(self, rule_id):
        return True


def _use(monkeypatch, client):
    monkeypatch.setattr(rule_ops, "_client", lambda: client)
    monkeypatch.setattr(
        rule_ops, "_session",
        lambda target: {"id": "sess-" + target, "agent": "ClaudeCode", "machine": "SOREN_NORTH"},
    )


# ---- discovery -----------------------------------------------------------------------------------

def test_the_rule_commands_are_discoverable_by_an_agent():
    result = runner.invoke(app, ["actions", "--json"])

    assert result.exit_code == 0
    actions = {a["id"]: a for a in json.loads(result.output)["actions"]}

    assert {"rule-list", "rule-show", "rule-screen", "rule-draft", "rule-add", "rule-delete"} <= set(actions)
    # Reading is marked as reading and writing as writing, so an agent choosing an action knows which
    # of these changes anything.
    assert actions["rule-screen"]["mutatesState"] is False
    assert actions["rule-draft"]["mutatesState"] is False
    assert actions["rule-add"]["mutatesState"] is True


def test_there_is_no_promote_command_because_arming_a_rule_is_a_persons_step():
    result = runner.invoke(app, ["actions", "--json"])
    ids = {a["id"] for a in json.loads(result.output)["actions"]}

    assert "rule-promote" not in ids

    result = runner.invoke(app, ["rule", "--help"])
    assert "promote" not in result.output.lower()


# ---- the screen is read and sent -----------------------------------------------------------------

def test_add_reads_the_named_sessions_screen_and_sends_it_with_the_sentence(monkeypatch):
    client = FakeClient()
    _use(monkeypatch, client)

    result = runner.invoke(
        app, ["rule", "add", "when the limit hits, wait and carry on", "--session", "37498c19"]
    )

    assert result.exit_code == 0
    assert client.screen_calls == [("sess-37498c19", 60)]
    said, screen, agent, machine, all_agents = client.draft_calls[0]
    assert said == "when the limit hits, wait and carry on"
    # THE REAL TEXT, not a description of it. This is what lets the Gateway refuse an invented word.
    assert screen == A_REAL_LIMIT_SCREEN
    # AND WHOSE SCREEN IT IS. The Gateway scopes the rule to this agent by default - a fact we hold, not
    # something a model should guess - and tells the drafting model whose words to use.
    assert (agent, machine) == ("ClaudeCode", "SOREN_NORTH")
    assert all_agents is False


def test_add_without_a_session_sends_no_screen_and_does_not_pretend_otherwise(monkeypatch):
    client = FakeClient()
    _use(monkeypatch, client)

    result = runner.invoke(app, ["rule", "add", "when the limit hits, wait and carry on"])

    assert result.exit_code == 0
    assert client.screen_calls == []
    _, screen, agent, machine, _ = client.draft_calls[0]
    assert screen == ""
    # No session, so no agent to claim. Nothing is invented to fill the gap.
    assert (agent, machine) == ("", "")


# ---- the star: a rule for every agent --------------------------------------------------------------

def test_all_agents_is_sent_as_the_star(monkeypatch):
    """The owner's ruling: a rule written against a session is for that session's agent unless you say
    every agent. `--all-agents` is how you say it, and it has to reach the Gateway as a fact rather than
    being folded into the sentence for a model to notice."""
    client = FakeClient()
    _use(monkeypatch, client)

    result = runner.invoke(
        app, ["rule", "add", "wait and carry on", "--session", "37498c19", "--all-agents"]
    )

    assert result.exit_code == 0
    assert client.draft_calls[0][4] is True


def test_the_default_is_the_sessions_agent_not_every_agent(monkeypatch):
    client = FakeClient()
    _use(monkeypatch, client)

    runner.invoke(app, ["rule", "add", "wait and carry on", "--session", "37498c19"])

    assert client.draft_calls[0][4] is False


# ---- what it stores ------------------------------------------------------------------------------

def test_add_stores_the_drafted_rule_unchanged(monkeypatch):
    client = FakeClient()
    _use(monkeypatch, client)

    runner.invoke(app, ["rule", "add", "wait and carry on", "--session", "37498c19"])

    # POSTED BACK AS DRAFTED. If the command rebuilt the body, a scope or a check could differ from
    # the one that was checked against the screen.
    assert client.created == [A_DRAFTED_RULE]


def test_add_says_it_stored_a_dry_run_that_types_nothing(monkeypatch):
    client = FakeClient()
    _use(monkeypatch, client)

    result = runner.invoke(app, ["rule", "add", "wait and carry on", "--session", "37498c19"])

    plain = flat(result.output)
    assert "DRY RUN" in plain
    assert "types nothing" in plain


def test_draft_stores_nothing(monkeypatch):
    client = FakeClient()
    _use(monkeypatch, client)

    result = runner.invoke(app, ["rule", "draft", "wait and carry on", "--session", "37498c19"])

    assert result.exit_code == 0
    assert client.created == []
    assert "Nothing was stored" in result.output.replace("\n", " ")


# ---- a question is not a rule ---------------------------------------------------------------------

def test_a_question_back_stores_nothing_and_says_what_it_needs(monkeypatch):
    client = FakeClient(draft_answer={"question": "Which model should it switch to?"})
    _use(monkeypatch, client)

    result = runner.invoke(app, ["rule", "add", "switch models when it runs out", "--session", "37498c19"])

    # Refusing to guess is the point: storing a rule built on an unanswered question would store a
    # rule the person never described.
    assert result.exit_code == 2
    assert client.created == []
    assert "Which model should it switch to?" in result.output.replace("\n", " ")
