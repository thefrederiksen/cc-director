# Screen harness report

Run at 2026-09-04 15:21:49 UTC. Every case went through the real RuleEvaluator on every run; the answer is read off the recorded firing. A case's row shows its WORST run and every answer it gave.

## wingman-fast - the numbers

Every count is over all 96 answers (32 cases x 3 runs).

- WRONG ANSWERS ON NEGATIVES: 15 (the number the phase is judged on; 5 negative case(s) wrong on at least one run)
  - of those, reached act (would have typed): 7
  - of those, act (ungrounded) - the grounding check stopped it: 0
  - of those, act - the second question stopped it (not this session's own state): 8
- FLIPS: 1 of 32 cases did not give the same answer on every run - n10-report-of-a-sub-agent-that-hit-its-spend-limit
- timeouts: 0; other no-answers: 0
- wrong answers on positives: 3 (1 positive case(s) wrong on at least one run)
- cases not asked (a corpus defect): 0
- right: 78; wrong: 18; answers: 96
- model call time: median 6.5s, maximum 31.2s

## wingman - the numbers

Every count is over all 96 answers (32 cases x 3 runs).

- WRONG ANSWERS ON NEGATIVES: 0 (the number the phase is judged on; 0 negative case(s) wrong on at least one run)
  - of those, reached act (would have typed): 0
  - of those, act (ungrounded) - the grounding check stopped it: 0
  - of those, act - the second question stopped it (not this session's own state): 0
- FLIPS: 7 of 32 cases did not give the same answer on every run - n22-commit-hash-containing-529, p01-fable-limit-blocked-session, p03-session-limit-with-options-menu, p04-session-limit-hundred-percent-banner, p08-monthly-spend-limit-mid-bug-group, p10-monthly-spend-limit-at-turn-two, p11-overloaded-529-after-ten-retries
- timeouts: 18; other no-answers: 0
- wrong answers on positives: 18 (9 positive case(s) wrong on at least one run)
- cases not asked (a corpus defect): 0
- right: 77; wrong: 19; answers: 96
- model call time: median 16.4s, maximum 80.9s

## wingman-fast - per case

| case | kind | expected | worst answer | right on every run | answers across runs | seconds (median) | outcome of the worst run |
| --- | --- | --- | --- | --- | --- | --- | --- |
| n01-model-picker-help-text | negative-documentation | decline | decline | right | decline x3 | 5.1 | declined: The screen shows a model selection menu, not a notice about usage allowance or session limits. |
| n02-prose-about-rate-limiting-as-a-name | negative-documentation | decline | decline | right | decline x3 | 5.4 | declined: The screen shows the session is still processing and thinking, not stopped on an error notice from the model provider. |
| n03-prose-about-a-competitors-hour-allowance | negative-documentation | decline | decline | right | decline x3 | 4.1 | declined: The screen does not show a notice from the agent about a usage allowance, session limit, or spend limit being used up. |
| n04-prose-about-a-model-that-timed-out | negative-documentation | decline | decline | right | decline x3 | 13.8 | declined: The screen shows a session that is idle and thinking, not stopped on an error notice from its model provider. |
| n05-prose-i-overloaded-it-with-detail | negative-documentation | decline | decline | right | decline x3 | 11.3 | declined: The session is not showing an error notice from its model provider; it is showing a conversation and processing messages. |
| n06-diff-adding-a-limit-screen-test-constant | negative-code | decline | decline | right | decline x3 | 12.2 | declined: The screen shows a code snippet that mentions a limit notice, but the session itself is not in the situation of having stopped due to a usage limit. |
| n07-diff-with-a-fixture-firing-api-error-overloaded | negative-code | decline | act (not its own state) | WRONG | act (not its own state) x3 | 7.8 | not-its-own-state: The line is part of a test fixture or log output, not the session's own state. |
| n08-reading-code-comments-about-allowance | negative-code | decline | decline | right | decline x3 | 3.5 | declined: The screen shows code and test cases, not a notice from the agent about usage allowance. |
| n10-report-of-a-sub-agent-that-hit-its-spend-limit | negative-report | decline | act | WRONG | act (not its own state) x2, act x1 | 8.1 | dry-run: continue |
| n11-session-listing-with-weekly-limit-banner | negative-report | decline | act (not its own state) | WRONG | act (not its own state) x3 | 6.9 | not-its-own-state: The line reports a status banner about an allowance that has not actually stopped this session. |
| n12-background-command-named-rules-allowance | negative-report | decline | decline | right | decline x3 | 2.5 | declined: The screen does not show a notice from the session's agent about a usage, session, or spend limit being used up. |
| n13-summary-report-of-a-spawn-timeout-fix | negative-report | decline | decline | right | decline x3 | 4.7 | declined: The screen shows a code review and commit approval prompt, not an error notice from the model provider. |
| n14-log-report-of-a-probe-that-timed-out | negative-report | decline | decline | right | decline x3 | 3.1 | declined: The screen shows a health check and log analysis, not an error notice from the model provider. |
| n15-context-limit-reached-not-allowance | negative-own-state-different-situation | decline | decline | right | decline x3 | 6.0 | declined: The screen shows the session is running low on context and has used a significant amount of tokens, but it does not show a notice that the usage allowance, sess... |
| n16-context-limit-with-zero-remaining | negative-own-state-different-situation | decline | act | WRONG | act x3 | 4.5 | dry-run: continue |
| n17-weekly-limit-banner-turn-finished-normally | negative-own-state-different-situation | decline | decline | right | decline x3 | 2.8 | declined: The screen shows a notice about usage limits but does not indicate the session has stopped due to using up the allowance. |
| n18-weekly-limit-banner-after-standup | negative-own-state-different-situation | decline | act | WRONG | act x3 | 20.7 | dry-run: continue |
| n19-tool-output-shutdown-post-timed-out | negative-own-state-different-situation | decline | decline | right | decline x3 | 8.1 | declined: The session is not stopped on an error notice from its model provider; it is managing a timeout and process shutdown. |
| n21-4529-warnings-in-a-commit-plan | negative-substring | decline | decline | right | decline x3 | 7.2 | declined: The screen shows a successful build and review, not a build that ended with a compiler error. |
| n22-commit-hash-containing-529 | negative-substring | decline | decline | right | decline x3 | 5.8 | declined: The screen shows the session is still running and processing, not stopped on an error notice from its model provider. |
| p01-fable-limit-blocked-session | positive | act | act | right | act x3 | 7.2 | dry-run: continue |
| p02-session-limit-after-two-hours | positive | act | act | right | act x3 | 6.4 | dry-run: continue |
| p03-session-limit-with-options-menu | positive | act | act | right | act x3 | 6.2 | dry-run: continue |
| p04-session-limit-hundred-percent-banner | positive | act | act | right | act x3 | 11.0 | dry-run: continue |
| p05-session-limit-while-subagent-ran | positive | act | act | right | act x3 | 4.9 | dry-run: continue |
| p06-monthly-spend-limit-with-task-list | positive | act | act | right | act x3 | 8.9 | dry-run: continue |
| p07-monthly-spend-limit-and-session-banner | positive | act | act | right | act x3 | 5.7 | dry-run: continue |
| p08-monthly-spend-limit-mid-bug-group | positive | act | act | right | act x3 | 7.3 | dry-run: continue |
| p09-monthly-spend-limit-with-spend-dialog | positive | act | act (ungrounded) | WRONG | act (ungrounded) x3 | 9.8 | ungrounded: grounding: the answer cites a line this screen does not contain: 'You've hit your monthly spend limit. /usage-credits to adjust your monthly spend limit.'. |
| p10-monthly-spend-limit-at-turn-two | positive | act | act | right | act x3 | 5.2 | dry-run: continue |
| p11-overloaded-529-after-ten-retries | positive | act | act | right | act x3 | 9.7 | dry-run: continue |
| p12-overloaded-529-stopped-live-session | positive | act | act | right | act x3 | 7.5 | dry-run: continue |

## wingman - per case

| case | kind | expected | worst answer | right on every run | answers across runs | seconds (median) | outcome of the worst run |
| --- | --- | --- | --- | --- | --- | --- | --- |
| n01-model-picker-help-text | negative-documentation | decline | decline | right | decline x3 | 29.6 | declined: The screen shows a model-selection menu where the user just set the model to Fable 5; it is not a notice from the agent that the session has stopped because its... |
| n02-prose-about-rate-limiting-as-a-name | negative-documentation | decline | decline | right | decline x3 | 9.7 | declined: The screen shows the session actively working — generating a table of domain name options and discussing naming — not stopped on a usage allowance or spend limi... |
| n03-prose-about-a-competitors-hour-allowance | negative-documentation | decline | decline | right | decline x3 | 11.4 | declined: The screen shows the session actively processing background agents and generating output, not stopped on a notice that its usage allowance or session limit is u... |
| n04-prose-about-a-model-that-timed-out | negative-documentation | decline | decline | right | decline x3 | 13.4 | declined: The session is actively working — shown by the 'Improvising…' and 'thinking' status indicators and a Bash command in 'Waiting…' state — not stopped on a model p... |
| n05-prose-i-overloaded-it-with-detail | negative-documentation | decline | decline | right | decline x3 | 6.3 | declined: The session is actively working (Pollinating…, thinking, 1 shell still running) and shows no error notice from its model provider such as overloaded, connection... |
| n06-diff-adding-a-limit-screen-test-constant | negative-code | decline | decline | right | decline x3 | 10.8 | declined: The session is actively working and writing a test file; the 'Claude usage limit reached' text appears inside a C# string constant in the diff, not as a notice ... |
| n07-diff-with-a-fixture-firing-api-error-overloaded | negative-code | decline | decline | right | decline x3 | 11.2 | declined: The session is actively working (Gallivanting…) and writing test code that merely contains the string 'API Error: overloaded' as test data, not an actual model-... |
| n08-reading-code-comments-about-allowance | negative-code | decline | decline | right | decline x3 | 8.2 | declined: The screen shows code and test content mentioning allowance, not the session's own agent reporting that its usage allowance is used up with a reset time. |
| n10-report-of-a-sub-agent-that-hit-its-spend-limit | negative-report | decline | decline | right | decline x3 | 16.0 | declined: The session is actively running (proving issue #330 live), not stopped on a usage-allowance notice; the spend-limit mention is a past-tense report about a sub-a... |
| n11-session-listing-with-weekly-limit-banner | negative-report | decline | decline | right | decline x3 | 24.1 | declined: The screen shows the session still running with a usage notice at 93%, not stopped because its allowance is used up. |
| n12-background-command-named-rules-allowance | negative-report | decline | decline | right | decline x3 | 7.7 | declined: The session is actively running commands and processing (swirling spinner, executing bash commands, reading files), not stopped on a usage allowance notice with... |
| n13-summary-report-of-a-spawn-timeout-fix | negative-report | decline | decline | right | decline x3 | 6.2 | declined: The screen shows the session awaiting commit approval while nesting/running, not stopped on a model-provider overloaded, connection, or timeout error. |
| n14-log-report-of-a-probe-that-timed-out | negative-report | decline | decline | right | decline x3 | 5.5 | declined: The session has stopped on completed diagnostic work and is awaiting the next user instruction, not on a model provider error such as overloaded or connection f... |
| n15-context-limit-reached-not-allowance | negative-own-state-different-situation | decline | decline | right | decline x3 | 8.5 | declined: The screen shows a context limit reached message ('Context limit reached · /compact or /clear to continue'), which is about the conversation context window bein... |
| n16-context-limit-with-zero-remaining | negative-own-state-different-situation | decline | decline | right | decline x3 | 6.3 | declined: The screen shows a context window limit being reached ('Context limit reached', 'Context low (0% remaining)'), which is about the conversation history filling u... |
| n17-weekly-limit-banner-turn-finished-normally | negative-own-state-different-situation | decline | decline | right | decline x3 | 5.4 | declined: The screen shows the session at 81% of its weekly limit and still actively running (spinners, thinking indicators), not stopped because the allowance is used up... |
| n18-weekly-limit-banner-after-standup | negative-own-state-different-situation | decline | decline | right | decline x3 | 8.2 | declined: The screen shows a usage warning at 86% of the weekly limit while the session is still actively running, not a session that has stopped because its usage allowa... |
| n19-tool-output-shutdown-post-timed-out | negative-own-state-different-situation | decline | decline | right | decline x3 | 6.0 | declined: The session is waiting for a local background install process to complete, not stopped on a model provider error; the timeout mentioned is from a local PowerShe... |
| n21-4529-warnings-in-a-commit-plan | negative-substring | decline | decline | right | decline x3 | 13.4 | declined: The screen shows the build succeeded, not failed — 'Build: clean Release passed, 0 errors' — so there is no compiler error to investigate. |
| n22-commit-hash-containing-529 | negative-substring | decline | no answer | WRONG | decline x2, no answer x1 | 22.1 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p01-fable-limit-blocked-session | positive | act | no answer | WRONG | no answer x2, act x1 | 60.0 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p02-session-limit-after-two-hours | positive | act | act | right | act x3 | 47.3 | dry-run: continue |
| p03-session-limit-with-options-menu | positive | act | no answer | WRONG | act x2, no answer x1 | 46.3 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p04-session-limit-hundred-percent-banner | positive | act | no answer | WRONG | act x2, no answer x1 | 60.0 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p05-session-limit-while-subagent-ran | positive | act | act | right | act x3 | 17.9 | dry-run: continue |
| p06-monthly-spend-limit-with-task-list | positive | act | no answer | WRONG | no answer x3 | 60.0 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p07-monthly-spend-limit-and-session-banner | positive | act | no answer | WRONG | no answer x3 | 60.0 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p08-monthly-spend-limit-mid-bug-group | positive | act | no answer | WRONG | act x2, no answer x1 | 60.0 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p09-monthly-spend-limit-with-spend-dialog | positive | act | no answer | WRONG | no answer x3 | 60.0 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p10-monthly-spend-limit-at-turn-two | positive | act | no answer | WRONG | no answer x2, decline x1 | 60.0 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p11-overloaded-529-after-ten-retries | positive | act | no answer | WRONG | act x2, no answer x1 | 60.0 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p12-overloaded-529-stopped-live-session | positive | act | act | right | act x3 | 42.3 | dry-run: continue |

