# Phase 1 - the run-time call measured on the new yes-or-no contract

Branch `mission/rules-p1`, worktree `D:/ReposFred/devthrottle-rules-p1`, head `050999e82` with a clean
tree before and after the run. Measured on 2026-09-04; the merged report is stamped 13:49:43 UTC.

The brief is `phase-1-fast-model-brief.md`, and this report answers its ruling P1-B: re-run the phase 0
harness on the new contract, both models, and put both tables here. The baseline being compared against
is `phase-0-report.md`.

**No model is chosen here.** Ruling P1-B says that if the fast model still fails the negatives on the
new contract, the Manager stops and reports, because that is the owner's decision changing on new
evidence and it is his to change. It does. This report is that stop.

---

## The one-paragraph version

The new contract fixes the positives and does not fix the negatives. On the fast model, eleven of the
twelve real limit and outage screens are now answered correctly on every one of three runs - in phase 0
both models were wrong on all twelve - and the median call is 3.3 seconds with no timeouts anywhere in
96 answers. But five of the twenty negatives are answered "act" on every single run, and the grounding
check no longer stops any of them: in phase 0 all seven false acts were refused because the model
paraphrased rather than quoted, and now that the contract asks for one copied line the model supplies a
real one, so **all fifteen of those wrong answers reach the keystroke.** The thinking model still says
act on no negative at all, and it is still slow: median 21.3 seconds, twenty of its 96 calls hit the
60-second deadline with no answer, and it now gets only three of the twelve positives right on every
run. **The shorter question did not make the thinking model fast** - its median moved from 21.0 seconds
to 21.3 - so the timeout the owner was solving for is a property of that model, not of the old contract.

---

## What was run, and how

- The production `RuleEvaluator` through the phase 0 screen harness, unchanged in substance from phase 0:
  the runner constructs the real evaluator with a per-case environment and calls `EvaluateAsync`.
- **The corpus is exactly as phase 0 left it.** Thirty-two cases, twenty negatives, twelve positives. No
  expected answer was changed, no case added, no case dropped; `git status` is clean and no file under
  `src/CcDirector.Rules.ScreenHarness/corpus/` was touched by this run.
- **Both models, three runs per case**: 96 answers per model, 192 in total. Runs are outer and cases
  inner, so a second answer to one screen never follows the first on a warm path.
- The run-time question is the new one: is this that situation, yes or no, plus one line copied from the
  screen, asked at temperature zero, with the text to type taken from the stored rule rather than
  composed by the model.
- Every invocation was in the foreground. The fast model took one invocation per run (all 32 cases,
  about two and a half minutes each). The thinking model cannot: a single call may take its full
  60-second deadline, so it ran as fourteen foreground batches of eight or four cases. The seventeen
  batch directories were merged with the runner's own `--merge`, which renders the summary through the
  same code the single-run path uses, and which refuses to count any answer twice. The merge exits 1, on
  the fast model's wrong negatives.

## The headline numbers

| | wingman (thinking, production today) | wingman-fast (the candidate) |
| --- | --- | --- |
| Answers | 96 (32 cases x 3 runs) | 96 |
| **Wrong answers on negatives - answered act** | **0** of 60 | **15** of 60 |
| negative CASES wrong on at least one run | 4 of 20, every one a timeout, none an act | **5 of 20, every one an act on all three runs** |
| of the wrong acts, stopped by the grounding check | not applicable | **0 of 15 - every one reached the keystroke** |
| Wrong answers on positives | 18 of 36 | 3 of 36 |
| positive CASES wrong on at least one run | 9 of 12 | 1 of 12 |
| Timeouts at the 60-second deadline | **20 of 96 (21 percent)** | **0 of 96** |
| **Flip rate - cases that did not answer the same on every run** | **10 of 32 (31 percent)** | **0 of 32 (0 percent)** |
| Right | 74 of 96 | 78 of 96 |
| **Model call time, median** | **21.3 s** | **3.3 s** |
| 90th percentile | 60.0 s | 11.7 s |
| Mean | 28.2 s | 6.7 s |
| Maximum | 60.0 s | 52.7 s |
| calls over 10 seconds | 73 of 96 | 13 of 96 |
| calls over 30 seconds | 34 of 96 | 5 of 96 |

Medians, counts and the flip rate are the harness's own; the percentile, the mean and the over-10 and
over-30 counts were computed from the same merged results file.

## The latency question the owner asked, answered

The seed of this measurement put it plainly: if the thinking model is now both correct on negatives and
fast enough under a contract that asks only for a yes or no plus one copied line, then the timeout was a
property of the contract and the owner's decision may not need reopening at all. **It is not.**

| | phase 0, the old 600-character contract | this run, the new yes-or-no contract |
| --- | --- | --- |
| thinking model, median call | 21.0 s | 21.3 s |
| thinking model, calls that timed out | 9 of 32 (28 percent) | 20 of 96 (21 percent) |
| fast model, median call | 12.8 s | 3.3 s |

The shorter question made the fast model nearly four times faster and did almost nothing for the
thinking model. A rule that fires on a stopped session and waits 21 seconds at the median, with one call
in five never answering at all, is the same failure the mission named at the start. **The decision is
open on the negatives, not on latency.**

## The gate, and why the fast model fails it

The gate is zero wrong answers on negatives. The fast model gives fifteen, on five cases, on every run:

| Case | What the screen really is | What the model copied off it |
| --- | --- | --- |
| `n07-diff-with-a-fixture-firing-api-error-overloaded` | a code diff being written, with a test fixture in it | `API Error: overloaded` |
| `n10-report-of-a-sub-agent-that-hit-its-spend-limit` | a report about a DIFFERENT session that hit a spend limit | a sentence of that report |
| `n11-session-listing-with-weekly-limit-banner` | a running session whose banner says 93 percent used | that banner line, which says 93 percent used |
| `n16-context-limit-with-zero-remaining` | a context limit, which is a different situation | `Context low (0% remaining)` |
| `n18-weekly-limit-banner-after-standup` | a running session whose banner says 86 percent used | that banner line, which says 86 percent used |

Each of these is a real line, really on that screen, so **the citation field does its job and the
grounding check passes them.** In phase 0 the fast model's seven false acts were all refused because it
paraphrased instead of quoting; the safety net that hid them is gone by design, and what is left is the
judgement itself. Two of the five are the same mistake twice - a percentage banner on a session that has
not stopped, read as an allowance that is used up - and one is the mission's own recurring confusion
between a context limit and a usage allowance.

The thinking model answers act on no negative in 60 chances, as in phase 0. Its four wrong negatives are
all the same failure: no answer inside 60 seconds. That is a safe failure on a negative and an unusable
one on a positive.

## The positives, which now matter

The fast model gets eleven of twelve right on every run, against zero of twelve for both models in phase
0. That is the single largest change the new contract makes, and it is the row the phase existed to fix.

Its one failure, `p09-monthly-spend-limit-with-spend-dialog`, is wrong on all three runs and is worth
reading before anything is concluded from it. The model's citation is `You've hit your monthly spend
limit. /usage-credits to adjust your monthly spend limit.` The screen carries that text with **no space
after the full stop** - the terminal redraw joined two lines - so the grounding check refuses a citation
that differs from the screen by one inserted space. The act was right and the evidence was real; a
whitespace difference the normaliser does not cover turned it into a refusal. **This is written down, not
acted on**: the corpus is frozen, and this is a note for whoever rules on the normaliser, not a change
made by the person being measured.

The thinking model gets three of twelve right on every run (`p02`, `p05`, `p11`). Sixteen of its 36
answers on positives are timeouts.

## The flip rate, as its own number

- fast model: **0 of 32 cases flipped.** Every case gave the same answer three times out of three, right
  or wrong. At temperature zero it is deterministic on this corpus.
- thinking model: **10 of 32 cases flipped (31 percent)** - `n04`, `n14`, `n21`, `n22`, `p01`, `p03`,
  `p04`, `p08`, `p09`, `p12`. Every one of those flips involves a timeout on at least one run, so the
  instability is the deadline rather than a changing judgement.

A single run of either model would have been misleading. On the thinking model run 1 scored 24 of 32 and
runs 2 and 3 scored 25 - a case-level answer that changes with the weather. On the fast model every run
scored 26 of 32, with the same six cases wrong each time.

## Both tables

The verbatim reasons and citations, and every recorded firing, are in `phase-1-harness-results.json` and
`phase-1-harness-report.md` beside this file. The tables below are those same rows with the free-text
column reduced to the outcome, because this document is written in plain ASCII and the captured screens
are not.

### wingman-fast - per case, worst answer across three runs

| case | kind | expected | worst answer | right on every run | answers across runs | seconds (median) | outcome of the worst run |
| --- | --- | --- | --- | --- | --- | --- | --- |
| n01-model-picker-help-text | negative-documentation | decline | decline | right | decline x3 | 3.0 | declined |
| n02-prose-about-rate-limiting-as-a-name | negative-documentation | decline | decline | right | decline x3 | 3.2 | declined |
| n03-prose-about-a-competitors-hour-allowance | negative-documentation | decline | decline | right | decline x3 | 3.5 | declined |
| n04-prose-about-a-model-that-timed-out | negative-documentation | decline | decline | right | decline x3 | 3.2 | declined |
| n05-prose-i-overloaded-it-with-detail | negative-documentation | decline | decline | right | decline x3 | 3.1 | declined |
| n06-diff-adding-a-limit-screen-test-constant | negative-code | decline | decline | right | decline x3 | 3.2 | declined |
| n07-diff-with-a-fixture-firing-api-error-overloaded | negative-code | decline | act | WRONG | act x3 | 3.2 | dry-run - would type: continue |
| n08-reading-code-comments-about-allowance | negative-code | decline | decline | right | decline x3 | 3.1 | declined |
| n10-report-of-a-sub-agent-that-hit-its-spend-limit | negative-report | decline | act | WRONG | act x3 | 5.2 | dry-run - would type: continue |
| n11-session-listing-with-weekly-limit-banner | negative-report | decline | act | WRONG | act x3 | 4.3 | dry-run - would type: continue |
| n12-background-command-named-rules-allowance | negative-report | decline | decline | right | decline x3 | 3.3 | declined |
| n13-summary-report-of-a-spawn-timeout-fix | negative-report | decline | decline | right | decline x3 | 6.3 | declined |
| n14-log-report-of-a-probe-that-timed-out | negative-report | decline | decline | right | decline x3 | 3.4 | declined |
| n15-context-limit-reached-not-allowance | negative-own-state-different-situation | decline | decline | right | decline x3 | 2.8 | declined |
| n16-context-limit-with-zero-remaining | negative-own-state-different-situation | decline | act | WRONG | act x3 | 3.6 | dry-run - would type: continue |
| n17-weekly-limit-banner-turn-finished-normally | negative-own-state-different-situation | decline | decline | right | decline x3 | 3.0 | declined |
| n18-weekly-limit-banner-after-standup | negative-own-state-different-situation | decline | act | WRONG | act x3 | 3.3 | dry-run - would type: continue |
| n19-tool-output-shutdown-post-timed-out | negative-own-state-different-situation | decline | decline | right | decline x3 | 3.0 | declined |
| n21-4529-warnings-in-a-commit-plan | negative-substring | decline | decline | right | decline x3 | 2.5 | declined |
| n22-commit-hash-containing-529 | negative-substring | decline | decline | right | decline x3 | 2.9 | declined |
| p01-fable-limit-blocked-session | positive | act | act | right | act x3 | 3.5 | dry-run - would type: continue |
| p02-session-limit-after-two-hours | positive | act | act | right | act x3 | 3.3 | dry-run - would type: continue |
| p03-session-limit-with-options-menu | positive | act | act | right | act x3 | 3.2 | dry-run - would type: continue |
| p04-session-limit-hundred-percent-banner | positive | act | act | right | act x3 | 10.4 | dry-run - would type: continue |
| p05-session-limit-while-subagent-ran | positive | act | act | right | act x3 | 5.6 | dry-run - would type: continue |
| p06-monthly-spend-limit-with-task-list | positive | act | act | right | act x3 | 6.1 | dry-run - would type: continue |
| p07-monthly-spend-limit-and-session-banner | positive | act | act | right | act x3 | 4.4 | dry-run - would type: continue |
| p08-monthly-spend-limit-mid-bug-group | positive | act | act | right | act x3 | 3.6 | dry-run - would type: continue |
| p09-monthly-spend-limit-with-spend-dialog | positive | act | act (ungrounded) | WRONG | act (ungrounded) x3 | 3.5 | ungrounded - the cited line is not on the screen |
| p10-monthly-spend-limit-at-turn-two | positive | act | act | right | act x3 | 3.5 | dry-run - would type: continue |
| p11-overloaded-529-after-ten-retries | positive | act | act | right | act x3 | 3.9 | dry-run - would type: continue |
| p12-overloaded-529-stopped-live-session | positive | act | act | right | act x3 | 4.6 | dry-run - would type: continue |

### wingman - per case, worst answer across three runs

| case | kind | expected | worst answer | right on every run | answers across runs | seconds (median) | outcome of the worst run |
| --- | --- | --- | --- | --- | --- | --- | --- |
| n01-model-picker-help-text | negative-documentation | decline | decline | right | decline x3 | 17.8 | declined |
| n02-prose-about-rate-limiting-as-a-name | negative-documentation | decline | decline | right | decline x3 | 12.2 | declined |
| n03-prose-about-a-competitors-hour-allowance | negative-documentation | decline | decline | right | decline x3 | 8.9 | declined |
| n04-prose-about-a-model-that-timed-out | negative-documentation | decline | no answer | WRONG | decline x2, no answer x1 | 58.5 | refused - no answer within the 60 second deadline |
| n05-prose-i-overloaded-it-with-detail | negative-documentation | decline | decline | right | decline x3 | 12.3 | declined |
| n06-diff-adding-a-limit-screen-test-constant | negative-code | decline | decline | right | decline x3 | 7.9 | declined |
| n07-diff-with-a-fixture-firing-api-error-overloaded | negative-code | decline | decline | right | decline x3 | 11.5 | declined |
| n08-reading-code-comments-about-allowance | negative-code | decline | decline | right | decline x3 | 7.8 | declined |
| n10-report-of-a-sub-agent-that-hit-its-spend-limit | negative-report | decline | decline | right | decline x3 | 10.3 | declined |
| n11-session-listing-with-weekly-limit-banner | negative-report | decline | decline | right | decline x3 | 35.5 | declined |
| n12-background-command-named-rules-allowance | negative-report | decline | decline | right | decline x3 | 7.2 | declined |
| n13-summary-report-of-a-spawn-timeout-fix | negative-report | decline | decline | right | decline x3 | 7.7 | declined |
| n14-log-report-of-a-probe-that-timed-out | negative-report | decline | no answer | WRONG | decline x2, no answer x1 | 7.3 | refused - no answer within the 60 second deadline |
| n15-context-limit-reached-not-allowance | negative-own-state-different-situation | decline | decline | right | decline x3 | 26.6 | declined |
| n16-context-limit-with-zero-remaining | negative-own-state-different-situation | decline | decline | right | decline x3 | 11.4 | declined |
| n17-weekly-limit-banner-turn-finished-normally | negative-own-state-different-situation | decline | decline | right | decline x3 | 9.3 | declined |
| n18-weekly-limit-banner-after-standup | negative-own-state-different-situation | decline | decline | right | decline x3 | 11.2 | declined |
| n19-tool-output-shutdown-post-timed-out | negative-own-state-different-situation | decline | decline | right | decline x3 | 9.0 | declined |
| n21-4529-warnings-in-a-commit-plan | negative-substring | decline | no answer | WRONG | decline x2, no answer x1 | 17.9 | refused - no answer within the 60 second deadline |
| n22-commit-hash-containing-529 | negative-substring | decline | no answer | WRONG | decline x2, no answer x1 | 21.9 | refused - no answer within the 60 second deadline |
| p01-fable-limit-blocked-session | positive | act | no answer | WRONG | no answer x2, decline x1 | 60.0 | refused - no answer within the 60 second deadline |
| p02-session-limit-after-two-hours | positive | act | act | right | act x3 | 23.4 | dry-run - would type: continue |
| p03-session-limit-with-options-menu | positive | act | no answer | WRONG | act x2, no answer x1 | 23.9 | refused - no answer within the 60 second deadline |
| p04-session-limit-hundred-percent-banner | positive | act | act (ungrounded) | WRONG | act x2, act (ungrounded) x1 | 42.3 | ungrounded - the cited line is not on the screen |
| p05-session-limit-while-subagent-ran | positive | act | act | right | act x3 | 21.6 | dry-run - would type: continue |
| p06-monthly-spend-limit-with-task-list | positive | act | no answer | WRONG | no answer x3 | 60.0 | refused - no answer within the 60 second deadline |
| p07-monthly-spend-limit-and-session-banner | positive | act | no answer | WRONG | no answer x3 | 60.0 | refused - no answer within the 60 second deadline |
| p08-monthly-spend-limit-mid-bug-group | positive | act | no answer | WRONG | act x2, no answer x1 | 43.3 | refused - no answer within the 60 second deadline |
| p09-monthly-spend-limit-with-spend-dialog | positive | act | no answer | WRONG | no answer x2, act x1 | 60.0 | refused - no answer within the 60 second deadline |
| p10-monthly-spend-limit-at-turn-two | positive | act | no answer | WRONG | no answer x3 | 60.0 | refused - no answer within the 60 second deadline |
| p11-overloaded-529-after-ten-retries | positive | act | act | right | act x3 | 17.2 | dry-run - would type: continue |
| p12-overloaded-529-stopped-live-session | positive | act | no answer | WRONG | act x2, no answer x1 | 49.0 | refused - no answer within the 60 second deadline |

## What this run does NOT prove

- **It does not run a Director and it types nothing.** As in phase 0, the harness measures the judgement,
  not the keystroke. A "dry-run" outcome means the evaluator reached the point of typing and recorded the
  stored text; the typing end is proven by the demonstrations, not here.
- **It does not read the rendered grid.** The corpus screens are turn-package tails and cleaned
  scrollback, which are a different rendering of the same terminal from what `ReadRuleScreenAsync` sees at
  run time. `p09` is a live example of that difference mattering.
- **It measures one machine's network on one afternoon.** The call times include the hosted round trip.
  The relative shape - one model four times faster than the other and one of them timing out one call in
  five - is the finding; the absolute seconds are of that afternoon.
- **The corpus rules all watch every session**, so no scope filtering was exercised, and every screen is
  one agent's interface. Both limits are inherited from phase 0 and unchanged.
- **The local gate was not re-run for this measurement.** No code changed in this session; the head
  measured is the head that was already committed and pushed.

## How to reproduce it

```
dotnet run --project src/CcDirector.Rules.ScreenHarness -- --models wingman-fast --runs 1 --first-run <1|2|3> --out <dir>
dotnet run --project src/CcDirector.Rules.ScreenHarness -- --models wingman --runs 1 --first-run <1|2|3> --case <eight ids> --out <dir>
dotnet run --project src/CcDirector.Rules.ScreenHarness -- --merge <parent of those dirs>
```

Clear `CC_DIRECTOR_ROOT` first: a fleet session points it at an instance directory whose vault does not
hold the key the runner needs, and the runner's error names the root it looked under.
