"""Add words to the dictation dictionary from a session (issue #2484).

WHY THIS COMMAND EXISTS. DevThrottle's dictation cleans up what it hears against a glossary of
words the person cares about - product names, surnames, tools. The documentation promised that
"add Kubernetes to my dictionary" was a single instruction to any connected agent, and it was not
true: no command existed, and the Gateway refused a session key on the term endpoint outright.
The owner ruled on 2026-08-07 that agents MAY add words, with no confirmation step in the way -
being asked to confirm every time is worse than the occasional stray entry.

ADD ONLY, AND THAT IS THE WHOLE VERB. There is deliberately no `dictionary remove`, no
`dictionary set`, and no `dictionary list` here, because the Gateway refuses a session key on
every one of those. A session key may add a term and nothing else: it can never delete, rename or
overwrite an existing term, and it can never touch the wrong-spellings list attached to one. So
the worst an agent can do is leave a stray extra word, and the person prunes in the Cockpit
dictionary editor exactly as they would a word they typed in themselves.

SPELL IT THE WAY IT IS WRITTEN DOWN. The spelling you add becomes the canonical one, and the
failure mode the owner named is a word that arrived through dictation ALREADY MANGLED being added
as though it were correct. Add a spelling you can SEE - from the repository, the code, the product
name in front of you - never one you only heard. `add` refuses a term that is obviously not a
written word for that reason; everything else is your judgement.
"""

from __future__ import annotations

import sys
from pathlib import Path
from typing import Any, Dict, List

import typer
from rich.console import Console

# Make cc_shared importable when running from source, matching the existing cc-* tools.
_tools_dir = str(Path(__file__).resolve().parent.parent.parent)
if _tools_dir not in sys.path:
    sys.path.insert(0, _tools_dir)

from cc_shared import gateway  # noqa: E402

console = Console()
err_console = Console(stderr=True)

#: The same class the shared transport raises, never a look-alike beside it - see mission_ops.py
#: for the trap that aliasing avoids.
GatewayError = gateway.GatewayError

TERMS_PATH = "/ingest/dictionary/terms"


def validate_terms(terms: List[str]) -> List[str]:
    """The terms to send, trimmed - or a message saying why one of them cannot be a dictionary word.

    Deliberately a THIN check. It rejects only what could not be a written word under any reading
    (empty, or carrying a line break, which means a block of prose was pasted where a term goes).
    It does NOT try to judge spelling: a checker that guessed would reject real product names, and
    the owner's instruction puts spelling on the agent, backed by looking at the written source,
    not on a rule in this file that cannot see the repository.
    """
    cleaned: List[str] = []
    for term in terms:
        trimmed = (term or "").strip()
        if not trimmed:
            raise ValueError("A dictionary term cannot be empty.")
        if "\n" in trimmed or "\r" in trimmed:
            raise ValueError(
                f"A dictionary term cannot span lines: {trimmed.splitlines()[0][:40]!r}... "
                "Add one word or phrase per term."
            )
        cleaned.append(trimmed)
    if not cleaned:
        raise ValueError("Give at least one term to add.")
    return cleaned


def add_terms(terms: List[str]) -> Dict[str, Any]:
    """POST the terms to this session's Gateway and return the glossary it answers with."""
    body = {"terms": terms}
    result = gateway.post_json(TERMS_PATH, body)
    return result if isinstance(result, dict) else {}


def add_command(terms: List[str]) -> None:
    """`cc-devthrottle dictionary add <term> [<term> ...]` - the command-line entry point."""
    try:
        cleaned = validate_terms(terms)
    except ValueError as err:
        err_console.print(f"[red]{err}[/red]")
        raise typer.Exit(code=1)

    try:
        glossary = add_terms(cleaned)
    except GatewayError as err:
        err_console.print(f"[red]{err}[/red]")
        raise typer.Exit(code=1)

    vocabulary = glossary.get("vocabulary") or []
    lowered = {str(word).lower() for word in vocabulary}
    missing = [term for term in cleaned if term.lower() not in lowered]

    for term in cleaned:
        if term.lower() in lowered:
            console.print(f"[green]In the dictation dictionary:[/green] {term}")

    # Say so rather than reporting a success the answer does not support. A term the Gateway
    # accepted but did not return is not "added" - and a command that printed success anyway is the
    # kind of report that has to be discovered by dictating the word and hearing it come out wrong.
    if missing:
        err_console.print(
            "[red]The Gateway accepted the request but these terms are not in the glossary it "
            f"returned: {', '.join(missing)}[/red]"
        )
        raise typer.Exit(code=1)

    console.print(
        "[dim]The person prunes this list in the Cockpit dictionary editor. You can add words; "
        "you cannot remove or change one.[/dim]"
    )
