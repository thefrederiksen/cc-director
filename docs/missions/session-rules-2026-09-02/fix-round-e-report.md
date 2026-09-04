# Fix round E - the Manager's report

Branch `mission/rules-fix-d`, on top of the round D work and the merged `inspection-e.md`
(`23a058fab`). Four rulings in `fix-round-e.md`, one section each, saying whether it is closed and what
proves it. The seven rulings the inspection cleared were not touched.

The evidence rule was applied as in round D, with the one correction the Architect asked for: every
probe commit was reverted with `git revert --no-edit HEAD` before the next, and the diff against the
green tree was checked empty after each.

---

## The red commit, before any fix: `82ec0b65a`

```
dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~RulesWriteGateTests" --nologo -v q
RulesWriteGateTests.A_stored_rules_trigger_words_cannot_be_changed_through_the_context_without_grounding_evidence [FAIL]
RulesWriteGateTests.A_rule_written_straight_through_the_context_is_refused_because_its_words_carry_no_grounding_evidence [FAIL]
Failed!  - Failed:     2, Passed:    21, Skipped:     0, Total:    23

packages/client-core: npx vitest run src/rules
 x  rules: null is an error, never an empty list
 x  rules: a string where the list should be is an error
 x  rules: a record missing a required field is an error naming the field
 x  firings: null is an error, never an empty history
 x  firings: an object where the list should be is an error
 x  firings: a record whose decision is not a string is an error naming the field
 x  delete: a deleted flag that is not a boolean is an error, never a client-authored outcome
 x  create: a rule that is not an object is an error
 x  draft: a proposal whose rule lacks its instruction is an error naming the field
 Tests  9 failed | 6 passed (15)

python -m pytest tools/cc-devthrottle/tests/test_rule_ops.py -q
FAILED test_rules_null_is_an_error_not_an_empty_list
FAILED test_rules_of_the_wrong_type_is_an_error
FAILED test_a_rule_record_missing_a_required_field_is_an_error_naming_the_field
FAILED test_firings_null_is_an_error_not_an_empty_history
FAILED test_firings_of_the_wrong_type_is_an_error
FAILED test_a_firing_whose_decision_is_not_a_string_is_an_error_naming_the_field
FAILED test_a_deleted_flag_that_is_not_a_boolean_is_an_error_not_a_client_authored_outcome
FAILED test_a_rule_that_is_not_an_object_is_an_error
FAILED test_list_does_not_print_a_zero_or_none_for_a_field_the_gateway_did_not_send
FAILED test_show_does_not_print_blanks_for_a_firing_the_gateway_sent_broken
10 failed, 23 passed
```

---

## RULING E1 - grounding is an invariant of the store. CLOSED.

**Commit `44553702d`.** `RuleGroundingEvidence` (private constructor, internal `Minted` factory) names the
session and the exact normalised words that were found on its screen, and is single use.
`RuleAuthor.GroundAsync` reads the session's screen afresh, runs the same grounding function as the
draft, holds the agent scope, and is the only production code that mints the evidence.
`SessionRuleStore.Create` takes the evidence as a required parameter and refuses null, evidence for other
words, and evidence presented twice. `GatewayDbContext.GroundingInEffect`, set by the store around its
one save, is demanded by the write gate for every ADDED rule and for every MODIFIED rule whose trigger
words changed - the same shape as `PromotionInEffect`, so a direct context write meets the same refusal.
The create route calls `GroundAsync` and hands the evidence to the store.
`RulesGroundingBoundaryGuardTests` reads the built assembly and asserts the one minter (`RuleAuthor`),
that nothing but the evidence type calls its constructor, and that the one production caller of the
store's `Create` is the create endpoint.

**The regression test, as the ruling describes it:**

- Direct context path, no evidence: `A_rule_written_straight_through_the_context_is_refused_because_its_words_carry_no_grounding_evidence`
  and `A_stored_rules_trigger_words_cannot_be_changed_through_the_context_without_grounding_evidence` -
  both red at `82ec0b65a` (quoted above), green now.
- Public store path, no evidence: `A_rule_with_no_grounding_evidence_is_refused_by_the_store_and_nothing_is_written`;
  evidence for other words (`Evidence_minted_for_other_words_is_refused`, three cases); evidence reused
  (`Evidence_is_spent_by_the_write_it_was_minted_for_and_cannot_be_presented_again`).
- **The positive control through the real grounded route:** `RuleAuthorTests.StoreAsync` now calls
  `RuleAuthor.GroundAsync` exactly as the create route does and hands the evidence to the store, so
  `A_drafted_rule_is_stored_by_the_writing_route_with_every_part_intact`,
  `A_rule_for_every_agent_survives_the_round_trip_as_every_session`,
  `A_rule_scoped_to_one_repository_survives_the_round_trip` and the outage test all reach storage - and
  over HTTP, `SessionRuleCreate_ReadsTheSessionsScreenAgainAndRefusesAnUngroundedWord` stores its control
  body through the real route. The refusals cannot pass on a store that refuses everything.
- The existing round-trip test (`A_rule_round_trips_through_the_store_with_every_part_intact`) was not
  deleted; it carries evidence minted through a real author over a screen that shows its five words
  (the `Grounded` helper has no shortcut to the factory).

**Mutation probes, each reverted before the next:**

```
c195fcbe6  the store mints its own evidence when handed none
  RulesGroundingBoundaryGuardTests.The_only_production_code_that_can_mint_grounding_evidence_is_the_author [FAIL]
  SessionRuleStoreTests.A_rule_with_no_grounding_evidence_is_refused_by_the_store_and_nothing_is_written [FAIL]
  Failed: 2, Passed: 35        reverted by b5abddf0b, diff against 6964fd73b empty
a8571e7f3  Covers answers true for any words
  SessionRuleStoreTests.Evidence_minted_for_other_words_is_refused (three cases) [FAIL]
  Failed: 3, Passed: 31        reverted by df0d6c1dd, diff empty
```

Two earlier attempts at the first probe (`c0a9e0397`, and a retake) did not compile and proved nothing;
both are reverted (`662b64569`, `2fff958c6`) and are listed here so the history reads honestly.

## RULING E2 - a present field of the wrong shape is as broken as a missing one. CLOSED.

**Commit `084a3bc8e`.** `rulesClient.ts` validates every answer at runtime: the container is a list or
object as asked, and every rule, firing, check run and proposal has each required field of the right
type, with the scope's four parts a string or null; the error names the field, what was expected and
what came. `deleteRule` demands a boolean. `rule_ops.py` does the same through `_need`, `_read_rule`,
`_read_firing` and `_read_draft_answer` (a Python bool is asked for exactly, since a bool is an int), and
`_describe` and the firing printer no longer default anything - a field the Gateway did not send is an
error naming it, not an empty string, `(none)` or `0`.

Tests, each beside a valid non-empty control, all red at `82ec0b65a`: null, wrong type and a malformed
child for rules and for firings, a non-boolean `deleted`, a non-object `rule`, a proposal missing its
instruction; and on the command line, a listing over a rule without `dailyCap` errors and names the
field, and `rule show` over a firing without `reason` errors and names it. Client-core rules suite 15
green, command line rule suite 33 green.

## RULING E3 - the provenance join is one testable type, kept in the path. CLOSED.

**Commit `b28111dd5`.** `GatewayRuleScreenReader` holds the production composition: locate the session on
the caller's tenant's roster, read the rows from the Director the roster named, take the origin from the
roster row. `GatewayHost.ReadRuleScreenAsync` now only supplies the two seams (`PushedSessions.TryLocate`
in the caller's tenant, and `GatewayRuleEnvironment.ReadScreenRowsAsync` through the Director resolved
in that tenant) and delegates. `GatewayRuleScreenReaderTests` keep the type in the path and observe the
far side, presence first:

| Case | Asserted |
| --- | --- |
| An owned session | the exact excerpt of the rows, and the origin `ClaudeCode` on `SOREN_NORTH` off the roster row; the read was made for the Director the roster named, in the caller's tenant |
| A second tenant's session | refused with "is not on this account's roster", model never asked |
| A session not on the roster | refused the same way, model never asked |
| A Director vanished between locate and read | refused with "could not be read", the word "empty" absent, model never asked |
| A screen that reads empty | a reading with an empty excerpt, refused by the author as "empty screen", the words "could not be read" absent, model never asked |

**Mutation probes:**

```
db8b522af  a vanished Director reads as an empty screen
  GatewayRuleScreenReaderTests.A_director_that_vanished_between_locate_and_read_is_refused_with_its_own_sentence_never_as_an_empty_screen [FAIL]
  Failed: 1, Passed: 5         reverted by 96f6611c4, diff empty
e5cbbcd04  the reader locates on a constant tenant
  GatewayRuleScreenReaderTests.A_second_tenants_session_is_refused_and_the_model_is_never_asked [FAIL]
  Failed: 1, Passed: 5         reverted by 589fbbd17, diff empty
```

**Not proven, stated:** the two seams the host supplies are still wired by hand in `GatewayHost` and no
test calls that wiring; the hosted fixture still substitutes the whole reader. The composition is now
tested; the two one-line lambdas that feed it are a code read. The demonstrations exercise them for real.

## RULING E4 - the placeholder goes; the full parked suite is the Architect's. CLOSED.

The `GATEWAY_TESTS_RESULT` token in `fix-round-d-report.md` is replaced by the truth: the full parked
suite was NOT completed by this seat. A full run was killed at the tool cap; five chunks (1,190 of
2,339 tests) then ran green at `3020fc945` before the Architect took the suite off every seat. Under
E4 this seat ran only the narrow filters covering its change (below).

---

## The gate for round E

| Suite | Result |
| --- | --- |
| `.\scripts\test-local.ps1` (9 projects) at `b5abddf0b` | every outcome Completed; 4,923 total, 0 failed, 2 skipped. The budget verdict again flagged `Gateway.UnitTests`, at 2 minutes 8 seconds against the 120-second ceiling; a budget note, not a failure, and the Architect's to decide |
| `Gateway.Tests` filtered to `CensusRouteTenancyProbeTests`, `ContextLessRouteCensusTests`, `SessionRuleRouteGuardCensusTests` | 18 passed, 0 failed, 38 seconds, at `b5abddf0b` |
| `Gateway.Tests`, full | not run by this seat (ruling E4) |
| `npm run typecheck` (4 workspaces) | clean |
| `npm test` per workspace | client-core 994, cockpit 298, mobile 14, cc-assistant 106, all green |
| `pytest tools/cc-devthrottle/tests/` | 276 passed |

**Never watched red, stated plainly:** `Evidence_covers_the_same_words_in_stored_form_whatever_their_order_or_padding`,
`Evidence_is_spent_by_the_write_it_was_minted_for_and_cannot_be_presented_again`, the structural guard's
constructor and store-caller assertions, the two reader presence tests and the two reader refusals not
named by a probe (not on the roster; empty screen), and the valid non-empty controls in both clients.
