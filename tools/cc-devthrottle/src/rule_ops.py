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

    @staticmethod
    def _field(answer: Dict[str, Any], what: str, field: str) -> Any:
        """The field the caller asked for, or an ERROR - never a quiet empty value.

        A MISSING FIELD IS A BROKEN INSTRUMENT (fix round D, ruling D8). An answer without `rules` in it
        is not an account with no rules; it is an answer this client cannot read, and saying "No rules
        yet" over it would report a positive fact the data never supported.
        """
        if not isinstance(answer, dict) or field not in answer:
            raise GatewayError(
                f"{what} answered without a '{field}' field, so nothing can be said about it. That is "
                "not an empty result; it is an answer this command cannot read."
            )
        return answer[field]

    def rules(self) -> List[Dict[str, Any]]:
        return self._field(self._json_or_raise(self._request("GET", "/gateway/rules")), "GET /gateway/rules", "rules")

    def rule(self, rule_id: str) -> Dict[str, Any]:
        return self._field(
            self._json_or_raise(self._request("GET", f"/gateway/rules/{rule_id}")), "GET /gateway/rules/{id}", "rule"
        )

    def firings(self, rule_id: str) -> List[Dict[str, Any]]:
        return self._field(
            self._json_or_raise(self._request("GET", f"/gateway/rules/{rule_id}/firings")),
            "GET /gateway/rules/{id}/firings",
            "firings",
        )

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
        return self._json_or_raise(
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

    def create(self, rule_body: Dict[str, Any]) -> Dict[str, Any]:
        """Store a rule. It is ALWAYS stored in dry run - there is no argument that could make it live.
        The body names the session it was grounded in; the Gateway reads that screen again first."""
        return self._field(
            self._json_or_raise(self._request("POST", "/gateway/rules", rule_body)), "POST /gateway/rules", "rule"
        )

    def delete(self, rule_id: str) -> bool:
        return bool(
            self._field(
                self._json_or_raise(self._request("DELETE", f"/gateway/rules/{rule_id}")),
                "DELETE /gateway/rules/{id}",
                "deleted",
            )
        )


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


def _label(rule: Dict[str, Any], field: str) -> str:
    """A label the Gateway stamped on the rule, printed verbatim. A rule served without one is an
    answer this client cannot read - it composes no words of its own for the scope or the wait."""
    if field not in rule:
        raise GatewayError(
            f"the Gateway served a rule without its '{field}', which happens on a build from before "
            "the labels were stamped on the Gateway. Upgrade or redeploy the Gateway."
        )
    return str(rule[field])


# THE ONE LABEL THIS CLIENT HOLDS FOR A RULE WITH NOTHING TO TYPE, in the Gateway's own words for it:
# such a rule was stored before rules carried their text, and the Gateway refuses to fire it and refuses
# to promote it until it is re-authored. Printed like every other rule it would hide exactly that.
NEEDS_REAUTHORING = (
    "needs re-authoring - it was stored before a rule carried the exact text it types, so it has "
    "nothing to type"
)


def _types_line(rule: Dict[str, Any]) -> str:
    """THE KEYSTROKE, VERBATIM. The text a rule types is the most consequential thing it does, so it is
    printed exactly as the Gateway serves it - never trimmed, never rephrased."""
    text = rule.get("textToType") or ""
    return f"  types         {text if text else NEEDS_REAUTHORING}"


def _describe(rule: Dict[str, Any]) -> None:
    console.print(f"[bold]{rule.get('instruction', '')}[/bold]")
    console.print(f"  id            {rule.get('id', '')}")
    console.print(f"  state         {rule.get('state', '')}")
    console.print(_types_line(rule), markup=False, highlight=False)
    console.print(f"  watches for   {rule.get('screenDescription', '')}")
    console.print(f"  trigger words {', '.join(rule.get('triggerWords') or []) or '(none)'}")
    checks = rule.get("checks") or []
    console.print(f"  checks        {', '.join(checks) if checks else '(none)'}")
    console.print(f"  acts on       {_label(rule, 'scopeLabel')}")
    console.print(f"  ceilings      {_label(rule, 'waitLabel')} apart, {rule.get('dailyCap', 0)} a day")
    if rule.get("promotedBy"):
        console.print(f"  made live by  {rule['promotedBy']}")
    if rule.get("acknowledgement"):
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
    except GatewayError as exc:
        err_console.print(f"[red]Error:[/red] {exc}")
        raise typer.Exit(1)

    console.print("")
    if not firings:
        console.print("It has not fired yet.")
        return
    # A DECLINE IS A FIRING TOO, and is printed like an act: a rule that did nothing because it decided
    # not to act must not read the same as one that did nothing because something broke.
    console.print(f"[bold]What it has done[/bold] ({len(firings)})")
    for firing in firings:
        console.print(
            f"  {firing.get('occurredUtc', '')}  {firing.get('decision', '')}  "
            f"session {firing.get('sessionId', '')}"
        )
        console.print(f"    why      {firing.get('reason', '')}")
        if firing.get("typedText"):
            console.print(f"    typed    {firing['typedText']}")
        console.print(f"    outcome  {firing.get('outcome', '')}")


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

    console.print(answer.get("readBack", ""))
    console.print("")
    # THE TEXT IT TYPES, RIGHT UNDER THE READ-BACK. The read-back is what a person confirms, and one
    # that described the situation but hid the keystroke asked them to approve an action they were not
    # shown. So the exact text comes before anything else about the rule.
    console.print(_types_line(answer.get("rule") or {}), markup=False, highlight=False)
    try:
        console.print(f"  acts on       {_label(answer, 'scopeLabel')}")
        console.print(
            f"  ceilings      {_label(answer, 'waitLabel')} apart, "
            f"{(answer.get('rule') or {}).get('dailyCap', 0)} a day"
        )
    except GatewayError as exc:
        err_console.print(f"[red]Error:[/red] {exc}")
        raise typer.Exit(1)
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
