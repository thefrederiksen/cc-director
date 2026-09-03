# Phase 0 report - the harness of real captured screens

Branch `mission/rules-p0`, written by the phase 0 Manager on 2026-09-03. The brief is
`phase-0-brief.md`; the acceptance row is section 6 of `implementation-plan-for-the-architect.html`.

## The one-paragraph version

The harness exists and it has already changed what the mission knows. Thirty-two real screens, twenty
of them negatives, went through the production `RuleEvaluator` on both models. **Neither model, through
today's contract, ever reached an act on a real screen.** The thinking model declined all twenty
negatives and then timed out on nine of the twelve positives. The fast model answered every case inside
its deadline, said act on seven negatives (every one stopped by the grounding check, none reaching the
keystroke), and said act on nine positives that the same grounding check also refused - because with
today's question neither model quotes the screen, and the grounding check requires a quote. Phase 1's
yes/no shape therefore has to decide what grounding means when there is no free-text reason, and it has
to be re-measured here: the fast model's seven wrong answers on negatives are the number the owner's
decision rests on, and today it is not zero.

## Acceptance, with real numbers

| Row | What it takes to pass | What is there |
| --- | --- | --- |
| At least 20 real cases | The count, and where each screen came from | **32 cases** from **25 distinct sessions**. 24 are `screenTail` fields of the Gateway's own saved turn packages (the last 4,000 characters of the terminal at turn end, captured in June 2026 when the owner's fleet hit its limits repeatedly); 7 are windows of live scrollback read on 2026-09-03 through `GET /sessions/{id}/buffer` with this session's key; 1 is the terminal-rules mission's captured fixture, copied byte for byte and credited. The full table is below. |
| At least half negative | The count, and the three kinds represented | **20 negatives of 32.** Documentation being read: 5. Code written or read: 3. A report about another session: 5. Plus two kinds the brief did not name and the fleet produced: the session's own state but a different situation (a context limit, a warning banner, a local tool timeout): 5; the trigger word inside another token (`4529 warnings`, a commit hash): 2. |
| Runs against the real engine | Name the shared functions; show it is not a second implementation | The runner constructs the production `RuleEvaluator` with a per-case `IRuleEnvironment` and calls `RuleEvaluator.EvaluateAsync`. The evaluator itself then runs `RuleCandidateFilter.Choose`, `RuleAgentContract.BuildPrompt`, `RuleAgentContract.Read`, `RuleCheckRunner.Run` and `RuleReasonGrounding.Check` - because it is the production class, not a copy. The model call is the production `HostedInferenceBrain` with the same 60-second deadline. `ScreenHarnessGuardTests` reads the built harness assembly with Mono.Cecil and asserts it calls `EvaluateAsync`, holds no call to `BuildPrompt` or `Read`, no copy of the prompt's screen delimiter, and no HTTP call of its own; each absence was watched failing first against a probe that added the forbidden call. |
| Reports per model, per case | Answer, right or wrong, time | `phase-0-harness-report.md` beside this file: one table per model, one row per case, with the answer read off the recorded firing, right or wrong, the model call time to one decimal, and the evaluator's outcome. `phase-0-harness-results.json` carries every recorded firing (understanding, decision, reason, grounding statement, check runs) so a verdict can be argued with. |
| The negatives number is prominent | Wrong answers on negatives, as a count | Above every table, per model: **wingman-fast: 7. wingman: 0.** |

## The numbers

| | wingman (thinking, production today) | wingman-fast (the Phase 1 candidate) |
| --- | --- | --- |
| **Wrong answers on negatives** | **0** of 20 | **7** of 20 |
| of those, reached an act (would have typed) | 0 | 0 |
| of those, act stopped by the grounding check | 0 | 7 |
| Wrong answers on positives | 12 of 12 | 12 of 12 |
| of those, timed out at 60 seconds | 9 | 0 |
| of those, act stopped by the grounding check | 1 | 9 |
| of those, refused for naming a check argument that does not exist | 1 | 3 |
| of those, declined | 1 | 0 |
| Right | 20 | 9 |
| Model call time, median | 21.0 s | 12.8 s |
| Model call time, maximum | 60.0 s (the deadline) | 40.1 s |
| Cases the free checks never let through | 0 | 0 |

Both models were run through the SAME contract - today's full JSON question, which asks for an
understanding, a decision, a reason, checks and the text to type. That is what production does today
and it is the baseline Phase 1 changes. The 0.4-second measurement in the plan was of a yes/no
question, which does not exist yet, and nothing here contradicts or confirms it.

## What the harness found, and what each finding means for Phase 1

1. **The grounding check refuses every act, on both models, because the question never asks for a
   quote.** `RuleReasonGrounding.Check` accepts an act only when the stated reason quotes a passage of
   at least eight characters that is on the screen. On the twelve positives the fast model's reasons
   were all paraphrases ("The screen shows a notice that the session limit has been reached and provides
   a reset time") and so was the one act the thinking model managed - every one refused with "the reason
   cites nothing from the screen". So the live engine today would not have typed into any of the ten
   real limit screens or the two real outage screens. Phase 1 moves to a yes/no question with the text
   decided at authoring time; it must say what grounds a yes when there is no free-text reason, or the
   fast model's seven wrong acts on negatives above become seven keystrokes, because grounding was the
   only thing that stopped them.
2. **The fast model says act on negatives it should not - seven of twenty.** Three were the session's
   own state in a different situation (a context limit, a warning banner, a local tool timeout), two
   were reports about other sessions, one was prose, one was code. Its reasons show it pattern-matched
   the words ("The session has stopped on a timeout error, which matches the criteria"). The owner's
   decision to use the fast model was made "with the negative control re-run as the gate"; on this corpus
   the fast model does not pass that gate through today's contract. The yes/no shape may do better or
   worse; it must be run here before Phase 1 is accepted.
3. **The thinking model's timeouts concentrate on the positives.** Nine of twelve positives timed out
   at the 60-second deadline; no negative did (maximum 44.7 s). An act answer is the long one - it
   carries checks and the text to type - and that is the one-in-three failure the mission named,
   measured here as 9 of 32 calls, or 28 percent, all on the answers that matter. Phase 1's shorter
   answer attacks exactly this.
4. **Both models invent check arguments.** Four refusals were "the check `extract_first` cannot look for
   `clock time`" or "`retry_delay_from` needs a value for `now`". The contract refuses these correctly
   and nothing was typed. In Phase 1's design the run-time question names no checks at all, so this goes
   away with the shape; until then it is a fourth way a real limit screen fails to act.
5. **One decline on a positive is a fair reading of the rule's wording, not a model error.** On
   `p08-monthly-spend-limit-mid-bug-group` the thinking model declined because the notice states no reset
   time and the instruction says "wait until the time the notice says it resets". Four of the ten limit
   screens name no reset time. The corpus keeps `act` as the expected answer, with the reason that the
   rule's ceilings bound the wait - but the rule's own sentence should say so, which is an authoring
   matter for the demonstrations, not a harness defect.

## The corpus

`src/CcDirector.Rules.ScreenHarness/corpus/` - `rules.json`, then `cases/<id>/screen.txt` (bytes as
captured; a `.gitattributes` keeps git from converting line endings) and `cases/<id>/case.json` (the
expected answer, the kind, the reason, the facts and how they were established, the source). The
README there explains the format and the kinds.

| Case | Expected | Kind | Source | Session | Captured |
| --- | --- | --- | --- | --- | --- |
| `n01-model-picker-help-text` | decline | negative-documentation | turn package | `214c4665` | 2026-06-11 |
| `n02-prose-about-rate-limiting-as-a-name` | decline | negative-documentation | turn package | `ec3694db` | 2026-06-11 |
| `n03-prose-about-a-competitors-hour-allowance` | decline | negative-documentation | buffer route | `c95fc1ad` | 2026-09-03 |
| `n04-prose-about-a-model-that-timed-out` | decline | negative-documentation | buffer route | `fc78a529` | 2026-09-03 |
| `n05-prose-i-overloaded-it-with-detail` | decline | negative-documentation | turn package | `180aee6b` | (package undated) |
| `n06-diff-adding-a-limit-screen-test-constant` | decline | negative-code | buffer route | `a3f1cb5d` | 2026-09-03 |
| `n07-diff-with-a-fixture-firing-api-error-overloaded` | decline | negative-code | buffer route | `a3f1cb5d` | 2026-09-03 |
| `n08-reading-code-comments-about-allowance` | decline | negative-code | buffer route | `a3f1cb5d` | 2026-09-03 |
| `n10-report-of-a-sub-agent-that-hit-its-spend-limit` | decline | negative-report | turn package | `214c4665` | 2026-06-11 |
| `n11-session-listing-with-weekly-limit-banner` | decline | negative-report | turn package | `4d25106c` | 2026-06-09 |
| `n12-background-command-named-rules-allowance` | decline | negative-report | buffer route | `f1451482` | 2026-09-03 |
| `n13-summary-report-of-a-spawn-timeout-fix` | decline | negative-report | turn package | `23a17225` | 2026-06-06 |
| `n14-log-report-of-a-probe-that-timed-out` | decline | negative-report | turn package | `2b74a9b2` | 2026-06-06 |
| `n15-context-limit-reached-not-allowance` | decline | negative-own-state-different-situation | turn package | `114912f8` | 2026-06-11 |
| `n16-context-limit-with-zero-remaining` | decline | negative-own-state-different-situation | turn package | `c27269a8` | 2026-06-08 |
| `n17-weekly-limit-banner-turn-finished-normally` | decline | negative-own-state-different-situation | turn package | `e6743d9e` | 2026-06-11 |
| `n18-weekly-limit-banner-after-standup` | decline | negative-own-state-different-situation | turn package | `2c64225c` | 2026-06-08 |
| `n19-tool-output-shutdown-post-timed-out` | decline | negative-own-state-different-situation | turn package | `bba187d0` | 2026-06-09 |
| `n21-4529-warnings-in-a-commit-plan` | decline | negative-substring | turn package | `4bd8765d` | (package undated) |
| `n22-commit-hash-containing-529` | decline | negative-substring | turn package | `674dbf6a` | 2026-06-06 |
| `p01-fable-limit-blocked-session` | act | positive | fixture (terminal-rules) | session 101 | 2026-09-02 |
| `p02-session-limit-after-two-hours` | act | positive | turn package | `329c8057` | 2026-06-08 |
| `p03-session-limit-with-options-menu` | act | positive | turn package | `180aee6b` | 2026-06-08 |
| `p04-session-limit-hundred-percent-banner` | act | positive | turn package | `4bd8765d` | 2026-06-08 |
| `p05-session-limit-while-subagent-ran` | act | positive | turn package | `663e3482` | 2026-06-11 |
| `p06-monthly-spend-limit-with-task-list` | act | positive | turn package | `063125d4` | 2026-06-11 |
| `p07-monthly-spend-limit-and-session-banner` | act | positive | turn package | `23a17225` | 2026-06-06 |
| `p08-monthly-spend-limit-mid-bug-group` | act | positive | turn package | `88d3de29` | 2026-06-06 |
| `p09-monthly-spend-limit-with-spend-dialog` | act | positive | turn package | `d7120ba3` | 2026-06-11 |
| `p10-monthly-spend-limit-at-turn-two` | act | positive | turn package | `558c4295` | 2026-06-11 |
| `p11-overloaded-529-after-ten-retries` | act | positive | turn package | `214c4665` | 2026-06-11 |
| `p12-overloaded-529-stopped-live-session` | act | positive | buffer route | `41eb7c07` | 2026-09-03 |

Nothing was composed. Two screens were trimmed from the top to the last 40 non-blank lines - the window
the engine carries into the question - so that their trigger words sit inside it; nothing inside the
window was altered and the case says so. Two packages carry no generation timestamp in their brief line
and are marked undated; their sibling turns in the same packages are dated 8 June 2026.

**One candidate was dropped.** Turn package t364 of session `9fcf02f8` - a stop on `API Error: 403`
with a `Please run /login` notice, which would have been a sharp negative for the outage rule - carried
a pasted sign-in code in the composer. Dropped, not edited, as the brief requires.

**Non-ASCII.** Thirty-one of the thirty-two screens carry non-ASCII bytes (box-drawing rules, the
agent's spinner glyphs, its status bar) because that is what the terminals held; they are kept
faithfully and each case's `nonAscii` flag says so. Every JSON file, every document and every line of
code in this phase is ASCII.

## The rules the corpus is judged against

Three, in `rules.json`, all scoped to every session and stored as dry runs: the allowance rule (the
owner's headline case - wait until the stated reset, then type continue; trigger words `limit`,
`usage credits`, `usage-credits`, `allowance`, `out of credits`), the outage rule (a provider error -
wait a minute, continue, give up for the day; trigger words `API Error`, `overloaded`, `529`,
`connection error`, `timed out`, `rate limit`) and the build-failure rule from the earlier demonstration
(`failed`, `error`), kept because its broad words put it in play on many screens where it must decline.
The corpus test asserts, with the real `RuleCandidateFilter.Choose`, that on every case at least one
rule is chosen on the last 40 non-blank lines - so every negative is a screen the model was actually
asked about.

## The runner, and how it was run

`src/CcDirector.Rules.ScreenHarness/` (in the solution, deliberately not in `scripts/test-local.ps1`
because it calls a live model):

```
dotnet run --project src/CcDirector.Rules.ScreenHarness -- --models wingman,wingman-fast
dotnet run --project src/CcDirector.Rules.ScreenHarness -- --models wingman --case <id>[,<id>...] --out <dir>
dotnet run --project src/CcDirector.Rules.ScreenHarness -- --merge <parent dir of batch runs>
```

Cases run one at a time within a model so each timing is one call under no self-inflicted load. The
fast model's 32 cases ran in one invocation (7 minutes 34 seconds). The thinking model's cannot: a call
may take its full 60-second deadline and a run is never backgrounded, so it ran as four foreground
batches of eight and `--merge` rendered the one report from the four `results.json` files through the
same summary code the single-run path uses. The runner exits 0 only when every case was asked and no
negative was answered with an act on any model; this run exits 1, on the fast model's seven.

Two things a reader needs to run it: a fleet session has `CC_DIRECTOR_ROOT` pointed at an instance
directory whose vault lacks the DevThrottle key, so clear it first (the runner's error names the root it
looked under); and the key it reads is the Gateway's own, from the same vault.

## The gate

`.\scripts\test-local.ps1`, run twice on the merged phase branch with the Postgres rig up:

| Run | Result |
| --- | --- |
| First | Eight suites green; `CcDirector.Gateway.UnitTests` STOPPED at the 120-second ceiling with no test output written (the machine was carrying thirteen fleet sessions). Not a failure in the suite. |
| The suite alone, `--no-build` | 3,547 passed, 0 failed, 2 skipped, 2 minutes 3 seconds |
| Second | Green: 9 suites, every TRX outcome Completed - 160, 3,549, 364, 63, 88, 113, 24, 25, 456 tests; the Gateway unit suite in 1 minute 52 seconds |

The 38 tests this phase adds run in about two seconds; the suite's proximity to the ceiling is the
machine's load today, not this change. The script's coverage note names the two parked suites; this
phase adds no code to the Gateway assembly, only a console project, tests, fixtures and documents, so
they were not run.

## What is NOT proven

- **The screens are cleaned scrollback and turn-package tails, not the rendered grid.** At run time
  the evaluator reads `screen-grid` rows; a session key cannot call that route, and the turn packages
  hold the linear terminal text. The rows the corpus feeds are real, but they are a different rendering
  of the same terminal, and the messiness a reader sees (redraw fragments, status bars repeated) is
  what the linear text holds rather than what the grid shows. Nothing here measures the difference.
- **Every screen is one agent's interface.** No real limit or outage screen from another agent was found
  on this machine's fleet; the trigger words for other agents are untested.
- **The 0.4-second yes/no measurement is untested here.** Both models ran today's full JSON question;
  the yes/no shape does not exist yet and must be measured with this harness when it does.
- **Machine, repository and mission facts are empty on the June captures** - the turn packages do not
  record them - so scope filtering was not exercised; every corpus rule watches all sessions.
- **The Worker's guard rounds were watched by the Worker**, and quoted in its report and commit message
  (probe added, test red naming the probe; probe removed, 22 green); the Manager re-ran the finished
  tests green but did not repeat the red runs.

## Record

- Corpus and its README: `src/CcDirector.Rules.ScreenHarness/corpus/`
- Runner: `src/CcDirector.Rules.ScreenHarness/`
- Tests: `src/CcDirector.Gateway.UnitTests/Rules/ScreenCorpusTests.cs`, `ScreenHarnessJudgementTests.cs`, `ScreenHarnessGuardTests.cs`
- The run: `phase-0-harness-report.md` and `phase-0-harness-results.json` beside this file
- The Worker's task: `phase-0-runner-task.md`
- The Worker was seated in its own worktree on `mission/rules-p0-runner`, merged into the phase branch, and retired; its worktree and branch are gone.
