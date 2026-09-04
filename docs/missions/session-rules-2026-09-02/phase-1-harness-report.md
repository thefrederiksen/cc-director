# Screen harness report

Run at 2026-09-04 13:49:43 UTC. Every case went through the real RuleEvaluator on every run; the answer is read off the recorded firing. A case's row shows its WORST run and every answer it gave.

## wingman-fast - the numbers

Every count is over all 96 answers (32 cases x 3 runs).

- WRONG ANSWERS ON NEGATIVES: 15 (the number the phase is judged on; 5 negative case(s) wrong on at least one run)
  - of those, reached act (would have typed): 15
  - of those, act (ungrounded) - the grounding check stopped it: 0
- FLIPS: 0 of 32 cases did not give the same answer on every run
- timeouts: 0; other no-answers: 0
- wrong answers on positives: 3 (1 positive case(s) wrong on at least one run)
- cases not asked (a corpus defect): 0
- right: 78; wrong: 18; answers: 96
- model call time: median 3.3s, maximum 52.7s

## wingman - the numbers

Every count is over all 96 answers (32 cases x 3 runs).

- WRONG ANSWERS ON NEGATIVES: 0 (the number the phase is judged on; 0 negative case(s) wrong on at least one run)
  - of those, reached act (would have typed): 0
  - of those, act (ungrounded) - the grounding check stopped it: 0
- FLIPS: 10 of 32 cases did not give the same answer on every run - n04-prose-about-a-model-that-timed-out, n14-log-report-of-a-probe-that-timed-out, n21-4529-warnings-in-a-commit-plan, n22-commit-hash-containing-529, p01-fable-limit-blocked-session, p03-session-limit-with-options-menu, p04-session-limit-hundred-percent-banner, p08-monthly-spend-limit-mid-bug-group, p09-monthly-spend-limit-with-spend-dialog, p12-overloaded-529-stopped-live-session
- timeouts: 20; other no-answers: 0
- wrong answers on positives: 18 (9 positive case(s) wrong on at least one run)
- cases not asked (a corpus defect): 0
- right: 74; wrong: 22; answers: 96
- model call time: median 21.3s, maximum 60.0s

## wingman-fast - per case

| case | kind | expected | worst answer | right on every run | answers across runs | seconds (median) | outcome of the worst run |
| --- | --- | --- | --- | --- | --- | --- | --- |
| n01-model-picker-help-text | negative-documentation | decline | decline | right | decline x3 | 3.0 | declined: The screen shows a model selection menu and does not indicate that the session has stopped due to a usage allowance limit. |
| n02-prose-about-rate-limiting-as-a-name | negative-documentation | decline | decline | right | decline x3 | 3.2 | declined: The screen shows the session is still thinking and processing, not stopped on an error notice from its model provider. |
| n03-prose-about-a-competitors-hour-allowance | negative-documentation | decline | decline | right | decline x3 | 3.5 | declined: The screen does not show a notice from the agent about a usage allowance, session limit, or spend limit being used up. |
| n04-prose-about-a-model-that-timed-out | negative-documentation | decline | decline | right | decline x3 | 3.2 | declined: The screen shows a session that is idle and thinking, not stopped on an error notice from its model provider. |
| n05-prose-i-overloaded-it-with-detail | negative-documentation | decline | decline | right | decline x3 | 3.1 | declined: The session is not showing an error notice from its model provider; it is showing a message about a confusing request and a design discussion. |
| n06-diff-adding-a-limit-screen-test-constant | negative-code | decline | decline | right | decline x3 | 3.2 | declined: The screen shows a code snippet that mentions a limit notice, but the session itself is not in the situation of having stopped due to a usage limit. |
| n07-diff-with-a-fixture-firing-api-error-overloaded | negative-code | decline | act | WRONG | act x3 | 3.2 | dry-run: continue |
| n08-reading-code-comments-about-allowance | negative-code | decline | decline | right | decline x3 | 3.1 | declined: The screen shows a code diff and comments, not a notice from the session's agent about usage allowance. |
| n10-report-of-a-sub-agent-that-hit-its-spend-limit | negative-report | decline | act | WRONG | act x3 | 5.2 | dry-run: continue |
| n11-session-listing-with-weekly-limit-banner | negative-report | decline | act | WRONG | act x3 | 4.3 | dry-run: continue |
| n12-background-command-named-rules-allowance | negative-report | decline | decline | right | decline x3 | 3.3 | declined: The screen shows the session is still active and processing, not stopped due to a usage allowance limit. |
| n13-summary-report-of-a-spawn-timeout-fix | negative-report | decline | decline | right | decline x3 | 6.3 | declined: The session is not showing an error notice from its model provider; it is showing a code review and commit message approval prompt. |
| n14-log-report-of-a-probe-that-timed-out | negative-report | decline | decline | right | decline x3 | 3.4 | declined: The screen shows a health check and log analysis, not an error notice from the model provider. |
| n15-context-limit-reached-not-allowance | negative-own-state-different-situation | decline | decline | right | decline x3 | 2.8 | declined: The screen shows the session is running out of context, but it does not show a notice that the usage allowance, session limit, or spend limit is used up with a ... |
| n16-context-limit-with-zero-remaining | negative-own-state-different-situation | decline | act | WRONG | act x3 | 3.6 | dry-run: continue |
| n17-weekly-limit-banner-turn-finished-normally | negative-own-state-different-situation | decline | decline | right | decline x3 | 3.0 | declined: The screen shows a warning about usage limits but does not indicate that the session has stopped due to using up the allowance. |
| n18-weekly-limit-banner-after-standup | negative-own-state-different-situation | decline | act | WRONG | act x3 | 3.3 | dry-run: continue |
| n19-tool-output-shutdown-post-timed-out | negative-own-state-different-situation | decline | decline | right | decline x3 | 3.0 | declined: The session is not stopped on an error notice from its model provider; it is managing a timeout and process shutdown. |
| n21-4529-warnings-in-a-commit-plan | negative-substring | decline | decline | right | decline x3 | 2.5 | declined: The screen shows a successful build and review, not a build that ended with a compiler error. |
| n22-commit-hash-containing-529 | negative-substring | decline | decline | right | decline x3 | 2.9 | declined: The screen shows the session is still running and processing, not stopped on an error notice from its model provider. |
| p01-fable-limit-blocked-session | positive | act | act | right | act x3 | 3.5 | dry-run: continue |
| p02-session-limit-after-two-hours | positive | act | act | right | act x3 | 3.3 | dry-run: continue |
| p03-session-limit-with-options-menu | positive | act | act | right | act x3 | 3.2 | dry-run: continue |
| p04-session-limit-hundred-percent-banner | positive | act | act | right | act x3 | 10.4 | dry-run: continue |
| p05-session-limit-while-subagent-ran | positive | act | act | right | act x3 | 5.6 | dry-run: continue |
| p06-monthly-spend-limit-with-task-list | positive | act | act | right | act x3 | 6.1 | dry-run: continue |
| p07-monthly-spend-limit-and-session-banner | positive | act | act | right | act x3 | 4.4 | dry-run: continue |
| p08-monthly-spend-limit-mid-bug-group | positive | act | act | right | act x3 | 3.6 | dry-run: continue |
| p09-monthly-spend-limit-with-spend-dialog | positive | act | act (ungrounded) | WRONG | act (ungrounded) x3 | 3.5 | ungrounded: grounding: the answer cites a line this screen does not contain: 'You've hit your monthly spend limit. /usage-credits to adjust your monthly spend limit.'. |
| p10-monthly-spend-limit-at-turn-two | positive | act | act | right | act x3 | 3.5 | dry-run: continue |
| p11-overloaded-529-after-ten-retries | positive | act | act | right | act x3 | 3.9 | dry-run: continue |
| p12-overloaded-529-stopped-live-session | positive | act | act | right | act x3 | 4.6 | dry-run: continue |

## wingman - per case

| case | kind | expected | worst answer | right on every run | answers across runs | seconds (median) | outcome of the worst run |
| --- | --- | --- | --- | --- | --- | --- | --- |
| n01-model-picker-help-text | negative-documentation | decline | decline | right | decline x3 | 17.8 | declined: The screen shows a model selection menu where the user chose Fable 5; there is no notice about usage allowance being used up or any reset time. |
| n02-prose-about-rate-limiting-as-a-name | negative-documentation | decline | decline | right | decline x3 | 12.2 | declined: The screen shows the session actively working and producing output (a domain-name table), not stopped on a usage-allowance or spend-limit notice with a reset ti... |
| n03-prose-about-a-competitors-hour-allowance | negative-documentation | decline | decline | right | decline x3 | 8.9 | declined: The screen shows the session actively working with background agents running and producing output, not stopped on a notice that its usage allowance or session l... |
| n04-prose-about-a-model-that-timed-out | negative-documentation | decline | no answer | WRONG | decline x2, no answer x1 | 58.5 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| n05-prose-i-overloaded-it-with-detail | negative-documentation | decline | decline | right | decline x3 | 12.3 | declined: The screen shows the session actively processing (Pollinating… spinners, thinking indicators, shell still running) with no model provider error such as overload... |
| n06-diff-adding-a-limit-screen-test-constant | negative-code | decline | decline | right | decline x3 | 7.9 | declined: The session is actively working (spinner running, writing a test file), and the words 'Claude usage limit reached' appear only as a string constant inside a cod... |
| n07-diff-with-a-fixture-firing-api-error-overloaded | negative-code | decline | decline | right | decline x3 | 11.5 | declined: The session is actively working (Gallivanting…) and writing test data that merely contains the string 'API Error: overloaded' as a test fixture, not experiencin... |
| n08-reading-code-comments-about-allowance | negative-code | decline | decline | right | decline x3 | 7.8 | declined: The screen shows a code diff being edited with test code and comments that mention 'allowance' as part of the content being written, not the session's own agent... |
| n10-report-of-a-sub-agent-that-hit-its-spend-limit | negative-report | decline | decline | right | decline x3 | 10.3 | declined: The session is actively running (Proving issue #330 live…), not stopped on a usage-allowance notice; the spend-limit mention is a historical report about a DEV ... |
| n11-session-listing-with-weekly-limit-banner | negative-report | decline | decline | right | decline x3 | 35.5 | declined: The session is still actively running (spinners and token counts visible) and has only used 93% of its weekly limit, not used up its allowance, so it has not st... |
| n12-background-command-named-rules-allowance | negative-report | decline | decline | right | decline x3 | 7.2 | declined: The session is actively running commands and thinking, not stopped on a notice about usage allowance being used up. |
| n13-summary-report-of-a-spawn-timeout-fix | negative-report | decline | decline | right | decline x3 | 7.7 | declined: The screen shows the session waiting for commit approval, not stopped on a model-provider overloaded, connection, or timeout error. |
| n14-log-report-of-a-probe-that-timed-out | negative-report | decline | no answer | WRONG | decline x2, no answer x1 | 7.3 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| n15-context-limit-reached-not-allowance | negative-own-state-different-situation | decline | decline | right | decline x3 | 26.6 | declined: The screen shows a context window limit being reached ('Context limit reached'), which is about the conversation's context capacity, not about a usage allowance... |
| n16-context-limit-with-zero-remaining | negative-own-state-different-situation | decline | decline | right | decline x3 | 11.4 | declined: The screen shows the context window limit being reached ('Context low (0% remaining)' and 'Context limit reached'), which is about the conversation's token capa... |
| n17-weekly-limit-banner-turn-finished-normally | negative-own-state-different-situation | decline | decline | right | decline x3 | 9.3 | declined: The session has used only 81% of its weekly limit and is still actively running (showing 'Hatching…' and thinking), not stopped because the allowance is used up... |
| n18-weekly-limit-banner-after-standup | negative-own-state-different-situation | decline | decline | right | decline x3 | 11.2 | declined: The screen shows 86% of the weekly limit used, not a used-up allowance that has stopped the session, and the session is still actively processing a command rath... |
| n19-tool-output-shutdown-post-timed-out | negative-own-state-different-situation | decline | decline | right | decline x3 | 9.0 | declined: The session is actively performing its own work (stopping processes, running an install) and waiting for a background install to complete; the timeouts shown ar... |
| n21-4529-warnings-in-a-commit-plan | negative-substring | decline | no answer | WRONG | decline x2, no answer x1 | 17.9 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| n22-commit-hash-containing-529 | negative-substring | decline | no answer | WRONG | decline x2, no answer x1 | 21.9 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p01-fable-limit-blocked-session | positive | act | no answer | WRONG | no answer x2, decline x1 | 60.0 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p02-session-limit-after-two-hours | positive | act | act | right | act x3 | 23.4 | dry-run: continue |
| p03-session-limit-with-options-menu | positive | act | no answer | WRONG | act x2, no answer x1 | 23.9 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p04-session-limit-hundred-percent-banner | positive | act | act (ungrounded) | WRONG | act x2, act (ungrounded) x1 | 42.3 | ungrounded: grounding: the answer cites a line this screen does not contain: 'You've used 100% of your session limit·resets 10:20am (America/Toronto)·/upgrade to keep using... |
| p05-session-limit-while-subagent-ran | positive | act | act | right | act x3 | 21.6 | dry-run: continue |
| p06-monthly-spend-limit-with-task-list | positive | act | no answer | WRONG | no answer x3 | 60.0 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p07-monthly-spend-limit-and-session-banner | positive | act | no answer | WRONG | no answer x3 | 60.0 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p08-monthly-spend-limit-mid-bug-group | positive | act | no answer | WRONG | act x2, no answer x1 | 43.3 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p09-monthly-spend-limit-with-spend-dialog | positive | act | no answer | WRONG | no answer x2, act x1 | 60.0 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p10-monthly-spend-limit-at-turn-two | positive | act | no answer | WRONG | no answer x3 | 60.0 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p11-overloaded-529-after-ten-retries | positive | act | act | right | act x3 | 17.2 | dry-run: continue |
| p12-overloaded-529-stopped-live-session | positive | act | no answer | WRONG | act x2, no answer x1 | 49.0 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |

