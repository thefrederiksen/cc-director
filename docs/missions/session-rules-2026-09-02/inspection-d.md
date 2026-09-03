# Inspection D - authoring by conversation

## Verdict

FAIL. Ten findings, ordered worst first. The worst is a product-level break: the new command-line
surface authenticates with a session key, while the session-key allowlist authorizes none of the
rule routes. Every command that reads, authors, stores, or deletes rules is refused before the rule
endpoint runs; only the separate screen-read command uses an already-allowed session route.

Inspected authoring commits `3eac5b70b` and `6749630d9`, the mission record through this report's
parent, and the two pull request bodies. I read the mission brief, phase plan, implementation plan,
authoring report, Gateway authoring code and tests, route and tenancy code, both clients and their
tests, and the session-key authentication boundary. Per the inspection brief, I did not rerun the
suites; claims about green and red runs below are judged from the durable evidence in the branch and
pull request bodies.

## 1. BLOCKER - every command-line rule operation except screen read is denied by authentication

What is wrong: `RuleClient` obtains `gateway.session_key()` and sends it as its bearer credential
(`tools/cc-devthrottle/src/rule_ops.py:59-67`). It then calls these route shapes:

- `GET /gateway/rules`
- `GET /gateway/rules/{id}`
- `GET /gateway/rules/{id}/firings`
- `POST /gateway/rules/draft`
- `POST /gateway/rules`
- `DELETE /gateway/rules/{id}`

`SessionKeyGuard.IsAllowed` contains no rule-route case in its read, POST, or DELETE branches
(`src/CcDirector.Gateway/Util/SessionKeyGuard.cs:86-268`). Each exact client route therefore reaches
the branch's final `false`. The owner ruling now recorded in
`docs/missions/session-rules-2026-09-02/handoff.md:75-81` is that agent credentials can do everything
except promote; the implementation currently lets them do nothing with rules. The new command tests
replace the whole transport with `FakeClient` (`tools/cc-devthrottle/tests/test_rule_ops.py:68-112`),
so the advertised green Python run cannot see this.

Why it matters: the owner-facing reason for this command group is that a session can set a rule up.
That headline path returns 403 for list, screen-independent draft, add, show, and delete. This is the
same cross-surface failure shape that the existing guard test warns about at
`src/CcDirector.Gateway.UnitTests/SessionKeyGuardTests.cs:70-104`.

What would have to be true for this to be fine: the command would have to authenticate with some
credential other than `gateway.session_key()`, or agent credentials would have to be intentionally
barred from all rule operations. Both contradict the code and the recorded ruling.

## 2. HIGH - the grounding claim is optional, caller-asserted, and checks a different screen than the prompt

What is wrong: the route accepts `screen`, `sessionAgent`, and `sessionMachine` directly from the JSON
body (`src/CcDirector.Gateway/Api/SessionRuleEndpoints.cs:51-71`). It accepts no session id and performs
no server-side screen read, so the Gateway cannot establish that the text is a captured screen or that
the claimed origin produced it. An empty string skips grounding completely
(`src/CcDirector.Gateway/Rules/RuleDraftContract.cs:348-361`). The Cockpit always supplies that empty
string (`apps/cockpit/src/rules/RulesView.tsx:120-125,161`), and the command makes `--session` optional
(`tools/cc-devthrottle/src/cli.py:1646-1675`). Thus both clients have a normal path around the headline
check, not merely a malformed-request edge.

Even with a nonempty value, the model sees only the last 40 nonempty lines
(`RuleDraftContract.cs:175-205,389-397`) while validation searches the caller's whole string
(`RuleDraftContract.cs:348-354`). A trigger that exists only on line 1 of a 41-line capture passes even
though it was outside the screen shown in the prompt. Finally, the proposal retains untrimmed trigger
text, but the store trims it (`src/CcDirector.Gateway/Rules/SessionRuleStore.cs:74-79`), so a trigger
such as ` limit ` can be checked in its narrow form and stored later as the wider `limit`. That also
refutes the claim that the confirmed document is stored unchanged.

Why it matters: the pull request's first safety claim is that trigger words come from a real captured
screen. The check establishes only that a caller-supplied string contains the characters somewhere,
and on the primary page it normally establishes nothing. A rule may be offered as grounded when the
word was outside the prompt or may be stored with a wider trigger than the checked proposal.

What would have to be true for this to be fine: grounding would have to be explicitly advisory,
callers would have to be trusted to attest screen provenance, and the product would have to stop
claiming that a real captured screen is enforced. That is not the stated design.

## 3. HIGH - an originless draft lets the authoring answer choose every agent

What is wrong: scope is overridden only when `origin.IsKnown`; otherwise the model's scope is accepted
unchanged (`src/CcDirector.Gateway/Rules/RuleDraftContract.cs:317-331`). The test explicitly blesses
`all-sessions` from an originless answer even though the account never selected the all-agent star
(`src/CcDirector.Gateway.UnitTests/Rules/RuleDraftContractTests.cs:421-430`). The Cockpit client sends
neither session origin nor `allAgents` (`packages/client-core/src/rules/rulesClient.ts:205-215`), and
the page has no all-agent control. Therefore the primary page can return an every-agent rule solely
because the authoring answer chose it. This conflicts with the rule that the account, not the model,
chooses every agent.

Why it matters: scope is the first safety bound. The known-session path pins one part correctly, but
the page always takes the unpinned path. Dry run delays harm; it does not make silent scope widening
correct.

What would have to be true for this to be fine: when no origin exists, the model would have to be the
authorized chooser of agent scope and the preview would have to be the only scope boundary. The owner
ruling says the opposite.

## 4. HIGH - `rule add` stores a fresh proposal before the person can read it

What is wrong: the command drafts, immediately calls `create`, and only then prints the read-back
(`tools/cc-devthrottle/src/rule_ops.py:332-367`). Running `rule draft` first does not repair the order:
`rule add` makes another authoring call, so the proposal it stores can differ from the one previously
read. There is no command that accepts the exact reviewed draft and stores that document.

Why it matters: the settled authoring contract is sentence, read-back, confirmation, store. The
command-line path is sentence, model output, store, read-back. It removes the person from the decision
over derived trigger words, scope, checks, and ceilings. The stored rule is dry run, but the explicit
confirmation promise is still false.

What would have to be true for this to be fine: invoking `rule add` would have to count as advance
approval of any valid proposal the call might produce. That is materially weaker than the stated
read-before-store design.

## 5. HIGH - the page fabricates promotion evidence and the record does not keep it

What is wrong: after a generic confirmation dialog, the page supplies a hard-coded sentence claiming
the person read the dry-run record (`apps/cockpit/src/rules/RulesView.tsx:400-415`). The page does not
require that record to have been opened. The server checks only that the sentence is nonblank
(`src/CcDirector.Gateway/Rules/RulePromotionGrant.cs:89-104`), and promotion persists only
`PromotedBy`, not `Acknowledgement` (`src/CcDirector.Gateway/Rules/SessionRuleStore.cs:185-203`). This
directly contradicts the client contract saying the acknowledgement is what the record shows
(`packages/client-core/src/rules/rulesClient.ts:244-249`). The page test stops after opening the dialog
and never confirms or asserts the value sent (`apps/cockpit/src/rules/RulesView.test.tsx:238-253`), so
any nonblank constant can be substituted while it stays green.

Why it matters: promotion is the one transition that permits unattended typing. The evidence says a
review happened when the interface does not require one, and the durable record cannot show what was
agreed to despite claiming it can.

What would have to be true for this to be fine: the acknowledgement would have to be only an internal
UI label with no evidentiary meaning, and the record would have to promise only the actor. The current
types, comments, and client contract all assign it stronger meaning.

## 6. MEDIUM - the safety ceilings have no meaningful bounds

What is wrong: the store rejects only values less than or equal to zero
(`src/CcDirector.Gateway/Rules/SessionRuleRecordRules.cs:55-63`). Authoring may therefore propose and
store a daily cap of 2,147,483,647 or a one-second cooldown. Both satisfy the safety gate while making
the supposed bound ineffective for a bad live rule. The tests cover zero, normal sample values, and
round trips; none establishes an allowed range.

Why it matters: the design calls the cooldown and daily cap the protection that makes a loop finite.
Mathematical finiteness is not a useful safety bound if a rule can type billions of times per day.

What would have to be true for this to be fine: every positive 32-bit value would have to be an
intentional product allowance, with the human preview accepted as the only guard against dangerous
values. No such ruling appears in the inspected design.

## 7. MEDIUM - malformed numeric answers escape the refusal contract as server errors

What is wrong: `SessionRuleWire.Number` calls `JsonElement.GetInt32()` for any JSON number
(`src/CcDirector.Gateway/Api/SessionRuleWire.cs:132-133`). A decimal or an out-of-range integer throws.
`RuleDraftContract.Read` catches JSON parsing only, and the draft endpoint does not catch this class of
exception (`RuleDraftContract.cs:265-280,363-372`; `SessionRuleEndpoints.cs:51-89`). The same issue is
present on the create route, whose catch handles only `RuleRejectedException`
(`SessionRuleEndpoints.cs:103-124`).

Why it matters: authoring's boundary says any answer it cannot safely read becomes a stated refusal.
A plausible malformed answer instead becomes a 500 and loses the reason. None of the refusal tests
covers numeric shape or range.

What would have to be true for this to be fine: a schema-enforcing layer would have to guarantee
32-bit integer tokens before these readers run. The route takes raw `JsonElement`; no such layer is
present.

## 8. MEDIUM - both clients turn missing Gateway data into clean empty results

What is wrong: the Python client reads absent `rules`, `rule`, and `firings` fields as `[]`, `{}`, and
`[]` (`tools/cc-devthrottle/src/rule_ops.py:123-132`). The TypeScript client likewise reads a missing
`firings` field as an empty history (`packages/client-core/src/rules/rulesClient.ts:157-165`). Those
values become `No rules yet` or `It has not fired yet` in the interfaces. The TypeScript rules list
correctly rejects a missing `rules` field at lines 145-154, proving the stricter instrument already
exists but is not applied consistently.

The clients also derive product meaning locally: scope labels and wait units are composed in
`rulesClient.ts:277-293`, the unstored scope is reinterpreted again in
`apps/cockpit/src/rules/RulesView.tsx:359-369`, and the Python client independently renders an empty
scope as every session (`rule_ops.py:213-227`). This contradicts the claim that the clients only lay
out Gateway verdicts.

Why it matters: an incomplete or version-skewed JSON response is reported as the positive fact that
nothing exists or nothing fired. This is exactly an absence-shaped check passing when the data never
arrived, and the two clients can disagree about the same state.

What would have to be true for this to be fine: response fields would have to be schema-required and
validated before these defaults, and display labels would have to be a client-owned contract. Neither
is true in the current wire client.

## 9. MEDIUM - the new tenant and identity decisions are constant-substitutable under the suite

What is wrong: every `RuleAuthorTests` call uses `TenantId.Local`, and its asking seam discards the
tenant argument (`src/CcDirector.Gateway.UnitTests/Rules/RuleAuthorTests.cs:31-39`). Replacing the
tenant passed to the authoring brain with that constant would leave all new unit tests green. The
agent-origin tests likewise use one concrete agent value only
(`src/CcDirector.Gateway.UnitTests/Rules/RuleDraftContractTests.cs:339-430`), so substituting that
value for every known origin survives the asserted cases.

The claimed cross-tenant probe does not close the first hole. It exercises create, list, get, and
delete of stored rules, not the new draft route
(`src/CcDirector.Gateway.Tests/CensusRouteTenancyProbeTests.cs:177-267`). The census explicitly excludes
draft (`src/CcDirector.Gateway.Tests/ContextLessRouteCensusTests.cs:75-83`), and the authoring report
admits the draft route never ran over HTTP (`phase-3-authoring-report.md:269-270`). The code currently
passes `currentTenant()` correctly, but the advertised proof would stay green if that line were
replaced by a constant.

Why it matters: a tenant constant can select the wrong account's hosted configuration or charging
context; an agent constant puts a rule on the wrong sessions. These are exactly the values the prose
calls facts the product holds.

What would have to be true for this to be fine: another executed test would have to pass two distinct
tenant ids through the draft route and two distinct agent origins through the contract, asserting both
at the far side. No such test is in the inspected diff.

## 10. MEDIUM - the durable test evidence fails the mission's own red-first rule

What is wrong: the phase plan requires every test to be watched red before green
(`docs/missions/session-rules-2026-09-02/plan.md:107-124`). The report instead says all 28 new tests
were green on their first run and documents only three later mutations, one of which stayed green
(`phase-3-authoring-report.md:148-161`). The two reported failures include no command output and no
commit identifying the broken tree, so the branch cannot establish that even those quoted reds came
from the code named. The pull request bodies contain green summaries, not read-back artifacts.

The same report is internally contradictory about its live conversation evidence: lines 182-184 say
the multi-turn path was proven live, while lines 271-272 say nobody watched it against a real model.
Both cannot be the durable verdict.

Why it matters: most of the new tests have not demonstrated that they detect their named defect, and
the report cannot be used to resolve what was actually run. A green total therefore does not support
the safety claims made from it.

What would have to be true for this to be fine: durable run artifacts would have to exist elsewhere,
tied to the exact broken and restored commits for every load-bearing test, and the contradictory live
claim would need one authoritative correction. None is present in the branch or pull request bodies.

## Checked without a finding

- The explicit refusal readers for an unknown check, empty scope, empty read-back, empty question,
  invented trigger word, empty answer, and unparseable answer each have an assertion that requires a
  refusal and no proposal. Deleting those individual refusal branches makes their named assertions
  fail, subject to finding 7 for numeric shapes and finding 10 for red-first evidence.
- I found no path that parses, compiles, or evaluates generated program text. The answer can name only
  a registry entry and typed argument values; execution reflects into attributed, reviewed static
  methods. Literal argument values can influence a check, but no argument is interpreted as a pattern,
  expression, format string, or program.
- A draft cannot directly promote. The draft route writes nothing, the create route ignores extra state
  fields, and `SessionRuleStore.Create` constructs `dry_run`. The separate promotion defects are finding
  5, and the agent credential boundary currently fails in the over-restrictive direction described in
  finding 1.
