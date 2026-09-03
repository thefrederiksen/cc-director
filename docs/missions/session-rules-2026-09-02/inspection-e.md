# Fix round D - inspection E

Inspected commit: `3020fc945a967f68eb20070b14bb2f4d345352e6` on `mission/rules-fix-d`.

Verdict: 4 findings. Ruling D2 is not closed at the persistence boundary, and ruling D8 still fails
open on malformed but present response fields.

## Findings

### 1. HIGH - Grounding is not an invariant of the rule persistence boundary

Where:

- `src/CcDirector.Gateway/Api/SessionRuleEndpoints.cs:116-151`
- `src/CcDirector.Gateway/Rules/SessionRuleStore.cs:13-28,54-129`
- `src/CcDirector.Gateway/Rules/SessionRuleRecordRules.cs:33-65`
- `src/CcDirector.Gateway.UnitTests/Rules/SessionRuleStoreTests.cs:49-94`

What is wrong:

The create endpoint re-reads the named screen and calls `WhyNotGroundedAsync` before `store.Create`,
but that endpoint is not the rule persistence boundary. `SessionRuleStore` says explicitly that it is
not the only door, then exposes public `Create` with no session, screen, or grounding capability. Its
shared database gate checks only that at least one trigger word exists; it cannot establish that any
stored word was checked against any screen. The passing
`A_rule_round_trips_through_the_store_with_every_part_intact` test is a positive control for the bypass:
it persists five trigger strings through `SessionRuleStore.Create` without calling a screen reader.

Why it matters:

D2 requires a path to a stored rule to re-run the exact grounding check. That is true for the currently
mapped HTTP create route, but false for another in-process caller or a direct database-context write.
Such a rule can later be promoted after an empty or misleading dry-run history. Comments that call the
endpoint "the one door" therefore describe a property the persistence layer does not enforce.

What would have to be true for this to be fine:

Either the ruling must be narrowed explicitly to the current mapped HTTP callers and the project must
accept convention as the boundary, or every persistence route must require non-forgeable evidence minted
by the fresh Gateway screen read. A regression test should attempt the public store and direct context
paths without that evidence, observe refusal, and include a control that reaches storage through the
real grounded route.

### 2. MEDIUM - Present but invalid client fields still become clean empty states

Where:

- `tools/cc-devthrottle/src/rule_ops.py:135-163,204-211,241-265,275-303`
- `packages/client-core/src/rules/rulesClient.ts:175-210,303-311`
- `packages/client-core/src/rules/rulesClient.test.ts:25-42`
- `tools/cc-devthrottle/tests/test_rule_ops.py:376-392`

What is wrong:

Both clients reject a missing container field but accept a present field without validating its runtime
shape. The Python `_field` returns `null` unchanged. An executable probe of `{"rules": null}` returned
`NoneType` and `list_rules` printed `No rules yet.`; `{"firings": null}` similarly selected `It has not
fired yet.` The browser client has the same boundary: a temporary test requiring `getRules()` to reject
`{"rules": null}` failed because the promise resolved `null`. The existing tests cover `{}` and the
valid empty-array control only. The command-line renderer also supplies empty strings, `(none)`, and `0`
for missing fields inside a rule or firing.

Why it matters:

A malformed or version-skewed Gateway answer is still reported as the positive fact that no rule or no
firing exists. That is the same absence-shaped failure D8 was meant to remove. It can conceal a rule or
its record, and wrong scalar types can also turn delete responses into client-authored outcomes.

What would have to be true for this to be fine:

The wire contract must guarantee these shapes before either client sees them, or both clients must
validate arrays, booleans, objects, and the required fields inside each record at runtime. Tests need
null, wrong-type, and malformed-child cases plus valid non-empty controls, not only missing fields.

### 3. MEDIUM - The production screen-provenance seam still has no executable proof

Where:

- `src/CcDirector.Gateway/GatewayHost.cs:2437-2468,2471-2477,3497-3503`
- `src/CcDirector.Gateway.Tests/CensusRouteTenancyProbeTests.cs:91-109`
- `docs/missions/session-rules-2026-09-02/fix-round-d-report.md:123-127,365-370`

What is wrong:

`GatewayHost.ReadRuleScreenAsync` is the code that joins the caller tenant, pushed roster location,
Director route, tunnel screen read, and roster-owned origin. No test calls it. The hosted rule fixture
always assigns `RuleScreenReaderForTests`, so its green HTTP tests prove middleware-to-seam handoff but
replace the exact production implementation that establishes provenance. The route-level tenant
mutation at `5bb91db4a` also reaches that fake, not the production locate-and-tunnel body.

Why it matters:

The headline claim depends on this join, especially across tenants and when a Director disappears
between roster location and screen read. Component tests and a code read are useful but cannot prove
that this production composition is called and returns the claimed screen and origin.

What would have to be true for this to be fine:

A focused test must keep the production reader in the path and observe its far side. It needs positive
presence controls for an owned session and exact screen rows, then a second tenant, a foreign session,
a vanished Director, and an empty screen, all with stated refusals and no authoring call on refusal. If
the private host method makes that impractical, extract the production composition behind an injectable
type and test that type rather than replacing it.

### 4. LOW - The reported full hosted gate result is an unresolved placeholder

Where:

- `docs/missions/session-rules-2026-09-02/fix-round-d-report.md:352-363`

What is wrong:

The gate table contains the literal `GATEWAY_TESTS_RESULT` for the full `Gateway.Tests` row while D10 is
reported as applied throughout. That is not a result. This inspection's full hosted run produced no
completion for more than ten minutes while other mission worktrees were using the shared rig, so it was
stopped rather than reported as green. Narrow runs did complete: the route-guard census passed 2 of 2,
and the four relevant HTTP tenancy, write-gate, and device-promotion probes passed 4 of 4.

Why it matters:

A placeholder cannot support the gate claim, and an unfinished run cannot be converted into a clean
result. The focused greens do not account for the full hosted suite.

What would have to be true for this to be fine:

Replace the token with the exact command, commit, count, elapsed time, and outcome of a completed run,
or state plainly that the full suite was not run. The evidence document and inspected branch must agree.

## Evidence checked

- Current rule-focused Gateway unit suite: 306 passed, 0 failed.
- Direct-store positive control: 1 passed, proving `SessionRuleStore.Create` persists without a screen
  read in its call path.
- Command-line rule suite: 21 passed. A separate malformed-response probe returned `NoneType` and printed
  the two clean absence messages described in finding 2.
- Browser-client rule suite: 3 passed. A temporary required-presence probe failed as expected with `promise
  resolved "null" instead of rejecting`; the temporary file was removed.
- Cockpit Rules page suite: 16 passed.
- Hosted route-guard census: 2 passed. Four focused HTTP tenancy, write-gate, and promotion probes passed.
- The installed command line has no `rule` command. Invoking the branch client against the running
  Gateway reached the real transport but received a session-key refusal for `GET /gateway/rules`, so it
  provides no successful running-Gateway proof for this branch.
- The mutation commits each alter the named production line, their reverts restore the source, and
  `git diff c418b6b2b 1cf5286ae -- src` is empty. The stacked failures are disclosed in the report.
  Commit `34baf2dd5` mutates only the authoring-call tenant despite its "both author seams" subject; the
  separate route mutation `5bb91db4a` is the destructive evidence for the screen-reader handoff.

## Other sharp questions

No separate defect was found in the proposal identity path, acknowledgement persistence, independent
promotion grant, store-level ceiling bounds, malformed-number refusals, route census, or the closed
primitive-call vocabulary. The four findings above prevent the round from being accepted as fully
closed.
