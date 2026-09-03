# Screen harness report

Run at 2026-09-03 17:58:33 UTC. Every case went through the real RuleEvaluator; the answer is read off the recorded firing.

## wingman-fast - the numbers

- WRONG ANSWERS ON NEGATIVES: 7 (the number the phase is judged on)
  - of those, reached act (would have typed): 0
  - of those, act (ungrounded) - the grounding check stopped it: 7
- timeouts: 0; other no-answers: 0
- wrong answers on positives: 12
- cases not asked (a corpus defect): 0
- right: 9; wrong: 23; cases: 32
- model call time: median 12.8s, maximum 40.1s

## wingman - the numbers

- WRONG ANSWERS ON NEGATIVES: 0 (the number the phase is judged on)
  - of those, reached act (would have typed): 0
  - of those, act (ungrounded) - the grounding check stopped it: 0
- timeouts: 9; other no-answers: 0
- wrong answers on positives: 12
- cases not asked (a corpus defect): 0
- right: 20; wrong: 12; cases: 32
- model call time: median 21.0s, maximum 60.0s

## wingman-fast - per case

| case | kind | expected | answer | right | seconds | outcome |
| --- | --- | --- | --- | --- | --- | --- |
| n01-model-picker-help-text | negative-documentation | decline | refused | WRONG | 16.5 | refused: the agent's answer names the instruction '', which is not an instruction id at all. |
| n02-prose-about-rate-limiting-as-a-name | negative-documentation | decline | act (ungrounded) | WRONG | 40.1 | ungrounded: grounding: the reason cites nothing from the screen, so there is nothing on it that can be checked. Nothing it said was contradicted; nothing it said was verifi... |
| n03-prose-about-a-competitors-hour-allowance | negative-documentation | decline | decline | right | 19.0 | declined: The screen does not contain a notice about the usage allowance being used up or any time at which it resets. |
| n04-prose-about-a-model-that-timed-out | negative-documentation | decline | decline | right | 11.9 | declined: The screen does not show a build ending with a compiler error; it is a discussion about pricing and performance issues. |
| n05-prose-i-overloaded-it-with-detail | negative-documentation | decline | abandoned | WRONG | 20.1 | abandoned: the check elapsed_since(first_failure=<first_failure>, now=<now>) could not be run (this check needs to know when the trouble first appeared, and nothing is tra... |
| n06-diff-adding-a-limit-screen-test-constant | negative-code | decline | decline | right | 17.0 | declined: The screen does not contain a notice from the session's agent about a usage limit being reached; it is discussing such a notice in the context of a test case. |
| n07-diff-with-a-fixture-firing-api-error-overloaded | negative-code | decline | act (ungrounded) | WRONG | 19.2 | ungrounded: grounding: the reason cites nothing from the screen, so there is nothing on it that can be checked. Nothing it said was contradicted; nothing it said was verifi... |
| n08-reading-code-comments-about-allowance | negative-code | decline | decline | right | 24.1 | declined: The screen does not show a notice from the session's agent about a usage allowance being used up. It appears to be discussing or coding around the concept of al... |
| n10-report-of-a-sub-agent-that-hit-its-spend-limit | negative-report | decline | act (ungrounded) | WRONG | 15.2 | ungrounded: grounding: the reason cites nothing from the screen, so there is nothing on it that can be checked. Nothing it said was contradicted; nothing it said was verifi... |
| n11-session-listing-with-weekly-limit-banner | negative-report | decline | act (ungrounded) | WRONG | 9.8 | ungrounded: grounding: the reason cites nothing from the screen, so there is nothing on it that can be checked. Nothing it said was contradicted; nothing it said was verifi... |
| n12-background-command-named-rules-allowance | negative-report | decline | decline | right | 13.5 | declined: The session has not stopped due to a usage allowance or any limits being reached. It is still active and processing commands. |
| n13-summary-report-of-a-spawn-timeout-fix | negative-report | decline | decline | right | 9.6 | declined: The session has not stopped due to an error from the model provider; it is waiting for commit approval. |
| n14-log-report-of-a-probe-that-timed-out | negative-report | decline | decline | right | 12.0 | declined: The screen does not show an error notice from the model provider; instead, it shows successful health checks and log entries indicating the service is functioni... |
| n15-context-limit-reached-not-allowance | negative-own-state-different-situation | decline | decline | right | 13.2 | declined: The screen does not show a notice that the usage allowance, session limit, or spend limit is used up, only that the context is low. |
| n16-context-limit-with-zero-remaining | negative-own-state-different-situation | decline | act (ungrounded) | WRONG | 11.0 | ungrounded: grounding: the reason cites nothing from the screen, so there is nothing on it that can be checked. Nothing it said was contradicted; nothing it said was verifi... |
| n17-weekly-limit-banner-turn-finished-normally | negative-own-state-different-situation | decline | act (ungrounded) | WRONG | 13.5 | ungrounded: grounding: the reason cites nothing from the screen, so there is nothing on it that can be checked. Nothing it said was contradicted; nothing it said was verifi... |
| n18-weekly-limit-banner-after-standup | negative-own-state-different-situation | decline | refused | WRONG | 11.4 | refused: the agent asked for a check that cannot be run: 'kind' on the check 'extract_first' cannot look for 'clock time'. It can look for: path, duration, timestamp. |
| n19-tool-output-shutdown-post-timed-out | negative-own-state-different-situation | decline | act (ungrounded) | WRONG | 16.2 | ungrounded: grounding: the reason cites nothing from the screen, so there is nothing on it that can be checked. Nothing it said was contradicted; nothing it said was verifi... |
| n21-4529-warnings-in-a-commit-plan | negative-substring | decline | refused | WRONG | 9.5 | refused: the agent's answer names the instruction '', which is not an instruction id at all. |
| n22-commit-hash-containing-529 | negative-substring | decline | decline | right | 7.8 | declined: The screen does not show an error notice from the model provider, but rather a long-running process or idle state. |
| p01-fable-limit-blocked-session | positive | act | refused | WRONG | 11.2 | refused: the agent asked for a check that cannot be run: 'kind' on the check 'extract_first' cannot look for 'clock time'. It can look for: path, duration, timestamp. |
| p02-session-limit-after-two-hours | positive | act | act (ungrounded) | WRONG | 11.2 | ungrounded: grounding: the reason cites nothing from the screen, so there is nothing on it that can be checked. Nothing it said was contradicted; nothing it said was verifi... |
| p03-session-limit-with-options-menu | positive | act | act (ungrounded) | WRONG | 14.5 | ungrounded: grounding: the reason cites nothing from the screen, so there is nothing on it that can be checked. Nothing it said was contradicted; nothing it said was verifi... |
| p04-session-limit-hundred-percent-banner | positive | act | act (ungrounded) | WRONG | 12.0 | ungrounded: grounding: the reason cites nothing from the screen, so there is nothing on it that can be checked. Nothing it said was contradicted; nothing it said was verifi... |
| p05-session-limit-while-subagent-ran | positive | act | refused | WRONG | 8.4 | refused: the agent asked for a check that cannot be run: 'kind' on the check 'extract_first' cannot look for 'clock time'. It can look for: path, duration, timestamp. |
| p06-monthly-spend-limit-with-task-list | positive | act | act (ungrounded) | WRONG | 10.4 | ungrounded: grounding: the reason cites nothing from the screen, so there is nothing on it that can be checked. Nothing it said was contradicted; nothing it said was verifi... |
| p07-monthly-spend-limit-and-session-banner | positive | act | act (ungrounded) | WRONG | 16.9 | ungrounded: grounding: the reason cites nothing from the screen, so there is nothing on it that can be checked. Nothing it said was contradicted; nothing it said was verifi... |
| p08-monthly-spend-limit-mid-bug-group | positive | act | act (ungrounded) | WRONG | 14.2 | ungrounded: grounding: the reason cites nothing from the screen, so there is nothing on it that can be checked. Nothing it said was contradicted; nothing it said was verifi... |
| p09-monthly-spend-limit-with-spend-dialog | positive | act | act (ungrounded) | WRONG | 8.7 | ungrounded: grounding: the reason cites nothing from the screen, so there is nothing on it that can be checked. Nothing it said was contradicted; nothing it said was verifi... |
| p10-monthly-spend-limit-at-turn-two | positive | act | refused | WRONG | 13.4 | refused: the agent asked for a check that cannot be run: the check 'retry_delay_from' needs a value for 'now' and was not given one. |
| p11-overloaded-529-after-ten-retries | positive | act | act (ungrounded) | WRONG | 6.9 | ungrounded: grounding: the reason cites nothing from the screen, so there is nothing on it that can be checked. Nothing it said was contradicted; nothing it said was verifi... |
| p12-overloaded-529-stopped-live-session | positive | act | act (ungrounded) | WRONG | 12.4 | ungrounded: grounding: the reason cites nothing from the screen, so there is nothing on it that can be checked. Nothing it said was contradicted; nothing it said was verifi... |

## wingman - per case

| case | kind | expected | answer | right | seconds | outcome |
| --- | --- | --- | --- | --- | --- | --- |
| n01-model-picker-help-text | negative-documentation | decline | decline | right | 33.8 | declined: This is not a notice from the session's own agent that a usage allowance, session limit, or spend limit has been used up with a reset time; it is a successful m... |
| n02-prose-about-rate-limiting-as-a-name | negative-documentation | decline | decline | right | 14.3 | declined: The screen shows the session has finished producing output (a domain comparison table) and is sitting idle at a prompt. There is no notice from the agent saying... |
| n03-prose-about-a-competitors-hour-allowance | negative-documentation | decline | decline | right | 16.7 | declined: Words like 'allowance,' 'hard cap,' and 'pricing' appear in the content being written (a comparison of a product's paid tier), not in a notice from the agent re... |
| n04-prose-about-a-model-that-timed-out | negative-documentation | decline | decline | right | 8.2 | declined: The screen shows a session that is actively generating content about model pricing, not one that has stopped on a model-provider error such as overloaded or con... |
| n05-prose-i-overloaded-it-with-detail | negative-documentation | decline | decline | right | 12.9 | declined: The instruction is watching for a session that has stopped on a model provider error (overloaded, connection failure, timeout, etc.). The screen shows the oppos... |
| n06-diff-adding-a-limit-screen-test-constant | negative-code | decline | decline | right | 7.7 | declined: The usage-limit text appears only inside a file the session is writing (a test fixture string), not as the session's own notice that it has run out of allowance... |
| n07-diff-with-a-fixture-firing-api-error-overloaded | negative-code | decline | decline | right | 7.8 | declined: The words 'API Error: overloaded' appear only as literal test data the session is writing into a file, not as the session's own report that its model provider r... |
| n08-reading-code-comments-about-allowance | negative-code | decline | decline | right | 20.5 | declined: The instruction is about the session's own agent reporting it is out of usage allowance and giving a reset time. Here, 'allowance' appears in code comments desc... |
| n10-report-of-a-sub-agent-that-hit-its-spend-limit | negative-report | decline | decline | right | 15.2 | declined: The instruction watches for the session itself being stopped by its own agent on a current usage/spend-limit notice with a reset time. Here the session is still... |
| n11-session-listing-with-weekly-limit-banner | negative-report | decline | decline | right | 22.5 | declined: The session has not stopped on a usage-allowance-exhausted notice. It is at 93%, not 100%, and the 'resets Jun 13' text is a passive status bar indicator — not ... |
| n12-background-command-named-rules-allowance | negative-report | decline | decline | right | 11.2 | declined: The instruction applies only when the session stops on a notice from its own agent that its usage allowance, session limit, or spend limit is used up. This scre... |
| n13-summary-report-of-a-spawn-timeout-fix | negative-report | decline | decline | right | 8.0 | declined: The instruction applies only when the session stopped on a model-provider error (overloaded, connection failure, timeout). This screen shows finished work await... |
| n14-log-report-of-a-probe-that-timed-out | negative-report | decline | decline | right | 9.0 | declined: The instruction watches for a session stopped on a model-provider error (overloaded, connection failure, timeout). This screen shows the session has completed i... |
| n15-context-limit-reached-not-allowance | negative-own-state-different-situation | decline | decline | right | 6.8 | declined: The screen shows a context window limit being reached, not a usage allowance, session limit, or spend limit being used up. There is no reset time mentioned, and... |
| n16-context-limit-with-zero-remaining | negative-own-state-different-situation | decline | decline | right | 13.0 | declined: The instruction targets a usage/session/spend allowance exhaustion notice with a reset time, but this screen shows a context limit instead, with no allowance re... |
| n17-weekly-limit-banner-turn-finished-normally | negative-own-state-different-situation | decline | decline | right | 7.4 | declined: The instruction applies when a session has stopped because its usage allowance is used up. Here the session is at 81% (not used up) and is still actively workin... |
| n18-weekly-limit-banner-after-standup | negative-own-state-different-situation | decline | decline | right | 21.5 | declined: The instruction is for when a session has stopped because its usage allowance is used up. Here the notice says 86% used, not 100%/used up, and the session is si... |
| n19-tool-output-shutdown-post-timed-out | negative-own-state-different-situation | decline | decline | right | 11.2 | declined: The instruction watches for a session stopped on a model provider error (overloaded, connection failure, server-side timeout). This session is not stopped on su... |
| n21-4529-warnings-in-a-commit-plan | negative-substring | decline | decline | right | 32.9 | declined: The session stopped on its own finished work — asking the user to approve a commit — not on a model-provider error such as overloaded, connection failure, or ti... |
| n22-commit-hash-containing-529 | negative-substring | decline | decline | right | 44.7 | declined: The session stopped because it finished its own work successfully, not because its model provider returned an overloaded, connection, or timeout error. The scre... |
| p01-fable-limit-blocked-session | positive | act | no answer | WRONG | 60.0 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p02-session-limit-after-two-hours | positive | act | refused | WRONG | 19.3 | refused: the agent asked for a check that cannot be run: 'kind' on the check 'extract_first' cannot look for 'clock time'. It can look for: path, duration, timestamp. |
| p03-session-limit-with-options-menu | positive | act | no answer | WRONG | 60.0 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p04-session-limit-hundred-percent-banner | positive | act | no answer | WRONG | 60.0 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p05-session-limit-while-subagent-ran | positive | act | no answer | WRONG | 60.0 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p06-monthly-spend-limit-with-task-list | positive | act | no answer | WRONG | 60.0 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p07-monthly-spend-limit-and-session-banner | positive | act | act (ungrounded) | WRONG | 44.6 | ungrounded: grounding: the reason cites nothing from the screen, so there is nothing on it that can be checked. Nothing it said was contradicted; nothing it said was verifi... |
| p08-monthly-spend-limit-mid-bug-group | positive | act | decline | WRONG | 53.4 | declined: The notice confirms the spend limit is used up, but it does not state a time at which the limit resets, so the instruction to wait until that reset time and the... |
| p09-monthly-spend-limit-with-spend-dialog | positive | act | no answer | WRONG | 60.0 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p10-monthly-spend-limit-at-turn-two | positive | act | no answer | WRONG | 60.0 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p11-overloaded-529-after-ten-retries | positive | act | no answer | WRONG | 60.0 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |
| p12-overloaded-529-stopped-live-session | positive | act | no answer | WRONG | 60.0 | refused: TimeoutException: The wingman model call did not answer within 60 seconds. |

