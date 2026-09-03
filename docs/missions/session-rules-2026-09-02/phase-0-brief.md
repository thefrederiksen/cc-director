# Phase 0 - the harness of real captured screens

The acceptance row is in `implementation-plan-for-the-architect.html`, section 6. This brief adds the
things the Architect established by going and looking, so nobody has to rediscover them.

**Nothing later in this mission is trusted until this exists.** Phase 1 changes which model answers
the run-time question, and the ONLY honest way to accept that change is to put real screens through
the real engine on both models and compare. Without this harness, Phase 1 is an opinion.

---

## What to build

A corpus of REAL captured terminal screens with the right answer written down beside each, and a
runner that puts every case through the real engine.

### Where the real screens come from - this is settled, do not invent a capture mechanism

**`GET /sessions/{sessionId}/buffer` on the Gateway, which a session key may already call.** Verified
by the Architect on 2026-09-03 against the live hosted Gateway: it returns
`{sessionId, totalBytes, newCursor, text}` and the text is the session's real terminal scrollback -
386,379 characters on the first session tried. `cc-devthrottle session list --json` gives you every
live session's `sessionId`, `agent`, `activityState`, `statusColor` and `lastStatusReason`, which is
how you find the interesting ones. The fleet on this machine runs a dozen sessions across several
agents and repositories, and several sit red on "needs you" - that is your raw material.

Two things follow from this and both matter:

- **You do NOT need to build a screen store, and you must not.** A screen store with automatic
  capture on idle and a retention sweep is already built on `origin/mission/terminal-rules`
  (`SessionScreenStore`, `SessionScreenEntity`, `SessionScreenSweep`, `GatewayScreenReader`, and
  migration `20260902154804_AddSessionScreens`), unlanded on pull request 2661. The owner's decision
  that capture is automatic on idle is about the ONGOING feed, and that feed is that mission's work.
  Phase 0's corpus is TEST FIXTURES: files on disk, checked in, with the right answer written down.
  A fixture corpus needs no database at all.
- **`cc-devthrottle rule screen <session>` also exists** and reads one session's screen right now. Use
  whichever is convenient; the buffer route gives you scrollback, which is what you want for
  harvesting.

**One genuinely blocked screen already exists as a fixture** and is the most valuable case in the
corpus, because it cannot be manufactured honestly:
`docs/missions/terminal-rules-2026-09-02/fixtures/blocked-session-101-screen-tail.txt` on the
`origin/mission/terminal-rules` branch. Its operative line is `You've reached your Fable 5 limit. Run
/usage-credits to continue or switch models with /model.` Copy it in and credit where it came from.

### The corpus

At least **20 real cases, at least half of them NEGATIVE**. A negative is a screen where the trigger
words ARE present and the right answer is still "decline". They are the whole point, and they are the
cases the corpus will be short of unless you go looking for them deliberately. Three kinds to hunt:

1. The words appear in **documentation the session is reading** - this very brief, on a screen, is
   one; so is any session that has `cat`-ed a file about usage limits.
2. The words appear in **code the session just wrote** - a test fixture, a string constant, a comment.
3. The words appear in **a report about another session** - a fleet listing, a handover, a QA report.
   The fleet on this machine generates these constantly.

Every case is a file plus a written-down expected answer and, crucially, **a written reason why that
is the right answer**. A corpus whose expected answers are unexplained is a corpus nobody can argue
with later, and the arguing is what makes it useful.

Redact nothing silently: these are the owner's own terminals. If a screen carries a secret, drop the
case rather than editing it, and say in the corpus that you did.

### The runner

Puts each case through the **real engine, not a copy**. That phrase is in the acceptance row and it
is the part most likely to be quietly broken: if the runner builds its own prompt, or its own reader,
it proves something about the runner. It must go through the same `RuleAgentContract.BuildPrompt` and
`RuleAgentContract.Read` the evaluator uses, and the same free checks.

It reports, per model and per case: the answer, whether it was right, and the time. Then, prominently,
**the count of wrong answers on negatives** - a false "act" is the unacceptable failure and it is the
number the phase is judged on. A wrong decline costs a missed recovery; a wrong act types something
nobody asked for into a live coding agent.

---

## Acceptance - copy these into your report with real numbers

| Row | What it takes to pass |
| --- | --- |
| At least 20 real cases | The count, and where each screen came from |
| At least half negative | The count, and the three kinds represented |
| Runs against the real engine | Name the shared functions it goes through, and show it is not a second implementation |
| Reports per model, per case | Answer, right or wrong, time |
| The negatives number is prominent | Wrong answers on negatives, stated as a count, not buried |

## What NOT to do

- Do not build a screen store, a migration, or a capture service. That is another mission's work and
  it is already built.
- Do not write screens by hand and call them real. A screen you composed is a screen that says what
  you expected it to say, which is exactly the bias the corpus exists to remove. If you cannot find a
  real screen for a case, record the case as missing.
- Do not park the negatives for later. A corpus of positives is the failure mode this phase exists to
  prevent.

## The gate

- `.\scripts\test-local.ps1` green. The Postgres proof rig must be up or the run is red for reasons
  that have nothing to do with you - container `cc-pg-test` on port 55432 was up on 2026-09-03.
- ASCII only in everything you touch, except where a captured screen genuinely contains other bytes -
  in which case say so, and keep the fixture faithful rather than tidied.
- No mention of any assistant, model, vendor or AI tool in a commit message, a document or a comment.
  Naming a MODEL as a subject of measurement is different and is required: the runner compares models
  and must say which.

## How to finish

Commit and push on your phase branch. Report to the Architect in ONE SINGLE LINE - fleet messages
truncate at the first newline. Write the detail to
`docs/missions/session-rules-2026-09-02/phase-0-report.md` and name it in your one line. Do not open a
pull request and do not merge; only the Architect lands work on main.
