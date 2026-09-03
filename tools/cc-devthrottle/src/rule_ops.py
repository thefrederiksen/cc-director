"""Session Rules from the command line - the surface an AGENT uses to set a rule up.

A rule is a standing instruction about your sessions: it sits there costing nothing until one of them
goes idle with something on its screen that looks like the thing you described, and then an agent reads
that screen and your instruction together and does what you asked.

WHY THIS EXISTS AT ALL, when the Cockpit has a page for it. The owner's own framing, on 3 September
2026: point a coding agent at a session, say "here is the problem, set up a rule for when this happens",
and let a genuinely capable agent do the authoring. That is only possible if the whole surface is
reachable from a command line. It also means the built-in page never has to be the clever one - the
clever one is whatever agent you point at this.

THE SCREEN IS THE POINT. `rule add` reads the named session's terminal and hands it to the drafting
call, and the Gateway then REFUSES any trigger word that is not on that screen. Written blind, the
words are the model's guess at what a screen SAYS - measured on 3 September, describing a usage-limit
rule from memory produced one watching for "hit its limit" and "when it comes back", which are the
person's own phrasing and appear on no screen anywhere. It would have sat in the list looking correct
and never fired once. Reading the real screen first is the difference between a rule and a decoration.

WHAT IS DELIBERATELY NOT HERE: promoting a rule out of dry run. Everything below stores a rule that
WATCHES and TYPES NOTHING; arming it is the one step that lets a machine act on somebody's sessions
unattended, and whether an agent's credential should be able to do that is an open question the owner
has not answered. Leaving it out costs one click in the Cockpit and keeps a person on the only step
where it matters. Adding it later is four lines.
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
        return self._json_or_raise(self._request("GET", "/gateway/rules")).get("rules", [])

    def rule(self, rule_id: str) -> Dict[str, Any]:
        return self._json_or_raise(self._request("GET", f"/gateway/rules/{rule_id}")).get("rule", {})

    def firings(self, rule_id: str) -> List[Dict[str, Any]]:
        return self._json_or_raise(
            self._request("GET", f"/gateway/rules/{rule_id}/firings")
        ).get("firings", [])

    def screen(self, session_id: str, lines: int = 60) -> str:
        """The session's terminal text as it is RIGHT NOW.

        Nothing stores terminal output, so this is the only place a rule's example screen can come
        from - and it is the screen as it is at this moment, not the one that annoyed you an hour ago.
        """
        resp = self._request("GET", f"/sessions/{session_id}/buffer?lines={lines}")
        text = (self._json_or_raise(resp).get("text") or "").strip()
        if not text:
            raise GatewayError(
                f"session {session_id} answered with an empty screen, so there is nothing to write a "
                "rule against. An empty screen is not a capture."
            )
        return text

    def draft(
        self,
        said: str,
        screen: str,
        session_agent: str = "",
        session_machine: str = "",
        all_agents: bool = False,
    ) -> Dict[str, Any]:
        """Turn a sentence into a rule. STORES NOTHING.

        `session_agent` and `session_machine` say which session the screen came from. The Gateway scopes the
        rule to that agent unless `all_agents` (the star) is set, whatever the model would have chosen - it
        is a fact we hold, not a guess to make.
        """
        return self._json_or_raise(
            self._request(
                "POST",
                "/gateway/rules/draft",
                {
                    "turns": [{"who": "person", "text": said}],
                    "screen": screen,
                    "sessionAgent": session_agent,
                    "sessionMachine": session_machine,
                    "allAgents": all_agents,
                },
            )
        )

    def create(self, rule_body: Dict[str, Any]) -> Dict[str, Any]:
        """Store a rule. It is ALWAYS stored in dry run - there is no argument that could make it live."""
        return self._json_or_raise(self._request("POST", "/gateway/rules", rule_body)).get("rule", {})

    def delete(self, rule_id: str) -> bool:
        return bool(
            self._json_or_raise(self._request("DELETE", f"/gateway/rules/{rule_id}")).get("deleted")
        )


def _client() -> RuleClient:
    try:
        return RuleClient()
    except GatewayError as exc:
        err_console.print(f"[red]Error:[/red] {exc}")
        raise typer.Exit(1)


def _session(target: str) -> Dict[str, str]:
    """The session a rule is being written against: its id, and the two facts that change what its screen
    MEANS - which agent it runs and where. A usage-limit notice on Claude Code reads "Claude usage limit
    reached"; on Codex it reads something else. The Gateway uses the agent to scope the rule (the owner's
    ruling: a rule written against a session is for that session's agent unless you say every agent) and
    to tell the drafting model whose screen it is looking at."""
    chosen = session_ops.resolve_session(target, command_name="cc-devthrottle rule")
    return {
        "id": gateway.field(chosen, "sessionId", "SessionId"),
        "agent": gateway.field(chosen, "agent", "Agent") or "",
        "machine": gateway.field(chosen, "machineName", "MachineName") or "",
    }


def _session_id(target: str) -> str:
    return _session(target)["id"]


def _describe(rule: Dict[str, Any]) -> None:
    console.print(f"[bold]{rule.get('instruction', '')}[/bold]")
    console.print(f"  id            {rule.get('id', '')}")
    console.print(f"  state         {rule.get('state', '')}")
    console.print(f"  watches for   {rule.get('screenDescription', '')}")
    console.print(f"  trigger words {', '.join(rule.get('triggerWords') or []) or '(none)'}")
    checks = rule.get("checks") or []
    console.print(f"  checks        {', '.join(checks) if checks else '(none)'}")
    scope = rule.get("scope") or {}
    named = [f"{k} {v}" for k, v in scope.items() if v]
    console.print(f"  acts on       {', '.join(named) if named else 'every session'}")
    console.print(
        f"  ceilings      {rule.get('cooldownSeconds', 0)}s apart, "
        f"{rule.get('dailyCap', 0)} a day"
    )
    if rule.get("promotedBy"):
        console.print(f"  made live by  {rule['promotedBy']}")


def list_rules(json_output: bool) -> None:
    client = _client()
    try:
        rules = client.rules()
    except GatewayError as exc:
        err_console.print(f"[red]Error:[/red] {exc}")
        raise typer.Exit(1)

    if json_output:
        console.print_json(json.dumps({"rules": rules}))
        return

    if not rules:
        console.print("No rules yet.")
        return
    for rule in rules:
        _describe(rule)
        console.print("")


def show_rule(rule_id: str, json_output: bool) -> None:
    client = _client()
    try:
        rule = client.rule(rule_id)
        firings = client.firings(rule_id)
    except GatewayError as exc:
        err_console.print(f"[red]Error:[/red] {exc}")
        raise typer.Exit(1)

    if json_output:
        console.print_json(json.dumps({"rule": rule, "firings": firings}))
        return

    _describe(rule)
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


def _draft(
    client: RuleClient, said: str, target: Optional[str], lines: int, all_agents: bool
) -> Dict[str, Any]:
    if not target:
        return client.draft(said, "", all_agents=all_agents)
    session = _session(target)
    screen = client.screen(session["id"], lines)
    return client.draft(said, screen, session["agent"], session["machine"], all_agents)


def draft_rule(
    said: str, target: Optional[str], lines: int, json_output: bool, all_agents: bool = False
) -> None:
    """Work out a rule and print it. STORES NOTHING."""
    client = _client()
    try:
        answer = _draft(client, said, target, lines, all_agents)
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
    console.print("[dim]Nothing was stored. Use 'rule add' to store it as a dry run.[/dim]")
    console.print_json(json.dumps(answer.get("rule", {})))


def add_rule(
    said: str, target: Optional[str], lines: int, json_output: bool, all_agents: bool = False
) -> None:
    """Work out a rule from what you said and STORE it - always in dry run, watching and typing nothing."""
    client = _client()
    try:
        answer = _draft(client, said, target, lines, all_agents)
        if answer.get("question"):
            # Refusing to guess is the whole point. Storing a rule built on an unanswered question
            # would store a rule the person did not describe.
            err_console.print(f"[yellow]It needs to know:[/yellow] {answer['question']}")
            err_console.print("Nothing was stored. Run this again with that answered in the sentence.")
            raise typer.Exit(2)

        rule_body = answer.get("rule")
        if not rule_body:
            err_console.print("[red]Error:[/red] the Gateway drafted no rule and gave no question.")
            raise typer.Exit(1)

        stored = client.create(rule_body)
    except GatewayError as exc:
        err_console.print(f"[red]Error:[/red] {exc}")
        raise typer.Exit(1)

    if json_output:
        console.print_json(json.dumps({"rule": stored, "readBack": answer.get("readBack", "")}))
        return

    console.print(answer.get("readBack", ""))
    console.print("")
    _describe(stored)
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
