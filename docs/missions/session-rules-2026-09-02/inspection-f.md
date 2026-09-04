# Fix round E - inspection F

Inspected commit: `5a4075b414c2a839b2e41f50af643e8720edc971` on
`mission/rules-fix-d`.

Verdict: 2 findings. The current legitimate rule writes still work, but E1's grounding evidence can
be changed after it is minted, and E2 still accepts a malformed required child in both clients.

## Findings

### 1. HIGH - Grounding evidence can be changed after the screen check and then stores different words

Where:

- `src/CcDirector.Gateway/Rules/RuleGroundingEvidence.cs:43-61,72-81`
- `src/CcDirector.Gateway/Rules/SessionRuleStore.cs:93-112`
- `src/CcDirector.Gateway.UnitTests/Rules/RulesGroundingBoundaryGuardTests.cs:45-82`

What is wrong:

`Minted` puts the normalized words in a `List<string>` and the public `Words` property returns that same
object behind `IReadOnlyList<string>`. The interface removes mutating methods at compile time, but it does
not make the object immutable: a caller can cast `evidence.Words` back to `List<string>` and replace the
words. `Covers` then compares the words to be stored against the evidence's current, mutated contents,
not against an immutable record of what was checked on the screen.

An executable probe minted evidence for `"actually on the screen"`, asserted that `Words` was a
`List<string>`, replaced its element with `"never on the checked screen"`, and passed the changed evidence
and changed word to `SessionRuleStore.Create`. The test required `RuleRejectedException`; it failed with
`No exception was thrown`, after the real migrated SQLite store accepted the row. The temporary probe was
removed after the run.

The structural guard does not see this route. Its constructor and minter assertions remain green because
the caller neither constructs nor mints a second token; it changes the contents of a valid token.

Why it matters:

E1 says the persistence boundary binds evidence to the exact words found on a real screen. It currently
binds the write to a mutable list held by the caller, so an ungrounded word can reach `session_rules`
through the public store while every E1 guard passes. That leaves the central invariant open.

What would have to be true for this to be fine:

The evidence would have to retain an immutable private snapshot, and anything it exposes would have to be
immutable or a defensive copy. A regression test must try to change the exposed collection after minting
and prove that the changed word is refused and no row is written. Otherwise the ruling would have to be
narrowed from a persistence invariant to a convention followed by the current endpoint.

### 2. MEDIUM - A missing required scope child is accepted, and one client invents `null` for it

Where:

- `packages/client-core/src/rules/rulesClient.ts:39-45,250-282`
- `packages/client-core/src/rules/rulesClient.test.ts:83-103`
- `tools/cc-devthrottle/src/rule_ops.py:246-263`
- `tools/cc-devthrottle/tests/test_rule_ops.py:452-470`
- `src/CcDirector.Gateway/Api/SessionRuleWire.cs:25-33`

What is wrong:

The served `RuleScope` contract requires `agent`, `repository`, `machine`, and `mission`, and the Gateway
projects all four. The browser reader nevertheless treats `undefined` exactly like a legitimate `null` in
`scopePart`. The command line reader checks a scope part's type only when the key exists. A response whose
`scope` object omits `agent` is therefore accepted by both clients; the browser client additionally returns
a new object with `agent: null`, turning missing data into the widest value for that part.

An executable browser-client probe removed only `scope.agent` from an otherwise valid non-empty rule and
required rejection naming `scope.agent`. It failed because the promise resolved and showed `agent: null`.
A direct command-line reader probe over the same shape printed
`ACCEPTED_MISSING_SCOPE_AGENT={'repository': None, 'machine': None, 'mission': None}`. The temporary browser
probe was removed after the run.

The new malformed-child tests remove a top-level `triggerWords` field or change a firing's `decision`;
they do not exercise required children of `scope`.

Why it matters:

E2 says every required field inside every record is checked and a malformed or version-skewed answer is
never converted into product meaning. Here the clients accept an incomplete record, and one manufactures
meaning that the Gateway did not send. It can also produce an internally contradictory object when the
Gateway's `scopeLabel` says the rule is narrow while the reconstructed scope says that part is unrestricted.

What would have to be true for this to be fine:

The wire contract would have to make the four scope children optional, contrary to the current interface
and projection, or both readers must require each key and then accept only a string or an explicit null.
The malformed-child matrix needs a missing scope child beside the existing valid non-empty control.

## Evidence checked

- The production writer inventory contains one rule creator (`POST /gateway/rules` ->
  `SessionRuleStore.Create`), promotion, and deletion. The evaluator writes firing records, not rule rows;
  there is no rule seeder or update route, and migrations create the tables rather than saving rule
  entities.
- The feared legitimate-write refusal did not reproduce. A focused Gateway unit run covering the author,
  store, write gate, grounding structural guard, and extracted screen reader passed 93 of 93. Promotion of
  a loaded rule and deletion both persisted, so the primitive collection did not report unchanged trigger
  words as modified on those writes. A focused hosted test passed 1 of 1 and drove a grounded create through
  the HTTP route into storage.
- `TryConsume` is reached only by `SessionRuleStore.Create`. It is spent before later validation and before
  the database save, but no current route retries with the same object: every HTTP retry calls
  `RuleAuthor.GroundAsync` again. I found no current legitimate write that this ordering refuses.
- The client-core rule suite passed 15 of 15 and the command-line rule suite passed 33 of 33 before the
  additional missing-scope-child probes above exposed the uncovered shape.
- `GatewayRuleScreenReader` is the production composition, `GatewayHost.ReadRuleScreenAsync` delegates to
  it, and the focused unit run included its six tests: two positive observations, the two tenant/roster
  refusals, a vanished Director, and a distinct empty-screen result. The hosted fixture still substitutes
  the outer reader, but the extracted production type itself is exercised with only its two lower seams
  substituted, which is the remedy ruling E3 asked for; the remaining host wiring is disclosed in the
  round report.
- The E4 replacement at the inspected tip states that the full parked suite was not completed by that seat.
  Commit `3020fc945` exists, the five reported partial counts sum to 1,190 of 2,339, and the report does not
  turn that partial run into a full-suite pass. The six probe/revert pairs each restore the production and
  test tree they started from, and production plus tests are byte-identical from the reported green commit
  `b5abddf0b` to the inspected tip. I did not rerun the full parked suite, as ruling E4 assigns that final
  merged-tree run to the Architect.
- The red-first commit `82ec0b65a` is the direct parent of the E1 fix. The successful E1 and E3 mutation
  probes name the changed behavior, and the report separately lists the load-bearing tests that were never
  observed red; I found no new load-bearing test omitted from both groups.
