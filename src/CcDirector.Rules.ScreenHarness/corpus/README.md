# The corpus of real captured screens (Session Rules mission, phase 0)

Every screen in `cases/` is a REAL terminal capture. None was written by hand, and none was edited
inside the window that is kept. A screen that says what the author expected it to say is exactly the
bias this corpus exists to remove, so a case that could not be found on a real terminal was left out
rather than composed.

Each case is a directory holding two files:

- `screen.txt` - the captured screen, bytes as captured. Most carry non-ASCII characters (box-drawing
  rules, spinner glyphs, the agent's own status bar) and some carry `\r\n` line endings, because that is
  what the terminal held. The `nonAscii` flag in `case.json` says which. Nothing was tidied.
- `case.json` - the expected answer (`act` or `decline`), the rule that should act when the answer is
  `act`, the kind of case, the written REASON why that is the right answer, the session facts the engine
  is given and how they were established, and where the screen came from.

`rules.json` holds the three standing instructions every case is judged against, in the shape of the
product's `SessionRule` (scope: all sessions; state: dry run). The allowance rule and the outage rule are
the owner's two headline cases; the build-failure rule is the negative control from the mission's earlier
demonstration, whose broad trigger words (`failed`, `error`) put it in play on many screens where it must
decline.

## Where the screens came from

| Method (in `source.method`) | Cases | What it is |
| --- | --- | --- |
| `turn-package screen tail` | 24 | The `screenTail` field of a Gateway turn-brief package: the last 4,000 characters of the terminal text, captured by the Gateway's turn-brief pipeline when a turn ended. These live under the Gateway's data root (`gateway-turnbriefs/<session>.packages/t<N>.json`) and were captured in June 2026, when the owner's fleet hit its limits repeatedly. |
| `gateway buffer route` | 7 | `GET /sessions/{id}/buffer` on the hosted Gateway, called with a session key on 2026-09-03. The route returns the session's cleaned scrollback; the case keeps a window of it ending where the session stopped or where the passage appears, and `source.detail` gives the line numbers. |
| `fixture copied from another mission` | 1 | The terminal-rules mission's `blocked-session-101-screen-tail.txt`, captured from a genuinely blocked session and copied byte for byte with its origin named. |

Twenty-five distinct sessions are represented.

## The kinds

| `kind` | Expected | What it means |
| --- | --- | --- |
| `positive` | `act` | The session is stopped on the situation a rule describes - its own allowance notice, or its own provider outage notice. |
| `negative-documentation` | `decline` | The trigger words are in text the session is reading or writing - help output, a menu's descriptions, prose. |
| `negative-code` | `decline` | The trigger words are in code the session wrote or read - a test constant, a fixture, a comment. |
| `negative-report` | `decline` | The trigger words are in a report about another session or another thing - a fleet listing, a summary of fixes, a log the agent quoted, a background command's name. |
| `negative-own-state-different-situation` | `decline` | The words ARE the session's own state, and the situation is still not the rule's: a context limit rather than an allowance, a warning banner while the allowance still has room, a local tool that timed out. These are the sharpest negatives in the corpus. |
| `negative-substring` | `decline` | The trigger word matched inside another token - `4529 warnings`, a commit hash `c20f529`. Cheap for a model, and a reminder that the free checks match substrings. |

The corpus test (`ScreenCorpusTests` in `CcDirector.Gateway.UnitTests`) asserts that for EVERY case the
real free checks choose at least one rule on the last 40 non-blank lines, which is the window the engine
carries into the model question. That is what makes a negative a negative: the model is asked, and the
right answer is still no.

## What was dropped, and why

One candidate screen (turn package t364 of session `9fcf02f8-...`, a stop on `API Error: 403` with a
`Please run /login` notice, a good negative for the outage rule) carried a pasted sign-in code in the
composer. It was dropped rather than edited, as the phase brief requires.

Ten more real limit stops from the same June days were left out only to keep the run time reasonable;
they are the same shape as the ten kept and can be added by the same script if a later phase wants a
larger positive set.

## Running the corpus through the engine

```
dotnet run --project src/CcDirector.Rules.ScreenHarness -- --models wingman,wingman-fast
```

The runner constructs the real `RuleEvaluator` with an environment whose screen read returns the case's
rows and whose model call is the real `HostedInferenceBrain`; it never builds a prompt and never reads a
reply itself. See the project's own README for the report it writes.
