"""Session Rules from the command line - the surface an AGENT uses to set a rule up.

A rule is a standing instruction about your sessions: it sits there costing nothing until one of them
goes idle with something on its screen that looks like the thing you described, and then an agent reads
that screen and your instruction together and does what you asked.

WHY THIS EXISTS AT ALL, when the Cockpit has a page for it. The owner's own framing, on 3 September
2026: point a coding agent at a session, say "here is the problem, set up a rule for when this happens",
and let a genuinely capable agent do the authoring. That is only possible if the whole surface is
reachable from a command line. It also means the built-in page never has to be the clever one - the
clever one is whatever agent you point at this.

THE SCREEN IS READ BY THE GATEWAY, NEVER SENT FROM HERE (fix round D, ruling D2). `rule draft` names the
session; the Gateway reads that session's screen itself, takes the agent and the machine from its own
roster, and REFUSES any trigger word that is not on the screen. There is no way to draft without a
session, because that was the path on which the words were the model's guess at what a screen says -
measured on 3 September, describing a usage-limit rule from memory produced one watching for "hit its
limit" and "when it comes back", which are the person's own phrasing and appear on no screen anywhere.

THE CONTRACT IS SENTENCE, READ-BACK, CONFIRMATION, STORE - AND THIS COMMAND GROUP CANNOT COLLAPSE IT
(fix round D, ruling D4). `rule draft` asks the model and prints the proposal; `rule add` takes THAT
proposal and posts it to the create route, making NO authoring call of its own. The old `rule add`
drafted, stored, and only then printed the read-back, so what was stored could differ from what was
read. Now what is stored is the document that was read, or nothing - and the Gateway reads the session's
screen again at the write gate, so a hand-edited proposal cannot smuggle an ungrounded word past it.

THIS CLIENT DECIDES NOTHING (repository rule 7, and fix round D, ruling D8). The words a person reads for
a rule's scope and its wait are stamped onto the rule by the Gateway as `scopeLabel` and `waitLabel`,
and this prints them verbatim. And an answer that is missing the field this client asked for is an
ERROR, never an empty list: "No rules yet" printed over a broken answer is an absence-shaped check
reporting a positive fact when the data never arrived.

AND IT READS NOTHING IT HAS NOT CHECKED THE SHAPE OF (fix round E, ruling E2). A present field of the
wrong shape - `{"rules": null}`, a rule with no trigger words, a firing whose decision is a number - is
as broken as a missing one, and used to be printed as a clean empty state. Every answer is validated at
runtime, the container and every required field inside every record, and nothing here supplies an empty
string, "(none)" or 0 for a field the Gateway did not send: a field it did not send is a broken answer,
not a zero.

WHAT IS DELIBERATELY NOT HERE: promoting a rule out of dry run. Everything below stores a rule that
WATCHES and TYPES NOTHING; arming it is the one step that lets a machine act on somebody's sessions
unattended, and the owner's ruling is that an agent's credential may not do it. The Gateway refuses it
twice - at the route guard and at the promotion grant itself - and this command group does not offer it.
"""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any, Dict, List, Optional

import requests
import typer
from rich.console import Console

# The shared transport lives beside this tool rather than inside it, so the path has to be on sys.path
# before the import - the same bootstrap every sibling ops module does.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import gateway  # noqa: E402

from . import session_ops  # noqa: E402

console = Console()
err_console = Console(stderr=True)

TIMEOUT_SECONDS = 120

GatewayError = gateway.GatewayError


class RuleClient:
    """Talks to one Gateway's rule surface, and to the session screen a rule is written against."""

    def __init__(self, base_url: Optional[str] = None) -> None:
        self.base_url = (base_url or gateway.gateway_base_url()).rstrip("/")
        self._token = gateway.session_key()

    def _headers(self) -> Dict[str, str]:
        headers = {"Accept": "application/json"}
        if self._token:
            headers["Authorization"] = f"Bearer {self._token}"
        return headers

    def _request(self, method: str, path: str, json_body: Optional[Dict[str, Any]] = None):
        url = f"{self.base_url}{path}"
        try:
            return requests.request(
                method, url, json=json_body, headers=self._headers(), timeout=TIMEOUT_SECONDS
            )
        except requests.exceptions.ConnectionError as exc:
            raise GatewayError(
                f"the Gateway at {self.base_url} could not be reached, so nothing was read or "
                "written. Rules live on the Gateway; there is no local copy to work from."
            ) from exc
        except requests.exceptions.Timeout as exc:
            raise GatewayError(
                f"the Gateway at {self.base_url} did not answer within {TIMEOUT_SECONDS}s. Working "
                "out a rule asks a model, which can be slow - run it again."
            ) from exc

    # A 2XX IS NOT PROOF THE GATEWAY UNDERSTOOD THE REQUEST. The Gateway serves the Cockpit at "/" and
    # falls unknown page paths back to index.html, so a Gateway from before Session Rules answers
    # /gateway/rules with HTTP 200 and a page of HTML. Read at face value that is an empty rule list,
    # which reads as "you have no rules" and is a different statement from "this Gateway cannot hold
    # rules at all".
    def _json_or_raise(self, resp) -> Dict[str, Any]:
        content_type = (resp.headers.get("Content-Type") or "").split(";")[0].strip().lower()
        if not resp.ok:
            raise GatewayError(self._message(resp))
        if content_type != "application/json":
            raise GatewayError(
                f"this Gateway answered with {content_type or 'an unlabelled body'} instead of rule "
                "data, which happens when it is running a build from before Session Rules existed. "
                "Upgrade or redeploy the Gateway."
            )
        try:
            return resp.json()
        except ValueError as exc:
            raise GatewayError("the Gateway's answer could not be read as JSON.") from exc

    @staticmethod
    def _message(resp) -> str:
        """The Gateway's OWN sentence for a refusal, which is the useful part.

        A rule it will not store is refused in plain English - which check does not exist, which value
        was missing, which trigger word is not on the screen. Flattening that into an HTTP status is
        how an agent ends up retrying the same malformed rule forever.
        """
        try:
            data = resp.json()
            if isinstance(data, dict) and data.get("error"):
                return str(data["error"])
        except ValueError:
            pass
        text = (resp.text or "").strip()
        return text if text else f"the Gateway returned HTTP {resp.status_code}"

    def rules(self) -> List[Dict[str, Any]]:
        answer = self._json_or_raise(self._request("GET", "/gateway/rules"))
        return [_read_rule("GET /gateway/rules", r) for r in _need(answer, "rules", list, "GET /gateway/rules")]

    def rule(self, rule_id: str) -> Dict[str, Any]:
        what = "GET /gateway/rules/{id}"
        answer = self._json_or_raise(self._request("GET", f"/gateway/rules/{rule_id}"))
        return _read_rule(what, _need(answer, "rule", dict, what))

    def firings(self, rule_id: str) -> List[Dict[str, Any]]:
        what = "GET /gateway/rules/{id}/firings"
        answer = self._json_or_raise(self._request("GET", f"/gateway/rules/{rule_id}/firings"))
        return [_read_firing(what, f) for f in _need(answer, "firings", list, what)]

    def screen(self, session_id: str, lines: int = 60) -> str:
        """The session's terminal text as it is RIGHT NOW, for an agent that wants to LOOK at the thing a
        rule would watch for. This is never sent to the Gateway - the Gateway reads the screen itself
        when a rule is drafted or stored."""
        resp = self._request("GET", f"/sessions/{session_id}/buffer?lines={lines}")
        text = (self._json_or_raise(resp).get("text") or "").strip()
        if not text:
            raise GatewayError(
                f"session {session_id} answered with an empty screen, so there is nothing to write a "
                "rule against. An empty screen is not a capture."
            )
        return text

    def draft(self, said: str, session_id: str, all_agents: bool = False) -> Dict[str, Any]:
        """Turn a sentence into a rule, about the named session. STORES NOTHING.

        The Gateway reads the session's screen itself and scopes the rule to that session's agent unless
        `all_agents` (the star) is set. Nothing about the screen, the agent or the machine is sent from
        here - they are facts the Gateway holds, not claims this client makes.
        """
        answer = self._json_or_raise(
            self._request(
                "POST",
                "/gateway/rules/draft",
                {
                    "turns": [{"who": "person", "text": said}],
                    "sessionId": session_id,
                    "allAgents": all_agents,
                },
            )
        )
        return _read_draft_answer("POST /gateway/rules/draft", answer)

    def create(self, rule_body: Dict[str, Any]) -> Dict[str, Any]:
        """Store a rule. It is ALWAYS stored in dry run - there is no argument that could make it live.
        The body names the session it was grounded in; the Gateway reads that screen again first."""
        what = "POST /gateway/rules"
        answer = self._json_or_raise(self._request("POST", "/gateway/rules", rule_body))
        return _read_rule(what, _need(answer, "rule", dict, what))

    def delete(self, rule_id: str) -> bool:
        what = "DELETE /gateway/rules/{id}"
        answer = self._json_or_raise(self._request("DELETE", f"/gateway/rules/{rule_id}"))
        return _need(answer, "deleted", bool, what)


# ---- the shape of what came back, checked before anything is believed (fix round E, ruling E2) ---------

def _kind(value: Any) -> str:
    if value is None:
        return "null"
    if isinstance(value, bool):
        return "a boolean"
    if isinstance(value, list):
        return "a list"
    if isinstance(value, dict):
        return "an object"
    return type(value).__name__


def _need(obj: Any, field: str, kind: type, what: str) -> Any:
    """The field, present and of the kind asked for, or a GatewayError naming it - never a quiet
    default. `bool` is asked for exactly (a Python bool is an int, and 1 is not an answer to a yes-or-no
    question)."""
    if not isinstance(obj, dict) or field not in obj:
        raise GatewayError(
            f"{what} answered without a '{field}' field, so nothing can be said about it. That is "
            "not an empty result; it is an answer this command cannot read."
        )
    value = obj[field]
    ok = isinstance(value, bool) if kind is bool else (isinstance(value, kind) and not isinstance(value, bool))
    if not ok:
        wanted = {str: "a string", int: "a whole number", bool: "a boolean", list: "a list", dict: "an object"}[kind]
        raise GatewayError(
            f"{what} answered with '{field}' as {_kind(value)} where {wanted} was expected, so nothing "
            "can be said about it. That is not an empty result; it is an answer this command cannot read."
        )
    return value


def _need_strings(obj: Any, field: str, what: str) -> List[str]:
    values = _need(obj, field, list, what)
    for item in values:
        if not isinstance(item, str):
            raise GatewayError(
                f"{what} answered with '{field}' holding {_kind(item)} where a list of strings was expected."
            )
    return values


def _read_rule(what: str, value: Any) -> Dict[str, Any]:
    """A rule as the Gateway serves one, every required field checked and nothing defaulted."""
    if not isinstance(value, dict):
        raise GatewayError(f"{what} answered with a rule that is {_kind(value)} where an object was expected.")
    for field in ("id", "instruction", "screenDescription", "scopeLabel", "waitLabel", "state",
                  "promotedBy", "acknowledgement", "createdUtc", "updatedUtc"):
        _need(value, field, str, what)
    _need_strings(value, "triggerWords", what)
    _need_strings(value, "checks", what)
    scope = _need(value, "scope", dict, what)
    for part in ("agent", "repository", "machine", "mission"):
        if part in scope and scope[part] is not None and not isinstance(scope[part], str):
            raise GatewayError(
                f"{what} answered with 'scope.{part}' as {_kind(scope[part])} where a string or null was expected."
            )
    _need(value, "cooldownSeconds", int, what)
    _need(value, "dailyCap", int, what)
    return value


def _read_firing(what: str, value: Any) -> Dict[str, Any]:
    """A firing as the Gateway serves one, every required field checked and nothing defaulted."""
    if not isinstance(value, dict):
        raise GatewayError(f"{what} answered with a firing that is {_kind(value)} where an object was expected.")
    for field in ("id", "ruleId", "sessionId", "occurredUtc", "screenText", "understanding", "decision",
                  "reason", "typedText", "outcome", "grounding"):
        _need(value, field, str, what)
    for run in _need(value, "checksRun", list, what):
        for field in ("name", "arguments", "answer"):
            _need(run, field, str, what)
    return value


def _read_draft_answer(what: str, answer: Any) -> Dict[str, Any]:
    """A draft answer: a proposal with every part, or a question. Anything else is unreadable."""
    if not isinstance(answer, dict):
        raise GatewayError(f"{what} answered with {_kind(answer)} where an object was expected.")
    if "rule" in answer:
        rule = _need(answer, "rule", dict, what)
        for field in ("instruction", "sessionId", "screenDescription"):
            _need(rule, field, str, what)
        _need(rule, "allAgents", bool, what)
        _need_strings(rule, "triggerWords", what)
        _need(rule, "checks", list, what)
        if "scope" not in rule or not isinstance(rule["scope"], (str, dict)):
            raise GatewayError(f"{what} answered with a rule whose 'scope' is not a string or an object.")
        _need(rule, "cooldownSeconds", int, what)
        _need(rule, "dailyCap", int, what)
        for field in ("readBack", "exampleScreen", "scopeLabel", "waitLabel"):
            _need(answer, field, str, what)
        return answer
    if "question" in answer:
        _need(answer, "question", str, what)
        return answer
    raise GatewayError(f"{what} answered without a rule, a question or a reason.")


def _client() -> RuleClient:
    try:
        return RuleClient()
    except GatewayError as exc:
        err_console.print(f"[red]Error:[/red] {exc}")
        raise typer.Exit(1)


def _session_id(target: str) -> str:
    """The session a rule is being written against, resolved the way every session verb resolves one -
    by number, id prefix, or name. Only the id is needed: the agent and the machine are facts the
    Gateway reads off its own roster, never something this client tells it."""
    chosen = session_ops.resolve_session(target, command_name="cc-devthrottle rule")
    return gateway.field(chosen, "sessionId", "SessionId")


# THE ONE LABEL THIS CLIENT HOLDS FOR A RULE WITH NOTHING TO TYPE, in the Gateway's own words for it:
# such a rule was stored before rules carried their text, and the Gateway refuses to fire it and refuses
# to promote it until it is re-authored. Printed like every other rule it would hide exactly that.
NEEDS_REAUTHORING = (
    "needs re-authoring - it was stored before a rule carried the exact text it types, so it has "
    "nothing to type"
)


def _types_line(rule: Dict[str, Any], what: str) -> str:
    """THE KEYSTROKE, VERBATIM. The text a rule types is the most consequential thing it does, so it is
    printed exactly as the Gateway serves it - never trimmed, never rephrased. Read through the same
    reader as every other field (ruling E2): a rule served without it is an error, not a blank line."""
    text = _need(rule, "textToType", str, what)
    return f"  types         {text if text else NEEDS_REAUTHORING}"


def _describe(rule: Dict[str, Any]) -> None:
    """Print a rule the Gateway served. It has been read through `_read_rule`, so every field is
    present and of the right shape; nothing here defaults a missing one, and a rule that reaches this
    unread is an error naming the field rather than a line printing 0."""
    what = "the rule being printed"
    console.print(f"[bold]{_need(rule, 'instruction', str, what)}[/bold]")
    console.print(f"  id            {_need(rule, 'id', str, what)}")
    console.print(f"  state         {_need(rule, 'state', str, what)}")
    console.print(_types_line(rule, what), markup=False, highlight=False)
    console.print(f"  watches for   {_need(rule, 'screenDescription', str, what)}")
    console.print(f"  trigger words {', '.join(_need_strings(rule, 'triggerWords', what))}")
    checks = _need_strings(rule, "checks", what)
    console.print(f"  checks        {', '.join(checks) if checks else 'none asked for'}")
    console.print(f"  acts on       {_need(rule, 'scopeLabel', str, what)}")
    console.print(f"  ceilings      {_need(rule, 'waitLabel', str, what)} apart, {_need(rule, 'dailyCap', int, what)} a day")
    if _need(rule, "promotedBy", str, what):
        console.print(f"  made live by  {rule['promotedBy']}")
    if _need(rule, "acknowledgement", str, what):
        console.print(f"  who agreed to {rule['acknowledgement']}")


def list_rules(json_output: bool) -> None:
    client = _client()
    try:
        rules = client.rules()
        if json_output:
            console.print_json(json.dumps({"rules": rules}))
            return
        if not rules:
            console.print("No rules yet.")
            return
        for rule in rules:
            _describe(rule)
            console.print("")
    except GatewayError as exc:
        err_console.print(f"[red]Error:[/red] {exc}")
        raise typer.Exit(1)


def show_rule(rule_id: str, json_output: bool) -> None:
    client = _client()
    try:
        rule = client.rule(rule_id)
        firings = client.firings(rule_id)
        if json_output:
            console.print_json(json.dumps({"rule": rule, "firings": firings}))
            return
        _describe(rule)
        console.print("")
        if not firings:
            console.print("It has not fired yet.")
            return
        # A DECLINE IS A FIRING TOO, and is printed like an act: a rule that did nothing because it
        # decided not to act must not read the same as one that did nothing because something broke.
        console.print(f"[bold]What it has done[/bold] ({len(firings)})")
        for firing in firings:
            what = "the firing being printed"
            console.print(
                f"  {_need(firing, 'occurredUtc', str, what)}  {_need(firing, 'decision', str, what)}  "
                f"session {_need(firing, 'sessionId', str, what)}"
            )
            console.print(f"    why      {_need(firing, 'reason', str, what)}")
            if _need(firing, "typedText", str, what):
                console.print(f"    typed    {firing['typedText']}")
            console.print(f"    outcome  {_need(firing, 'outcome', str, what)}")
    except GatewayError as exc:
        err_console.print(f"[red]Error:[/red] {exc}")
        raise typer.Exit(1)


def show_screen(target: str, lines: int) -> None:
    """Print a session's terminal, so an agent can look at the thing a rule would watch for."""
    client = _client()
    try:
        console.print(client.screen(_session_id(target), lines))
    except GatewayError as exc:
        err_console.print(f"[red]Error:[/red] {exc}")
        raise typer.Exit(1)


def draft_rule(said: str, target: str, json_output: bool, all_agents: bool = False) -> None:
    """Work out a rule about the named session and print the proposal. STORES NOTHING.

    The proposal printed under 'rule' (and the whole JSON answer with --json) is exactly what
    `rule add` takes, so the document a person reads here is the document that gets stored.
    """
    client = _client()
    try:
        answer = client.draft(said, _session_id(target), all_agents)
    except GatewayError as exc:
        err_console.print(f"[red]Error:[/red] {exc}")
        raise typer.Exit(1)

    if json_output:
        console.print_json(json.dumps(answer))
        return

    # A QUESTION IS A FIRST-CLASS ANSWER. A model that does not know which sessions a rule is for has
    # to be able to ask, or it picks the widest scope it can and hands back a rule nobody asked for.
    if answer.get("question"):
        console.print(f"[yellow]It needs to know:[/yellow] {answer['question']}")
        console.print("Run this again with that answered in the sentence.")
        return

    console.print(answer["readBack"])
    console.print("")
    # THE TEXT IT TYPES, RIGHT UNDER THE READ-BACK. The read-back is what a person confirms, and one
    # that described the situation but hid the keystroke asked them to approve an action they were not
    # shown. So the exact text comes before anything else about the rule.
    console.print(_types_line(answer["rule"], "the drafted rule"), markup=False, highlight=False)
    console.print(f"  acts on       {answer['scopeLabel']}")
    console.print(f"  ceilings      {answer['waitLabel']} apart, {answer['rule']['dailyCap']} a day")
    console.print("")
    console.print(
        "[dim]Nothing was stored. Save this answer as JSON (run again with --json) and pass the file to "
        "'rule add' to store exactly this as a dry run.[/dim]"
    )
    console.print_json(json.dumps(answer))


def _read_proposal(source: str) -> Dict[str, Any]:
    """The proposal `rule draft --json` printed: the whole answer, or just its 'rule' body."""
    try:
        text = sys.stdin.read() if source == "-" else Path(source).read_text(encoding="utf-8")
    except OSError as exc:
        raise GatewayError(f"the proposal could not be read from {source}: {exc}") from exc
    try:
        document = json.loads(text)
    except ValueError as exc:
        raise GatewayError(
            f"the proposal in {source} is not JSON. Pass the file 'rule draft --json' wrote."
        ) from exc
    if not isinstance(document, dict):
        raise GatewayError(f"the proposal in {source} is not a JSON object.")
    if document.get("question"):
        raise GatewayError(
            "that answer is a question, not a rule: " + str(document["question"]) + ". Nothing was "
            "stored. Draft again with the question answered in the sentence."
        )
    rule_body = document.get("rule") if "rule" in document else document
    if not isinstance(rule_body, dict) or not rule_body.get("instruction"):
        raise GatewayError(
            f"the proposal in {source} holds no rule to store - it has no 'rule' with an 'instruction'. "
            "Pass the file 'rule draft --json' wrote."
        )
    return {"rule": rule_body, "readBack": document.get("readBack", "")}


def add_rule(source: str, json_output: bool) -> None:
    """Store the proposal `rule draft` printed - exactly that document, with NO authoring call - as a
    dry run that watches and types nothing."""
    client = _client()
    try:
        proposal = _read_proposal(source)
        stored = client.create(proposal["rule"])
        if json_output:
            console.print_json(json.dumps({"rule": stored, "readBack": proposal["readBack"]}))
            return
        if proposal["readBack"]:
            console.print(proposal["readBack"])
            console.print("")
        _describe(stored)
    except GatewayError as exc:
        err_console.print(f"[red]Error:[/red] {exc}")
        raise typer.Exit(1)

    console.print("")
    console.print(
        "[dim]Stored as a DRY RUN: it watches, records what it WOULD have done, and types nothing. "
        "Make it live from the Cockpit's Rules page when you have read what it would do.[/dim]"
    )


def delete_rule(rule_id: str) -> None:
    client = _client()
    try:
        deleted = client.delete(rule_id)
    except GatewayError as exc:
        err_console.print(f"[red]Error:[/red] {exc}")
        raise typer.Exit(1)
    # The record outlives the rule, and saying so stops somebody deleting a rule to hide what it did.
    console.print("Deleted. Its firings are kept." if deleted else "There is no rule with that id.")
