"""Shared helpers for the cc-devthrottle tests.

WHY THIS EXISTS. These tests drive the command-line surface through Typer's CliRunner and then
assert on what the user would read: that `--subject` is offered, that a summary says "900
directories", that an error message survives intact. Rich renders that output with STYLE, so the
captured text carries ANSI escape sequences threaded through it, and a plain `in` check against a
styled string can miss a substring that is plainly on the screen:

    assert '--subject' in '\x1b[1m ... \x1b[0m\x1b[1;36m--subject\x1b[0m ...'   # False

Whether styling is on depends on the environment, not on the code under test. Locally, with output
captured to a buffer, Rich usually renders plain and the assertions pass; on a continuous
integration runner colour is on and they fail. Five of these tests had never been run anywhere but
a developer's console, so nobody had seen it (issue #1082, found by the job added in #1077).

The fix is to assert against the TEXT rather than the rendering. `plain` strips the escape
sequences, so the assertion means the same thing wherever it runs. It deliberately does not turn
colour off: forcing a plain console would make these particular tests pass while leaving the next
one written the same way just as fragile, and it would depend on knowing exactly which environment
variable a given runner uses to enable colour.
"""

import re

import pytest

# Control Sequence Introducer colour/style codes: ESC [ ... m. That is the whole of what Rich emits
# for styling here; cursor movement and the like do not appear in captured CliRunner output.
_ANSI_STYLE = re.compile(r"\x1b\[[0-9;]*m")


def strip_ansi(text: str) -> str:
    """Return `text` with ANSI style sequences removed, leaving what a reader actually sees."""
    return _ANSI_STYLE.sub("", text)


@pytest.fixture
def plain():
    """Strip ANSI styling from captured command output before asserting on it."""
    return strip_ansi
