# Fix round D - the Manager's report

Branch `mission/rules-fix-d`, cut from the Gateway slice `3eac5b70b`, carrying the client slice
(`origin/rule-authoring-by-conversation`, merged at `619ab9cf7`) and the landed guard fix
(`dd78fd878`, merged at `0bc0bd87e`). Every ruling in `fix-round-d.md` is addressed below, one section
each, with what closes it and what proves it. Finding 1 was not touched here; it is the guard fix.

The evidence rule for this round was: watch the test fail first, quote the output, name the broken
commit. Where a test could compile against the old code it was committed RED before any fix, in one
commit, and the run at that commit is quoted. Where a test needed the new shapes to compile, the guard
was broken on purpose afterwards in a probe commit, the run is quoted, and the probe was reverted.
The last section says plainly which tests were never watched red.

---

## The red commit, before any fix: `40cd17c63`

Sixteen Gateway unit tests, one client-core test, one Cockpit page test and four command line tests
were committed against the unfixed code and run.

```
dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~CcDirector.Gateway.Tests.Rules" --nologo -v q
SessionRuleWireTests.A_number_that_is_not_a_whole_number_in_range_is_refused_with_a_sentence(written: "99999999999") [FAIL]
SessionRuleWireTests.A_number_that_is_not_a_whole_number_in_range_is_refused_with_a_sentence(written: "-1e3") [FAIL]
SessionRuleWireTests.A_number_that_is_not_a_whole_number_in_range_is_refused_with_a_sentence(written: "600.5") [FAIL]
SessionRuleWireTests.A_narrow_scope_is_labelled_by_the_parts_that_are_set [FAIL]
SessionRuleWireTests.A_served_rule_carries_the_finished_scope_label_and_wait_label [FAIL]
RuleDraftContractTests.A_padded_trigger_word_is_offered_as_the_word_the_store_will_keep [FAIL]
RuleDraftContractTests.An_out_of_range_ceiling_is_refused_with_a_sentence_and_not_thrown [FAIL]
RuleDraftContractTests.An_answer_whose_session_origin_is_not_known_is_refused_rather_than_scoped_by_the_model [FAIL]
RuleDraftContractTests.A_trigger_word_outside_the_lines_the_model_was_shown_is_refused [FAIL]
RuleDraftContractTests.A_decimal_ceiling_is_refused_with_a_sentence_and_not_thrown [FAIL]
RuleDraftContractTests.The_question_states_the_bounds_of_the_ceilings [FAIL]
SessionRuleStoreTests.Promoting_persists_what_the_person_agreed_to_and_serves_it_back [FAIL]
SessionRuleStoreTests.A_ceiling_outside_the_bounds_is_refused_naming_the_value_and_the_bound(cooldown: 1, cap: 2147483647) [FAIL]
SessionRuleStoreTests.A_ceiling_outside_the_bounds_is_refused_naming_the_value_and_the_bound(cooldown: 86401, cap: 5) [FAIL]
SessionRuleStoreTests.A_ceiling_outside_the_bounds_is_refused_naming_the_value_and_the_bound(cooldown: 600, cap: 101) [FAIL]
SessionRuleStoreTests.A_ceiling_outside_the_bounds_is_refused_naming_the_value_and_the_bound(cooldown: 59, cap: 5) [FAIL]
Failed!  - Failed:    16, Passed:   270, Skipped:     0, Total:   286
```

```
packages/client-core: npx vitest run src/rules
 x  a firings answer with no firings field is an error, never an empty history
 Tests  1 failed | 2 passed (3)

apps/cockpit: npx vitest run src/rules
 x  shows the dry-run record before making a rule live, and sends an acknowledgement that describes it
    Unable to find an element with the text: /no work of the session's/
 Tests  1 failed | 10 passed (11)

python -m pytest tools/cc-devthrottle/tests/test_rule_ops.py -q
FAILED test_add_takes_the_reviewed_proposal_and_makes_no_authoring_call   (assert [('C:\\Users\...', '', False)] == [])
FAILED test_a_rules_answer_with_no_rules_field_is_an_error_not_an_empty_list   (DID NOT RAISE)
FAILED test_a_rule_answer_with_no_rule_field_is_an_error_not_an_empty_rule   (DID NOT RAISE)
FAILED test_a_firings_answer_with_no_firings_field_is_an_error_not_an_empty_history   (DID NOT RAISE)
```

Two tests in that commit PASSED on purpose - `The_agent_scope_is_the_origin_that_was_given_and_not_a_constant`
and `Two_accounts_reach_the_model_as_two_different_tenants_and_not_as_a_constant` - because the code
was already right; they are guards against a constant, and they were watched red by mutation (below).

Several of the sixteen were written against the OLD signatures (a screen as a string, an origin as a
parameter) and were then moved onto the new shapes in `c418b6b2b`. Their assertions did not change.
They are: the padded word, the word outside the excerpt, the originless refusal, the two D7
refusals, the bounds sentence, the two-origins theory, the two-tenants test.

---

## RULING D2 - the Gateway reads the screen itself. CLOSED.

**Commit `c418b6b2b`.** New types `RuleScreenReading` (one excerpt, produced once), `RuleScreenExcerpt`,
`RuleScreenResult`, the `RuleScreenReader` delegate, and `RuleTriggerWords` (one normaliser, one
grounding check). `RuleDraftContract.BuildDraftPrompt` and `Read` take a `RuleScreenReading` and nothing
else about the screen - there is no overload without one. `RuleAuthor` takes an `ask` seam and a
`readScreen` seam; `DraftAsync(tenant, turns, sessionId, allAgents, ct)` refuses a blank session id
before the model is asked. `GatewayHost.ReadRuleScreenAsync` locates the session on the pushed roster in
the caller's tenant, resolves its Director in that tenant, reads the screen through
`GatewayRuleEnvironment.ReadScreenRowsAsync` (the evaluator's own read), and takes the agent and machine
from the roster row. The request body fields `screen`, `sessionAgent` and `sessionMachine` are gone.

Item by item:

1. Session id, not a screen - `SessionRuleEndpoints` reads `sessionId` and `allAgents` only.
2. No empty-screen path - `RuleAuthor.ReadScreenAsync` refuses a blank id, a reader refusal, an
   empty excerpt, and an origin with no agent. `Naming_no_session_is_refused_and_the_model_is_never_asked`
   asserts the ask seam was called zero times.
3. One text - the prompt appends `screen.Excerpt` and `RuleTriggerWords.WhyNotGrounded` checks against
   `screen.Excerpt`. `A_trigger_word_outside_the_lines_the_model_was_shown_is_refused` builds a 41-line
   screen, asserts the prompt does NOT carry line 1, and asserts the word from line 1 is refused.
4. One normaliser - `RuleTriggerWords.NormaliseAll` is called by `RuleDraftContract.Read` and by
   `SessionRuleStore.Create`. `A_padded_trigger_word_is_offered_as_the_word_the_store_will_keep`.
5. The write gate - `POST /gateway/rules` reads `sessionId` and calls `RuleAuthor.WhyNotGroundedAsync`,
   which re-reads the screen and runs the same function, before `store.Create`. Unit:
   `The_write_gate_refuses_a_trigger_word_that_is_not_on_the_sessions_screen_now`,
   `The_write_gate_refuses_a_body_that_names_no_session`, `The_write_gate_lets_a_grounded_body_through`.
   Over HTTP: `SessionRuleCreate_ReadsTheSessionsScreenAgainAndRefusesAnUngroundedWord` posts straight to
   create with no draft, is refused by sentence on "ECONNREFUSED", refused on another tenant's session,
   and stored on the control body.

**Mutation probes (each committed, run, reverted; the probe commits stacked because my first revert
step used a flag git revert does not accept, so the failure set grows down the list - the reds
attributable to each probe are the NEW names in its run):**

```
85d23c122  contract pins "ClaudeCode" instead of origin.Agent
  RuleDraftContractTests.The_agent_scope_is_the_origin_that_was_given_and_not_a_constant(agent: "Codex", machine: "SOREN_SOUTH") [FAIL]
  Failed: 1, Passed: 35
694127bd9  the blank-session refusal removed
  RuleAuthorTests.Naming_no_session_is_refused_and_the_model_is_never_asked [FAIL]
  RuleAuthorTests.The_write_gate_refuses_a_body_that_names_no_session [FAIL]
  (plus the stacked Two_accounts red from 34baf2dd5)
fdeef51c5  the write gate's grounding line removed
  RuleAuthorTests.The_write_gate_refuses_a_trigger_word_that_is_not_on_the_sessions_screen_now [FAIL]  (new)
```

Reverts: `9ab84f96c`, `4e32b2d0c`, `f032b1657`, `e26819e23`, `1cf5286ae`; `git diff c418b6b2b -- src`
was empty afterwards and the rules filter ran 299 green.

**Phase 3 is now the conversation loop only.** The screen-reading half of the phase 3 brief is done by
this ruling: the page and the command line name a session and the Gateway reads it.

**Not proven:** `GatewayHost.ReadRuleScreenAsync` - the production roster locate and tunnel read - is
exercised by no test. Every test of the draft route substitutes the reader seam. Its verdict is a code
read: it is the same `TryLocate` the session routes use and the same `ReadScreenRowsAsync` the
evaluator uses, both already proven elsewhere, but nobody has drafted a rule against a real Director's
screen through this code.

## RULING D3 - the model never chooses the agent scope. CLOSED.

**Commit `c418b6b2b`.** `RuleDraftContract.Read` refuses when `screen.Origin.IsKnown` is false, before
the scope is read; otherwise `scope.Agent` is the origin's agent or null under the star, whatever the
model wrote. The test that blessed an originless every-session answer
(`With_no_session_known_the_agent_scope_is_left_as_the_model_wrote_it`) is deleted and replaced by
`An_answer_whose_session_origin_is_not_known_is_refused_rather_than_scoped_by_the_model` (red at
`40cd17c63`). `RuleAuthor` refuses a roster row with no agent
(`A_session_with_no_known_agent_is_refused_rather_than_letting_the_model_choose`).

**The star is the account's control.** The Cockpit page (`9a025ae54`) has a checkbox "For every agent,
not only ClaudeCode" beside the chosen session and sends `allAgents` on the draft; the drafted body
carries `allAgents` and `sessionId` so the write gate holds the scope to the same choice
(`The_write_gate_refuses_an_agent_scope_that_is_not_the_sessions_or_the_star`, watched red at probe
`d2ac4e26d`: three inline cases FAIL, then reverted). Page tests
`drafts against the chosen session and sends the every-agent choice as a fact` and
`sends the star when the person chose every agent` assert the third argument of the client call.

## RULING D4 - rule add stores the document that was read. CLOSED.

**Commit `9a025ae54`.** `rule draft "<said>" --session <s> [--all-agents] [--json]` names the session
(required; the command refuses without it before the Gateway is asked) and prints the proposal.
`rule add <proposal.json>` reads the file `draft --json` wrote (or a bare rule body, or `-` for
standard input), posts exactly its `rule` to the create route, and makes NO authoring call; a file
holding a question or no rule is refused and nothing is stored. There is no command that authors and
stores in one step. The action registry says so.

Red at `40cd17c63`: `test_add_takes_the_reviewed_proposal_and_makes_no_authoring_call` (the old `add`
made a draft call). Green now, with `test_what_draft_prints_is_what_add_stores` (the real round trip:
draft `--json` to a file, add the file, the created body equals the printed rule) and
`test_the_draft_request_carries_the_session_id_and_the_star_and_no_screen` (the wire body).

## RULING D5 - persist the acknowledgement; the page must not invent it. CLOSED.

**Commit `c418b6b2b`:** `SessionRuleEntity.Acknowledgement`, set in `SessionRuleStore.Promote` from
`grant.Acknowledgement`, carried on `SessionRule` and served as `acknowledgement`. Migration pair
`AddRuleAcknowledgement` (SQLite `20260903162737`, Postgres `20260903162749`), one column each.
Red at `40cd17c63`: `Promoting_persists_what_the_person_agreed_to_and_serves_it_back`.

**Commit `9a025ae54`:** "Make it live" first loads the rule's firings and shows them in the confirmation
dialog; the confirm button is "Make it live, I have read the record"; the sentence sent is built from
what was shown (`I have read this rule's dry-run record: 1 firing, the latest on <date> decided act. I
am making it live.`); a record that cannot be read shows the reason and sends nothing. Red at
`40cd17c63`: `shows the dry-run record before making a rule live, and sends an acknowledgement that
describes it` - it asserts the VALUE sent. Green now, with `says so when the record is empty, and sends
that` and `does not promote when the dry-run record cannot be read`.

**Migration slot.** `origin/main` (now `3f2e2b652`) holds only the three migrations of 2026-09-02;
this pair is the only one on this branch. `origin/mission/terminal-rules` still holds an unlanded
`AddSessionScreens`; whoever lands second regenerates the snapshot.

### The defect found under D5, and why no test caught it

While writing the direct tests for the grant (`RulePromotionGrantCallerTests`) I found that
`RulePromotionGrant.CallerOf` read a request item named `DeviceKeyId` that nothing in the Gateway ever
writes - `AuthMiddleware` writes `cc.auth.DeviceKey` and `cc.auth.DeviceIdentity` - and that the
middleware never sets an authenticated principal. So `CallerOf` returned null on every real request and
every promotion over HTTP was refused as "no caller the Gateway could name". The Architect confirmed
this independently. It means promotion has shipped broken on `main` since the grant was hardened; the
only promotion this mission ever demonstrated was under the earlier id-and-timestamp shape.

**Proven red, over HTTP, before the fix:**

```
dotnet test src/CcDirector.Gateway.Tests --filter "FullyQualifiedName~CensusRouteTenancyProbeTests|FullyQualifiedName~ContextLessRouteCensusTests" --nologo -v q
CensusRouteTenancyProbeTests.SessionRulePromote_ByADeviceKey_IsNamedAfterTheDeviceAndKeepsTheAcknowledgement [FAIL]
```

and in the unit suite, once the request helper marked a request the way the middleware really does:

```
RulePromotionGrantCallerTests.A_request_a_session_key_authenticated_is_refused_on_the_credential_itself [FAIL]
RulePromotionGrantCallerTests.A_request_the_device_middleware_authenticated_is_named_after_the_device [FAIL]
RulePromotionGrantCallerTests.A_request_carrying_a_session_key_beside_a_device_identity_is_still_refused [FAIL]
RulePromotionBoundaryTests.A_person_who_asked_does_promote_it_and_the_rule_records_who [FAIL]
SessionRuleStoreTests.Promoting_persists_what_the_person_agreed_to_and_serves_it_back [FAIL]
RulePromotionBoundaryTests.A_grant_is_spent_by_the_promotion_it_was_obtained_for_and_cannot_be_used_again [FAIL]
SessionRuleStoreTests.A_new_rule_is_always_in_dry_run_and_only_a_person_moves_it [FAIL]
SessionRuleStoreTests.A_live_rule_may_record_what_it_typed [FAIL]
Failed: 8, Passed: 34
```

(That same run also failed `SessionRules_AreNotReachableAcrossTenantsEvenHoldingTheOtherTenantsRuleId`.
It passes alone: the hosted test class shares one database per run, my new probes had left rules in
tenant A, and that test asserts an exact count. My probes now delete what they store.)

**Fixed in `cc50ffff9`:** `CallerOf` reads `AuthMiddleware.AuthenticatedDeviceItemKey` as a
`DeviceCredentialIdentity` and names its `DeviceId`. The HTTP test now passes: a device-key promotion is
served `live`, `promotedBy` is `dev-ca` (the enrolled device), `acknowledgement` is the sentence sent,
and a re-read shows both.

**Why no test caught it, stated plainly.** Every test of promotion - the store tests, the boundary
tests, the write-gate tests - minted the grant through the helper `AnInboundRequest.FromDevice`, which
set the SAME made-up item the grant read. The test and the product agreed with each other and with
nothing else; nothing sent a promotion through the real middleware. That is the shape of the guard
defect (the guard's tests were written against its own list, the command line's tests mocked the
transport) and the shape of finding 9 (a tenant constant nothing at the far side would notice). Three
instances of one pattern: a test that constructs, from the same assumptions as the code under test, the
thing the production pipeline was supposed to produce. This is a finding about the suite. The remedy
applied here: the helper now uses the middleware's own constants and identity types, and promotion has
an HTTP-level test. The Architect said this defect goes in the QA report as a user-facing one.

## RULING D6 - the ceilings have real bounds. CLOSED.

**Commit `c418b6b2b`.** `RuleCeilings`: cooldown at least 60 seconds and at most 24 hours, daily cap at
least 1 and at most 100, enforced in `SessionRuleRecordRules.CheckRule` (so the store, the write gate
in the context, and the draft's pre-check all refuse the same way), with the sentence naming the value
and the bound. The question to the model quotes the bounds
(`The_question_states_the_bounds_of_the_ceilings`).

Red at `40cd17c63`: the four out-of-bounds cases (59, 86401, 101, and 1 with 2,147,483,647). Green now,
with the four on-the-bound cases accepted and `A_rule_whose_ceiling_is_outside_the_bounds_is_not_offered`.

**These are the Architect's numbers, not the owner's, and the owner can widen them.** They were chosen so
a live rule cannot type more than a hundred times a day into one session, and they are generous next
to the design's own examples.

## RULING D7 - a number that cannot be read is a refusal. CLOSED.

**Commit `c418b6b2b`.** `SessionRuleWire.TryNumber` reads a present number with `TryGetInt32`; the
draft reader refuses with a sentence naming the field and the value as written; `SessionRuleWire.Number`
throws `RuleRejectedException` for the write route, whose catch turns it into a 400. Both route handlers
now catch `RuleRejectedException` around the whole body.

Red at `40cd17c63`: the two contract tests (`600.5`, `99999999999`) and the wire theory (`600.5`,
`99999999999`, `-1e3`). Over HTTP (`cc50ffff9`): `SessionRuleCreate_RefusesACeilingItCannotReadWithASentence`
(both values, 400 with the field and the value in the sentence) and
`SessionRuleDraft_RefusesAModelAnswerWhoseCeilingItCannotReadWithASentence` (the fake model writes
`900.5`, the route answers 400 with `cooldown_seconds` and `900.5`).

## RULING D8 - a missing field is a broken instrument; the clients stop deciding. CLOSED.

**Gateway (`c418b6b2b`):** `RuleLabels.Scope` and `RuleLabels.Wait`; the served rule carries `scopeLabel`
and `waitLabel`, and the drafted proposal carries the same two. Red at `40cd17c63`: the two wire tests.

**Clients (`9a025ae54`):** `rulesClient.ts` throws on a missing `rules`, `firings`, `rule` or `deleted`
field and on a proposal missing its excerpt or labels; `describeScope`, `describeWait`, the page's
`describeWriteScope` and `captureSessionScreen` are deleted; the page renders `scopeLabel` and
`waitLabel` verbatim (`renders the Gateway's scope and wait labels verbatim and composes none of its
own` uses labels no client could compose and asserts "every session" and "2 hours" are absent). The
Python client raises `GatewayError` on a missing field and prints the labels verbatim
(`test_list_prints_the_gateways_labels_verbatim_and_composes_none`,
`test_a_rule_served_without_its_labels_is_an_error_not_a_guess`).

Red at `40cd17c63`: client-core `a firings answer with no firings field is an error` and the three
Python missing-field tests.

## RULING D9 - the tenant and the agent are not constants. CLOSED.

- **Two origins through the contract:** `The_agent_scope_is_the_origin_that_was_given_and_not_a_constant`
  (ClaudeCode on SOREN_NORTH, Codex on SOREN_SOUTH), watched red at probe `85d23c122` (quoted under D2).
- **Two tenants at the author's seams:**
  `Two_accounts_reach_the_model_and_the_roster_as_two_different_tenants_and_not_as_a_constant` records
  the tenant at BOTH seams; watched red at probe `34baf2dd5` (`_ask(TenantId.Local, ...)`):
  ```
  RuleAuthorTests.Two_accounts_reach_the_model_and_the_roster_as_two_different_tenants_and_not_as_a_constant [FAIL]
  Failed: 1, Passed: 26
  ```
- **Two tenants through the draft route over HTTP (`cc50ffff9`):** the hosted test host is given two
  seams (`RuleAuthoringAskForTests`, `RuleScreenReaderForTests`), resolved at call time, and the fake
  roster is keyed by tenant. `SessionRuleDraft_ReachesTheModelAndTheRosterAsTheCallersOwnTenant_ForTwoTenants`
  asserts at the far side `[tenantA, tenantB]` at the model and `[(tenantA, sess-a), (tenantB, sess-b)]`
  at the roster; `SessionRuleDraft_CannotGroundInAnotherTenantsSession` asserts the refusal sentence and
  that the model was never asked, with a destructibility control. Watched red by the route-level
  mutation `5bb91db4a` (the host hands the reader `TenantId.Local`):
  ```
  CensusRouteTenancyProbeTests.SessionRuleDraft_ReachesTheModelAndTheRosterAsTheCallersOwnTenant_ForTwoTenants [FAIL]
  CensusRouteTenancyProbeTests.SessionRuleDraft_CannotGroundInAnotherTenantsSession [FAIL]
  CensusRouteTenancyProbeTests.SessionRuleCreate_ReadsTheSessionsScreenAgainAndRefusesAnUngroundedWord [FAIL]
  (and five more that need the caller's tenant)   Failed: 8, Passed: 5
  ```
  reverted at `c0c4b0bfd`.
- **The census:** the context-less census cannot hold the draft route by its own definition (no path
  parameter), so `ContextLessRouteCensusTests` gains a second inventory derived from the same route
  table - the rules routes that take a body and no path parameter and no HttpContext - asserted
  EXACTLY as `POST /gateway/rules` and `POST /gateway/rules/draft`, with the verdict and the executed
  probes named in its comment. The tenancy probe now covers draft, create and promote.

## RULING D10 - the evidence rules. APPLIED THROUGHOUT.

The red commit, the probe commits and their reverts, and the quoted outputs are above. The phase 3
report's contradiction is corrected in `4f74c0beb` to one statement: the multi-turn conversation has one
observed live run through the hand-wired probe and none through the page or the command line.

**Watched red and how:**

| Test | How |
| --- | --- |
| The sixteen Gateway unit tests, the client-core firings test, the Cockpit acknowledgement test, the four Python tests listed at the top | red at `40cd17c63` before any fix |
| Two origins; two tenants at the seams; no-session refusal; write-gate grounding; write-gate agent scope | mutation probes `85d23c122`, `34baf2dd5`, `694127bd9`, `fdeef51c5`, `d2ac4e26d` |
| The three grant caller tests; the HTTP promotion test; the boundary and store promotion tests | red before `cc50ffff9`, quoted under D5 |
| The HTTP draft-route tenancy tests, the HTTP write-gate test, the HTTP D7 tests | red at route mutation `5bb91db4a` |

**Never watched red, stated plainly:** `A_ceiling_on_the_bound_itself_is_accepted`,
`A_whole_number_in_range_is_read_as_itself`, `The_write_gate_lets_a_grounded_body_through`,
`An_empty_screen_is_refused_rather_than_written_from`,
`A_session_with_no_known_agent_is_refused_rather_than_letting_the_model_choose`,
`The_proposal_names_the_session_it_was_grounded_in`,
`A_rule_for_every_agent_survives_the_round_trip_as_every_session`,
`A_rule_whose_ceiling_is_outside_the_bounds_is_not_offered`, `There_is_no_reading_without_a_screen`,
`A_request_the_pipeline_could_not_name_is_refused`, `A_signed_in_person_is_named`,
`SessionRuleDraft_WithNoSessionIsRefusedAndTheModelIsNeverAsked` (over HTTP; its unit twin was),
`The_hosted_body_scoped_rule_route_set_is_exactly_the_ruled_census`; the Cockpit session-chooser tests,
the labels-verbatim test, the empty-record and unreadable-record tests; the Python draft tests
(`test_draft_names_the_session...`, `test_draft_without_a_session...`, the star tests,
`test_draft_stores_nothing_and_prints_the_gateways_labels`, `test_what_draft_prints_is_what_add_stores`,
`test_add_accepts_a_bare_rule_body_too`, the two add-refusal tests, the labels tests); the client-core
`rules` missing-field test (it already passed at the red commit - the instrument existed there).
None of the tests from the previous round were retrofitted.

## RULING D11 - refuse promotion at the grant on the credential. CLOSED.

**Commit `cc50ffff9`.** `RulePromotionGrant.FromAuthenticatedRequest` first asks
`AuthMiddleware.CallingSession(http)`; a session-key identity is refused with its own sentence ("made
with a session key, which is an agent's credential ... Nothing was promoted"), before a device identity
beside it could name anybody. Tested directly with no route guard in the path:
`A_request_a_session_key_authenticated_is_refused_on_the_credential_itself` and
`A_request_carrying_a_session_key_beside_a_device_identity_is_still_refused`, both watched red (quoted
under D5). The Architect has corrected D11's stated reason in the record: the grant refused everyone by
accident rather than admitting agents, and D11 is defence in depth on a grant that now works.

---

## The gate

| Suite | Result |
| --- | --- |
| `.\scripts\test-local.ps1` (9 projects) | every outcome Completed; 4,906 total, 0 failed, 2 skipped. The script's budget verdict flagged `Gateway.UnitTests` at 1 minute 59 seconds against the 120-second ceiling; that is a budget note, not a test failure, and the suite grew by the guard fix's and this round's tests |
| `Gateway.Tests` (parked, hosted), full | GATEWAY_TESTS_RESULT |
| `Gateway.Tests` filtered to the probe, the census and the guard census | 16 + 2 green |
| `npm run typecheck` (4 workspaces) | clean |
| `npm test` per workspace | client-core 982, cockpit 298, mobile 14, cc-assistant 106, all green |
| `pytest tools/cc-devthrottle/tests/` | 264 passed |

The Postgres proof rig `cc-pg-test` on port 55432 was up (46 hours) for every hosted run.

## What is not proven

- No rule has been drafted against a real Director's screen through `GatewayHost.ReadRuleScreenAsync`;
  every draft-route test substitutes the screen seam. The production locate-and-read is a code read.
- The page has not been driven against a running Gateway; its client is mocked. The command line's
  transport is mocked too, except for the wire-body test of the draft request.
- No live model has been asked with the new prompt (the bounds sentence, the "read just now" framing).
- The Cockpit's session list is the roster's `listSessions`; a session with no id on the roster is
  filtered out silently, which is a code-read verdict.

## Left for the Architect

- The report and this branch do not touch `SessionKeyGuard.cs`; the guard fix is merged in as it landed.
- `rule draft` and `rule add` changed shape: `--lines` is gone from both, `--session` is required on
  draft, and `add` takes a file. The action registry describes the new shape.
- The default gate's budget note on `Gateway.UnitTests` will need a decision (park the DB-backed rules
  tests, or accept the two minutes) that is not this round's to make.
