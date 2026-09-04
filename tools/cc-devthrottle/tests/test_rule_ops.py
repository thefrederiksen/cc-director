"""Setting up a Session Rule from the command line - the surface an agent uses.

The owner's framing, on 3 September 2026: point a coding agent at a session, tell it the problem, and
let it author the rule. That only works if these commands are discoverable and if the properties that
keep a rule honest survive the trip:

  1. `rule draft` NAMES THE SESSION and sends nothing about its screen. The Gateway reads the screen
     itself (fix round D, ruling D2); the words the rule watches for are checked against that reading,
     and a draft with no session is refused - by this command before the Gateway is even asked.
  2. `rule add` STORES THE DOCUMENT THAT WAS READ (ruling D4). It takes the proposal `rule draft` printed
     and posts exactly that, with no second model call. The old `add` drafted again, so what it stored
     could differ from what was read.
  3. A MISSING FIELD IS AN ERROR (ruling D8), never "No rules yet"; and the scope and wait words are the
     Gateway's own labels, printed verbatim - this client composes none.
  4. NOTHING HERE ARMS A RULE. Everything stores in dry run, and there is no promote command.
"""

import json
import sys
from pathlib import Path

import pytest
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

# THE EXACT TEXT THE RULE TYPES - decided when it was written, and the thing a person is agreeing to.
THE_TEXT = "carry on from where you stopped"

A_DRAFTED_RULE = {
    "instruction": "when the limit hits, wait until it resets and carry on",
    "sessionId": "sess-37498c19",
    "allAgents": False,
    "screenDescription": "The session has stopped on a usage limit notice with a reset time.",
    "textToType": THE_TEXT,
    "triggerWords": ["Claude usage limit reached", "Your limit will reset at"],
    "checks": [],
    "scope": {"agent": "ClaudeCode"},
    "cooldownSeconds": 600,
    "dailyCap": 5,
}

A_DRAFT_ANSWER = {
    "readBack": "When a session stops on a limit notice I will wait and then tell it to carry on.",
    "rule": A_DRAFTED_RULE,
    "exampleScreen": A_REAL_LIMIT_SCREEN,
    "scopeLabel": "agent ClaudeCode",
    "waitLabel": "10 minutes",
}

A_STORED_RULE = dict(
    A_DRAFTED_RULE,
    id="11111111-1111-1111-1111-111111111111",
    state="dry_run",
    promotedBy="",
    acknowledgement="",
    scope={"agent": "ClaudeCode", "repository": None, "machine": None, "mission": None},
    scopeLabel="agent ClaudeCode",
    waitLabel="10 minutes",
    checks=[],
    createdUtc="2026-09-03T09:00:00Z",
    updatedUtc="2026-09-03T09:00:00Z",
)


class FakeClient:
    """Stands in for the Gateway. Records what it was asked, so the test can assert on the REQUEST."""

    def __init__(self, draft_answer=None, rules=None):
        self.draft_answer = draft_answer or A_DRAFT_ANSWER
        self.served_rules = [A_STORED_RULE] if rules is None else rules
        self.screen_calls = []
        self.draft_calls = []
        self.created = []

    def screen(self, session_id, lines=60):
        self.screen_calls.append((session_id, lines))
        return A_REAL_LIMIT_SCREEN

    def draft(self, said, session_id, all_agents=False):
        self.draft_calls.append((said, session_id, all_agents))
        return self.draft_answer

    def create(self, rule_body):
        self.created.append(rule_body)
        return A_STORED_RULE

    def rules(self):
        return self.served_rules

    def rule(self, rule_id):
        return self.served_rules[0]

    def firings(self, rule_id):
        return []

    def delete(self, rule_id):
        return True


def _use(monkeypatch, client):
    monkeypatch.setattr(rule_ops, "_client", lambda: client)
    monkeypatch.setattr(rule_ops, "_session_id", lambda target: "sess-" + target)


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
    # And the session is REQUIRED for a draft - an agent reading the registry must not believe it can
    # write a rule without one.
    assert {"name": "session", "required": True} in actions["rule-draft"]["args"]


def test_there_is_no_promote_command_because_arming_a_rule_is_a_persons_step():
    result = runner.invoke(app, ["actions", "--json"])
    ids = {a["id"] for a in json.loads(result.output)["actions"]}

    assert "rule-promote" not in ids

    result = runner.invoke(app, ["rule", "--help"])
    assert "promote" not in result.output.lower()


# ---- fix round D, ruling D2: the session is named; the screen is never sent --------------------------

def test_draft_names_the_session_and_sends_nothing_about_its_screen(monkeypatch):
    client = FakeClient()
    _use(monkeypatch, client)

    result = runner.invoke(
        app, ["rule", "draft", "when the limit hits, wait and carry on", "--session", "37498c19"]
    )

    assert result.exit_code == 0, result.output
    # THE SESSION ID, and only that. The Gateway reads the screen itself and holds the agent and the
    # machine; this client makes no claim about any of them.
    assert client.draft_calls == [("when the limit hits, wait and carry on", "sess-37498c19", False)]
    assert client.screen_calls == []


def test_draft_without_a_session_is_refused_before_the_gateway_is_asked(monkeypatch):
    client = FakeClient()
    _use(monkeypatch, client)

    result = runner.invoke(app, ["rule", "draft", "when the limit hits, wait and carry on"])

    # A usage error from the command itself: --session is required, and nothing was drafted.
    assert result.exit_code != 0
    assert client.draft_calls == []
    assert "--session" in result.output


def test_all_agents_is_sent_as_the_star(monkeypatch):
    """The owner's ruling: a rule written against a session is for that session's agent unless you say
    every agent. `--all-agents` is how you say it, and it has to reach the Gateway as a fact rather than
    being folded into the sentence for a model to notice."""
    client = FakeClient()
    _use(monkeypatch, client)

    result = runner.invoke(
        app, ["rule", "draft", "wait and carry on", "--session", "37498c19", "--all-agents"]
    )

    assert result.exit_code == 0, result.output
    assert client.draft_calls[0][2] is True


def test_the_default_is_the_sessions_agent_not_every_agent(monkeypatch):
    client = FakeClient()
    _use(monkeypatch, client)

    runner.invoke(app, ["rule", "draft", "wait and carry on", "--session", "37498c19"])

    assert client.draft_calls[0][2] is False


def test_draft_stores_nothing_and_prints_the_gateways_labels(monkeypatch):
    client = FakeClient()
    _use(monkeypatch, client)

    result = runner.invoke(app, ["rule", "draft", "wait and carry on", "--session", "37498c19"])

    assert result.exit_code == 0, result.output
    assert client.created == []
    plain = flat(result.output)
    assert "Nothing was stored" in plain
    # THE GATEWAY'S WORDS, verbatim - this client composes no scope or wait of its own.
    assert "agent ClaudeCode" in plain
    assert "10 minutes apart" in plain


# ---- phase 1: the exact text it types is shown, verbatim --------------------------------------------

def test_draft_prints_the_exact_text_it_types_under_the_read_back(monkeypatch):
    """The keystroke is the most consequential thing a rule does and the read-back is what a person
    confirms. A read-back that describes the situation but hides the keystroke asks somebody to approve
    an action they were not shown - so the text is printed verbatim, right under the read-back, before
    anything else about the rule."""
    client = FakeClient()
    _use(monkeypatch, client)

    result = runner.invoke(app, ["rule", "draft", "wait and carry on", "--session", "37498c19"])

    assert result.exit_code == 0, result.output
    plain = flat(result.output)
    assert f"types {THE_TEXT}" in plain
    assert plain.index(A_DRAFT_ANSWER["readBack"]) < plain.index(f"types {THE_TEXT}") < plain.index("acts on")


def test_list_prints_the_exact_text_a_rule_types_verbatim(monkeypatch):
    served = dict(A_STORED_RULE, textToType="the exact text the Gateway served")
    client = FakeClient(rules=[served])
    _use(monkeypatch, client)

    result = runner.invoke(app, ["rule", "list"])

    assert result.exit_code == 0, result.output
    assert "types the exact text the Gateway served" in flat(result.output)


def test_a_rule_served_with_no_text_to_type_is_said_to_need_re_authoring(monkeypatch):
    """A rule stored before rules carried their text has nothing to type; the Gateway refuses to fire
    it and refuses to promote it. Printed like every other rule it would hide exactly that."""
    client = FakeClient(rules=[dict(A_STORED_RULE, textToType="")])
    _use(monkeypatch, client)

    result = runner.invoke(app, ["rule", "list"])

    assert result.exit_code == 0, result.output
    assert "needs re-authoring" in flat(result.output)


# ---- fix round D, ruling D4: `rule add` stores the document that was read -------------------------

def test_add_takes_the_reviewed_proposal_and_makes_no_authoring_call(monkeypatch, tmp_path):
    """The contract is sentence, read-back, confirmation, store. The old `rule add` drafted, stored and
    only then printed the read-back - and running `rule draft` first did not help, because `add` drafted
    AGAIN, so the document stored could differ from the one that was read. `add` now takes the proposal
    `draft --json` printed, posts exactly that rule to the create route, and never asks a model."""
    client = FakeClient()
    _use(monkeypatch, client)
    proposal = tmp_path / "proposal.json"
    proposal.write_text(json.dumps(client.draft_answer), encoding="utf-8")

    result = runner.invoke(app, ["rule", "add", str(proposal)])

    assert result.exit_code == 0, result.output
    # NO AUTHORING CALL. What is stored is what was read, or nothing.
    assert client.draft_calls == []
    assert client.screen_calls == []
    assert client.created == [A_DRAFTED_RULE]


def test_what_draft_prints_is_what_add_stores(monkeypatch, tmp_path):
    """The round trip an agent actually runs: draft --json to a file, add the file. The body that reaches
    the create route is byte-for-byte the rule the draft printed."""
    client = FakeClient()
    _use(monkeypatch, client)

    drafted = runner.invoke(app, ["rule", "draft", "wait and carry on", "--session", "37498c19", "--json"])
    assert drafted.exit_code == 0, drafted.output
    proposal = tmp_path / "proposal.json"
    proposal.write_text(drafted.output, encoding="utf-8")

    added = runner.invoke(app, ["rule", "add", str(proposal)])

    assert added.exit_code == 0, added.output
    assert client.created == [json.loads(drafted.output)["rule"]]
    assert client.draft_calls == [("wait and carry on", "sess-37498c19", False)]


def test_add_accepts_a_bare_rule_body_too(monkeypatch, tmp_path):
    client = FakeClient()
    _use(monkeypatch, client)
    proposal = tmp_path / "rule.json"
    proposal.write_text(json.dumps(A_DRAFTED_RULE), encoding="utf-8")

    result = runner.invoke(app, ["rule", "add", str(proposal)])

    assert result.exit_code == 0, result.output
    assert client.created == [A_DRAFTED_RULE]


def test_add_refuses_a_file_that_holds_a_question_and_stores_nothing(monkeypatch, tmp_path):
    client = FakeClient()
    _use(monkeypatch, client)
    proposal = tmp_path / "question.json"
    proposal.write_text(json.dumps({"question": "Which model should it switch to?"}), encoding="utf-8")

    result = runner.invoke(app, ["rule", "add", str(proposal)])

    # Refusing to guess is the point: storing a rule built on an unanswered question would store a
    # rule the person never described.
    assert result.exit_code == 1
    assert client.created == []
    assert "Which model should it switch to?" in flat(result.output)


def test_add_refuses_a_file_with_no_rule_in_it(monkeypatch, tmp_path):
    client = FakeClient()
    _use(monkeypatch, client)
    proposal = tmp_path / "empty.json"
    proposal.write_text(json.dumps({"readBack": "something", "exampleScreen": ""}), encoding="utf-8")

    result = runner.invoke(app, ["rule", "add", str(proposal)])

    assert result.exit_code == 1
    assert client.created == []
    assert "no rule to store" in flat(result.output)


def test_add_says_it_stored_a_dry_run_that_types_nothing(monkeypatch, tmp_path):
    client = FakeClient()
    _use(monkeypatch, client)
    proposal = tmp_path / "proposal.json"
    proposal.write_text(json.dumps(A_DRAFT_ANSWER), encoding="utf-8")

    result = runner.invoke(app, ["rule", "add", str(proposal)])

    plain = flat(result.output)
    assert "DRY RUN" in plain
    assert "types nothing" in plain


# ---- a question is not a rule ---------------------------------------------------------------------

def test_a_question_back_from_draft_stores_nothing_and_says_what_it_needs(monkeypatch):
    client = FakeClient(draft_answer={"question": "Which model should it switch to?"})
    _use(monkeypatch, client)

    result = runner.invoke(app, ["rule", "draft", "switch models when it runs out", "--session", "37498c19"])

    assert result.exit_code == 0
    assert client.created == []
    assert "Which model should it switch to?" in flat(result.output)


# ---- fix round D, ruling D8: the Gateway's labels, and a missing field is a broken instrument ------

def test_list_prints_the_gateways_labels_verbatim_and_composes_none(monkeypatch):
    served = dict(A_STORED_RULE, scopeLabel="the label the Gateway stamped", waitLabel="the wait the Gateway stamped")
    client = FakeClient(rules=[served])
    _use(monkeypatch, client)

    result = runner.invoke(app, ["rule", "list"])

    plain = flat(result.output)
    assert result.exit_code == 0, result.output
    assert "the label the Gateway stamped" in plain
    assert "the wait the Gateway stamped apart" in plain
    assert "every session" not in plain


def test_a_rule_served_without_its_labels_is_an_error_not_a_guess(monkeypatch):
    without = {k: v for k, v in A_STORED_RULE.items() if k not in ("scopeLabel", "waitLabel")}
    client = FakeClient(rules=[without])
    _use(monkeypatch, client)

    result = runner.invoke(app, ["rule", "list"])

    assert result.exit_code == 1
    assert "scopeLabel" in flat(result.output)


class _Answer:
    """A Gateway answer with the given JSON body and a JSON content type."""

    ok = True
    status_code = 200
    headers = {"Content-Type": "application/json"}
    text = "{}"

    def __init__(self, body):
        self._body = body

    def json(self):
        return self._body


def _client_answering(monkeypatch, body):
    monkeypatch.setattr(rule_ops.gateway, "gateway_base_url", lambda: "http://gateway.test")
    monkeypatch.setattr(rule_ops.gateway, "session_key", lambda: "key")
    client = rule_ops.RuleClient()
    monkeypatch.setattr(client, "_request", lambda method, path, json_body=None: _Answer(body))
    return client


def test_a_rules_answer_with_no_rules_field_is_an_error_not_an_empty_list(monkeypatch):
    with pytest.raises(rule_ops.GatewayError, match="rules"):
        _client_answering(monkeypatch, {}).rules()


def test_a_rule_answer_with_no_rule_field_is_an_error_not_an_empty_rule(monkeypatch):
    with pytest.raises(rule_ops.GatewayError, match="rule"):
        _client_answering(monkeypatch, {}).rule("11111111-1111-1111-1111-111111111111")


def test_a_firings_answer_with_no_firings_field_is_an_error_not_an_empty_history(monkeypatch):
    with pytest.raises(rule_ops.GatewayError, match="firings"):
        _client_answering(monkeypatch, {}).firings("11111111-1111-1111-1111-111111111111")


def test_an_answer_carrying_the_field_is_read_as_what_it_carries(monkeypatch):
    assert _client_answering(monkeypatch, {"firings": []}).firings("x") == []


def test_the_draft_request_carries_the_session_id_and_the_star_and_no_screen(monkeypatch):
    """The wire, not the fake: the body that leaves this client names the session and the star and
    carries no screen, agent or machine - those are the Gateway's facts."""
    sent = {}

    class _Recording:
        ok = True
        status_code = 200
        headers = {"Content-Type": "application/json"}
        text = "{}"

        def json(self):
            return A_DRAFT_ANSWER

    monkeypatch.setattr(rule_ops.gateway, "gateway_base_url", lambda: "http://gateway.test")
    monkeypatch.setattr(rule_ops.gateway, "session_key", lambda: "key")
    client = rule_ops.RuleClient()

    def _request(method, path, json_body=None):
        sent.update({"method": method, "path": path, "body": json_body})
        return _Recording()

    monkeypatch.setattr(client, "_request", _request)

    client.draft("wait and carry on", "sess-1", True)

    assert sent["method"] == "POST"
    assert sent["path"] == "/gateway/rules/draft"
    assert sent["body"] == {
        "turns": [{"who": "person", "text": "wait and carry on"}],
        "sessionId": "sess-1",
        "allAgents": True,
    }
    assert "screen" not in sent["body"]
    assert "sessionAgent" not in sent["body"]


# ---- fix round E, ruling E2: a present field of the wrong shape is as broken as a missing one --------

A_FIRING = {
    "id": "f1",
    "ruleId": A_STORED_RULE["id"],
    "sessionId": "abc123",
    "occurredUtc": "2026-09-03T09:30:00Z",
    "screenText": "API Error",
    "understanding": "a provider error",
    "decision": "act",
    "reason": "the screen shows the provider's own error.",
    "checksRun": [],
    "typedText": "",
    "outcome": "dry run: nothing was typed.",
    "grounding": "grounding: the quoted words are on the screen.",
}


def test_rules_null_is_an_error_not_an_empty_list(monkeypatch):
    with pytest.raises(rule_ops.GatewayError, match="rules"):
        _client_answering(monkeypatch, {"rules": None}).rules()


def test_rules_of_the_wrong_type_is_an_error(monkeypatch):
    with pytest.raises(rule_ops.GatewayError, match="rules"):
        _client_answering(monkeypatch, {"rules": "none"}).rules()


def test_a_rule_record_missing_a_required_field_is_an_error_naming_the_field(monkeypatch):
    broken = {k: v for k, v in A_STORED_RULE.items() if k != "triggerWords"}
    with pytest.raises(rule_ops.GatewayError, match="triggerWords"):
        _client_answering(monkeypatch, {"rules": [broken]}).rules()


def test_a_valid_non_empty_rules_list_is_read_as_what_it_carries(monkeypatch):
    assert _client_answering(monkeypatch, {"rules": [A_STORED_RULE]}).rules() == [A_STORED_RULE]


def test_firings_null_is_an_error_not_an_empty_history(monkeypatch):
    with pytest.raises(rule_ops.GatewayError, match="firings"):
        _client_answering(monkeypatch, {"firings": None}).firings("x")


def test_firings_of_the_wrong_type_is_an_error(monkeypatch):
    with pytest.raises(rule_ops.GatewayError, match="firings"):
        _client_answering(monkeypatch, {"firings": {"count": 0}}).firings("x")


def test_a_firing_whose_decision_is_not_a_string_is_an_error_naming_the_field(monkeypatch):
    with pytest.raises(rule_ops.GatewayError, match="decision"):
        _client_answering(monkeypatch, {"firings": [dict(A_FIRING, decision=7)]}).firings("x")


def test_a_valid_non_empty_history_is_read_as_what_it_carries(monkeypatch):
    assert _client_answering(monkeypatch, {"firings": [A_FIRING]}).firings("x") == [A_FIRING]


def test_a_deleted_flag_that_is_not_a_boolean_is_an_error_not_a_client_authored_outcome(monkeypatch):
    with pytest.raises(rule_ops.GatewayError, match="deleted"):
        _client_answering(monkeypatch, {"deleted": "yes"}).delete("x")


def test_a_rule_that_is_not_an_object_is_an_error(monkeypatch):
    with pytest.raises(rule_ops.GatewayError, match="rule"):
        _client_answering(monkeypatch, {"rule": "stored"}).rule("x")


# ---- fix round F, ruling F2: a required child that is absent is a broken answer -----------------------
# The served contract requires all four scope children and the Gateway projects all four. This reader
# checked a scope part's type ONLY when the key existed, so a response omitting scope.agent was accepted
# whole. An absent required child is a broken instrument, never a value - and never one this command
# fills in for itself.


def test_a_rule_whose_scope_is_missing_a_required_child_is_an_error_naming_it(monkeypatch):
    broken = dict(A_STORED_RULE, scope={k: v for k, v in A_STORED_RULE["scope"].items() if k != "agent"})
    with pytest.raises(rule_ops.GatewayError, match="scope.agent"):
        _client_answering(monkeypatch, {"rules": [broken]}).rules()


def test_a_scope_child_of_the_wrong_type_is_an_error_naming_it(monkeypatch):
    broken = dict(A_STORED_RULE, scope=dict(A_STORED_RULE["scope"], mission=7))
    with pytest.raises(rule_ops.GatewayError, match="scope.mission"):
        _client_answering(monkeypatch, {"rules": [broken]}).rules()


def test_a_scope_with_all_four_children_is_read_as_what_it_carries(monkeypatch):
    served = _client_answering(monkeypatch, {"rules": [A_STORED_RULE]}).rules()
    assert served[0]["scope"] == A_STORED_RULE["scope"]


def test_list_does_not_print_a_zero_or_none_for_a_field_the_gateway_did_not_send(monkeypatch):
    """The renderer used to supply '', '(none)' and 0 for missing fields. A field the Gateway did not send
    is a broken answer, not a zero - the listing errors and names the field."""
    broken = {k: v for k, v in A_STORED_RULE.items() if k != "dailyCap"}
    client = FakeClient(rules=[broken])
    _use(monkeypatch, client)

    result = runner.invoke(app, ["rule", "list"])

    assert result.exit_code == 1
    assert "dailyCap" in flat(result.output)
    assert " 0 a day" not in flat(result.output)


def test_show_does_not_print_blanks_for_a_firing_the_gateway_sent_broken(monkeypatch):
    client = FakeClient()
    client.firings = lambda rule_id: [{k: v for k, v in A_FIRING.items() if k != "reason"}]
    _use(monkeypatch, client)

    result = runner.invoke(app, ["rule", "show", A_STORED_RULE["id"]])

    assert result.exit_code == 1
    assert "reason" in flat(result.output)
