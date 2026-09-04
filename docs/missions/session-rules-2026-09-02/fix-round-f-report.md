# Fix round F - the Manager's report

Branch `mission/rules-fix-f`, cut on top of fix round E and the merged `inspection-f.md`
(`63ce5a91a`), with the Architect's rulings at `1b881bcf3`. Two findings, three rulings, one section
each, saying whether it is closed and what proves it. Nothing the earlier rounds closed was reopened
and nothing was improved in passing.

**Where the work is.** `fix-round-f.md` says to push on `mission/rules-fix-d`; this seat was placed on
`mission/rules-fix-f`, which is `mission/rules-fix-d` plus the inspection and the rulings. The work is
pushed there. Nothing was merged and no pull request was opened.

The evidence rule was applied as in rounds D and E: the new tests were committed and run BEFORE any
fix, every mutation probe was a commit of its own reverted with `git revert --no-edit HEAD` before the
next, and the diff against the tree it started from was checked empty each time.

---

## The red commit, before any fix: `496b7315a`

```
dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~SessionRuleStoreTests"
  SessionRuleStoreTests.The_words_the_evidence_exposes_cannot_be_written_to [FAIL]
    the evidence exposed System.Collections.Generic.List`1[[System.String, ...]], which a caller can write to.
  SessionRuleStoreTests.Evidence_changed_after_it_was_minted_refuses_the_changed_word_and_writes_no_row [FAIL]
    Assert.Equal() Failure: Collections differ
    Expected: string[]     ["limit"]
    Actual:   List<string> ["never on the checked screen"]
Failed!  - Failed:     2, Passed:    34, Skipped:     0, Total:    36

packages/client-core: npx vitest run src/rules
  x  rules: a rule whose scope is missing a required child is an error naming it
     resolved with scope { agent: null, machine: null, mission: null, repository: null }
 Tests  1 failed | 17 passed (18)

python -m pytest tools/cc-devthrottle/tests/test_rule_ops.py -q
  FAILED test_a_rule_whose_scope_is_missing_a_required_child_is_an_error_naming_it
    Failed: DID NOT RAISE <class 'cc_shared.gateway.GatewayError'>
  1 failed, 35 passed
```

**The second store test stopped at its first assertion, so the WRITE half had not been watched.** That
matters: an exception thrown after a write is not a write that never happened, and the ruling asks for
both halves. A probe (`61f42aa1a`) lifted the snapshot assertion so the store call was reached, on the
same unfixed tree:

```
dotnet test ... --filter "FullyQualifiedName~Evidence_changed_after_it_was_minted"
  Error Message:
   Assert.Throws() Failure: No exception was thrown
```

That is the inspection's own symptom reproduced here: the changed word was accepted and the row was
written. Reverted by `d14dc732e`; diff against `496b7315a` empty.

---

## RULING F1 - the evidence is an immutable snapshot, not a view of a mutable list. CLOSED.

**Commit `984e20828`.** `RuleGroundingEvidence` holds a private `readonly ImmutableArray<string>`
taken at minting from the words that were just checked against the screen. `Covers` compares against
that field and never against the exposed property, and `Words` returns the immutable array, so a
caller that casts it gets a collection which refuses to be written to rather than the store of words
the evidence reasons about. The constructor takes `IEnumerable<string>` and copies; nothing a caller
holds reaches the snapshot.

**The regression test proves both halves, as the ruling requires:**

- `The_words_the_evidence_exposes_cannot_be_written_to` - a PRESENCE assertion. The exposed collection
  must be a read-only list, and writing to it must throw. A test that merely observed no mutation would
  be absence-shaped.
- `Evidence_changed_after_it_was_minted_refuses_the_changed_word_and_writes_no_row` - the inspection's
  probe kept. It mutates the exposed collection if it can, then asserts the snapshot survived, the
  store refuses naming the words it was really minted for, and `store.All()` is EMPTY. The row count is
  asserted because the refusal alone does not say the write never happened.

**Mutation probe:**

```
00b21b3be  the evidence holds a live list again instead of a snapshot
  SessionRuleStoreTests.The_words_the_evidence_exposes_cannot_be_written_to [FAIL]
  SessionRuleStoreTests.Evidence_changed_after_it_was_minted_refuses_the_changed_word_and_writes_no_row [FAIL]
  Failed: 2, Passed: 34        reverted by 332a2841f, diff against 1d8cad4b0 empty
```

**The same pattern elsewhere on the rules surface, as the ruling asked - a checked-then-trusted value
exposed over a mutable backing store:**

| Where | Exposed as | Verdict |
| --- | --- | --- |
| `RuleGroundingEvidence.Words` | was `IReadOnlyList` over a `List` | THE FINDING. Now an `ImmutableArray` |
| `SessionRule.TriggerWords`, `.Calls` | `IReadOnlyList` over the list the store built | Harmless. The record is a projection READ OUT of the database, not a token anything trusts on the way IN; changing a copy changes nothing that is checked. The store already takes a defensive `CopyOf` of each call on the way in |
| `RuleProposal.TriggerWords`, `.Calls` | `IReadOnlyList` over the reader's lists | Harmless for the same reason, and more strongly: the proposal is not evidence. The write route re-reads the screen and re-runs the whole check on whatever body comes back, so a mutated proposal is re-checked rather than trusted |
| `RulePrimitiveCall.Arguments`, `RuleArgument.Values` | public `List<T>` with public setters | Deliberately mutable - it is a document being built, not a token. Validated at every boundary it crosses, and the store copies it |
| `RulePrimitiveRegistry.Primitives` | `IReadOnlyList` over a built list | Built once from the assembly at start-up. Lookups go through `Find`, which reads a private dictionary, so a mutated view would not change what a check resolves to |
| `RulePromotionGrant` | holds only strings and a Guid | Nothing to mutate. This is why promotion never had the hole |

The distinction that matters, and it is the lesson rather than the list: **only `RuleGroundingEvidence`
was a token whose contents a later gate TRUSTED without re-deriving them.** Everything else on this
surface is either re-checked downstream or is a projection nobody checks. A read-only view is fine for
a value; it is never enough for evidence.

## RULING F2 - a required child that is absent is a broken answer, and is never filled in. CLOSED.

**Commit `1d8cad4b0`.** Both readers now require each of the four scope keys and then accept only a
string or an explicit null.

- `rulesClient.ts` `scopePart` refuses a key that is not present at all, naming `scope.<field>`, before
  it looks at the value. `null` still means "any"; `undefined` is no longer a synonym for it. `kindOf`
  gained "nothing at all", so the refusal says the field did not arrive rather than calling it
  undefined.
- `rule_ops.py` `_read_rule` refuses a missing scope child with its own sentence - "That is not an
  unrestricted rule; it is an answer this command cannot read" - and keeps the existing type check for
  a child that is present.
- The malformed-child matrix gains a missing scope child in both clients, each beside a valid non-empty
  control that reads the four children back unchanged.

**Mutation probes:**

```
c24aa418e  the browser reader treats an absent scope child as null again
  rules: a rule whose scope is missing a required child is an error naming it [FAIL]
  Tests 1 failed | 17 passed          reverted by 94a20ba2b, diff empty
1c512800f  the command line reader checks a scope child only when the key exists
  FAILED test_a_rule_whose_scope_is_missing_a_required_child_is_an_error_naming_it
  1 failed, 35 passed                 reverted by 1a57ec647, diff empty
```

**On the internal contradiction, and how far it actually reached.** The ruling asks that a Gateway
stamped label can never disagree with a client-reconstructed scope. Two things are now true:

1. **Nothing renders the reconstructed scope.** `RulesView.tsx` reads `scopeLabel` and `waitLabel` and
   never touches `rule.scope` - verified by grep over the page, which finds the two labels at lines
   335, 338, 600 and 604 and no read of `.scope` at all. So the manufactured `agent: null` never
   reached a screen. It reached the typed object every consumer of `client-core` gets, which is where
   the next reader would have found it.
2. **The label stamper itself had the same habit, and now does not** - see ruling F3 below, which is
   where the sweep found it.

## RULING F3 - the sweep for the CLASS: method, coverage, and what it would have missed

The ruling is right that this is one habit rather than four slips, so this section reports HOW the
surface was enumerated before it reports what was found. "I looked and found none" is an
absence-shaped claim and this section does not make one.

### The method, in the order it was run

**Step 1 - derive the surface, do not hand-keep it.** The file list is derived with the same predicate
the feature's own structural guard uses (`RulesTypeNothingGuardTests.IsFeatureType`): the
`CcDirector.Gateway.Rules` namespace, plus any type carrying `[RuleFeature]` wherever it lives, plus
the stored-row types named `SessionRule*` or `RulePrimitive*` in the entities namespace.

```
grep -rl "^namespace CcDirector\.Gateway\.Rules;" src/CcDirector.Gateway --include=*.cs
grep -rl "\[Rules\.RuleFeature\]" src/CcDirector.Gateway --include=*.cs
grep -rl "^\[RuleFeature\]"       src/CcDirector.Gateway --include=*.cs
ls src/CcDirector.Gateway/Data/Entities/SessionRule*.cs src/CcDirector.Gateway/Data/Entities/RulePrimitive*.cs
```

That derives **35 Gateway files.** Four more were added BY HAND and are disclosed as such, because the
derived predicate does not reach them: `GatewayDbContext.cs` (the write gate - it is the whole
Gateway's context, so it carries no rules marker), `packages/client-core/src/rules/rulesClient.ts`,
`apps/cockpit/src/rules/RulesView.tsx` and `tools/cc-devthrottle/src/rule_ops.py`. **39 files.**

**Step 2 - enumerate the sites mechanically.** One pattern for every syntactic form in which an absent,
null, empty or unreadable value can BECOME a value, in all three languages: `??`, `?.`, `||`, `or`,
`is null`, `== null`, `=== null`, `=== undefined`, `is None`, `IsNullOrWhiteSpace`, `IsNullOrEmpty`,
`TryGetProperty`, `TryGetValue`, `.get(`, `FirstOrDefault`, `GetValueOrDefault`, `Array.Empty`,
`JsonValueKind.Null`, `not in`, `in scope`, `setdefault`. Comment-only lines excluded.

**383 sites across 32 files** (seven of the 39 have none). Every one of the 383 was read.

**Step 3 - a second pass for the VACUOUS-TRUTH forms the first pattern cannot see**, because those are
the shape this mission keeps being bitten by: `.All(`, `.every(`, `all(`, `.Any(`, `.some(`,
`DefaultIfEmpty`, `TryParse`, `GetValueOrDefault`. **21 further sites, all read.** There is no `.All(`
or `.every(` anywhere on the surface. Every `.Any(` asks "is there a bad one", which answers false on
an empty sequence and is the safe direction; `SessionRuleRecordRules` uses `!...Any(...)` to REFUSE an
empty word list, which is a presence check. Both `TryParse` failures are refusals.

**Step 4 - classify each site.** Three buckets: (A) an absence becomes a value that means something;
(B) an absence becomes a refusal, a fault, or the NARROWER value; (C) not an absence conversion at all
(a null-argument guard, or a test for a branch that produces no value).

### What the sweep found

**One defect, fixed.** Commit `69ec67e83`.

`RuleLabels.Scope(RuleScope? scope)` answered **"every session"** for a null scope. That is the exact
habit: an absent value becoming the widest one it could mean - and worse than the other three, because
a stamped label is what a client renders VERBATIM (ruling D8), so it would have put the widest sentence
there is in front of a person on the strength of a scope nobody ever said. The parameter is now
non-nullable and a null scope throws, so the compiler refuses the shape at any future call site.
A scope whose four parts are all blank still labels as "every session", and that is correct - that IS
every session, said out loud, and it is `RuleScope.AllSessions`.

Red first, on `SessionRuleWireTests`:

```
SessionRuleWireTests.A_scope_that_is_not_there_at_all_is_a_fault_and_never_the_widest_label [FAIL]
Failed!  - Failed: 1, Passed: 19, Total: 20
```

with `A_scope_that_says_every_session_out_loud_is_labelled_every_session` as the control beside it, so
the guard cannot pass on a stamper that refuses everything. Reachability, stated honestly: both
production callers (`SessionRuleWire.Project`, twice) pass a non-nullable property, so **this was a
latent fail-open rather than a live one.** It is fixed because the default was wrong, not because it
was firing.

**One site the sweep found, referred to the Architect, and then FIXED on his ruling.** Commits
`aa44c1c84` (red) and `1eda859bf` (fix).

`RuleTriggerWords.WhyNotGrounded` answered **"grounded"** for an EMPTY list of trigger words. The check
asks whether every trigger word is on the screen; over no words that question has no work to do, so it
passed. That is a check answering its own absence, sitting in the one function that defines what
grounding means for the whole feature.

I reported it and recommended deferring it, because closing it breaks a round E store test and the
round was told not to grow. **The Architect overruled that, and the reasoning is the part worth
keeping:** the argument for leaving it was that two later gates refuse a wordless rule anyway, and that
is the argument this mission has now watched fail twice in two days - grounding was itself believed to
be the backstop behind the model's judgement until phase 1 measured it waving a confident wrong act
through, and the structural guard was believed to protect the evidence token until an inspector edited
a valid one straight past it. A backstop is worth what it is worth on the day the thing in front of it
fails, and the definition of grounding is the last place to be relying on one. He also set the test
that made it cheap: an empty trigger-word list is never legitimate, because a rule with no words could
never fire and the store already demands at least one, so refusing it costs nothing real.

**The fix is one function.** `WhyNotGrounded` normalises the words once and refuses an empty list with
its own sentence, before it looks at the screen at all.

**Red first, and the defect executed rather than inferred:**

```
dotnet test src/CcDirector.Gateway.UnitTests --filter "FullyQualifiedName~watches_for_nothing"
  RuleAuthorTests.The_write_gate_refuses_a_rule_that_watches_for_nothing [FAIL]
    Assert.NotNull() Failure: Value is null
  SessionRuleStoreTests.A_rule_that_watches_for_nothing_cannot_be_grounded_so_it_never_reaches_the_store [FAIL]
    Assert.Throws() Failure: No exception was thrown
    Expected: typeof(System.InvalidOperationException)
```

"Value is null" is the author returning NO refusal at all for a rule that watches for nothing.

**Mutation probes, each reverted before the next:**

```
35cceacd9  grounding answers yes for an empty word list again
  RuleAuthorTests.The_write_gate_refuses_a_rule_that_watches_for_nothing [FAIL]
  SessionRuleStoreTests.A_rule_that_watches_for_nothing_cannot_be_grounded_so_it_never_reaches_the_store [FAIL]
  Failed: 2, Passed: 326      reverted by 2954e9115, diff against 1eda859bf empty
a41fe9bbf  the store no longer asks what a rule watches for
  RuleAuthorTests.A_rule_the_store_would_refuse_is_not_offered_and_says_the_stores_reason [FAIL]
  Failed: 1, Passed: 0        reverted by 7d12486e6, diff empty
```

**TWO tests broke, not one, and the ruling asked for the reason in each case before changing it.**

1. `SessionRuleStoreTests.A_rule_with_no_trigger_words_is_refused_because_it_would_cost_a_model_call_every_time`
   **depended on the defect without asserting it.** Its subject is the store's own "at least one word"
   rule, and the only way to reach that rule through the store's public door was to hand it evidence
   covering an empty set - which `Grounded.For(Array.Empty<string>())` could produce only because
   grounding waved the empty set through. It could not simply be given words, because having no words
   is its subject. So it is repointed at the gate that now refuses first, and renamed
   `A_rule_that_watches_for_nothing_cannot_be_grounded_so_it_never_reaches_the_store`. **The store's own
   rule is still covered where it is still reachable** - a rule written straight through the context,
   which does not pass grounding at all:
   `RulesWriteGateTests.A_rule_written_straight_through_the_context_still_has_to_be_a_rule` with its
   "trigger words" case. No coverage was lost; it moved.
2. `RuleAuthorTests.A_rule_the_store_would_refuse_is_not_offered_and_says_the_stores_reason` broke for
   the same reason. It is NOT the round E test, so it is reported separately rather than folded in. Its
   subject is that the store's validator is asked early, so the store's OWN sentence is what a person
   reads. Its example was a drafted rule with no trigger words, which grounding now catches first. The
   example is now a drafted rule that does not say what it watches FOR - a store rule nothing upstream
   pre-empts - so the test goes on proving the thing it was written for. It was watched red under the
   second probe above, so the repointed version is not decoration.

**One thing this fix does NOT close, named rather than left.** `RuleGroundingEvidence.Minted` runs its
own copy of the check (`NotOn` directly, not `WhyNotGrounded`), so the TYPE would still mint evidence
for an empty set if it were reached with one. It cannot be reached with one: `Minted` is internal, a
structural test asserts `RuleAuthor` is its only production caller, and that caller now refuses the
empty set immediately upstream in the same method. I left it alone because the ruling said one function
and nothing else. **It is the remaining place, and the file's own comment names the hazard - "a check
that exists twice is a check that can be different twice" - so unifying `Minted` on `WhyNotGrounded` is
recommended for a round that has room.**

**Everything else was read and is correct.** The ones worth naming, because each LOOKS like the class:

| Site | Absence becomes | Verdict |
| --- | --- | --- |
| `RuleCandidateFilter.PartOutOfScope` - the scope part is unset | "any", so the rule applies | Correct. An unset part is a stored, deliberate choice: the store refuses a rule with no scope at all, and the wire reader refuses an object whose four parts are blank. "All sessions" is sayable, and has to be said |
| `RuleCandidateFilter.PartOutOfScope` - the session FACT is unreadable | out of scope, so the rule does NOT fire | Correct, and it is the answer that matters: an unreadable fact narrows, never widens. Same for `PathsAreTheSamePlace`, where an empty repository path is not a match |
| `GatewayRuleEnvironment.ReadSessionFacts` - a roster field is null | the empty string | Correct, because of the row above: an empty fact can only fail a scope test, never pass one |
| `GatewayRuleScreenReader` - the roster row has no agent | `RuleSessionOrigin` with an empty agent, `IsKnown` false | Correct, and it is the best precedent on this surface. BOTH authoring routes REFUSE an unknown origin outright rather than letting the model choose the scope - `RuleAuthor.ReadScreenAsync` and `RuleDraftContract.Read` each say so in their own sentence |
| `RuleAgentContract.Read` - any field of the model's answer missing | the empty string, then a refusal naming what was missing | Correct throughout. A missing `decision` is not a decision, an act with no reason is refused, an act with nothing to type is refused. Not one absence becomes permission |
| `RuleDraftContract.Read` - the model's drafted scope is absent | refused with a sentence | Correct, and it already says so: it calls reading an omitted scope as widest "the fail-open" |
| `RuleReasonGrounding` - the reason quotes nothing | grounded, but `HasCitation` false, and `CanCarryAnAct` is false | Correct, and this is the same class already caught and fixed earlier in this mission. An act needs a citation present AND correct; a decline needs neither, and its record says which it had |
| `SessionRuleStore.CompleteFiring` - the rule is gone | the dry-run check is skipped and the typed text is recorded | Correct, and it is the safe direction here: the keystroke already went out, and the record is the product. Refusing would lose the record of something that really happened |
| `SessionRuleStore.Blank` - a scope part is an empty string | null, which means "any" | Correct in effect, and worth knowing WHY: it is safe because `SessionRuleWire.ReadScope` refuses an object of four blanks first ("the same omission wearing a pair of braces"). A direct store caller passing four empty strings would get every session |
| `SessionRuleWire.TryNumber` - a ceiling is absent | zero, which the ceilings refuse | Correct. Absence becomes a value the bounds reject |
| `SessionRuleWire.Flag` - `allAgents` is absent | false, the NARROWER choice | Correct, and the right direction by construction |
| `SessionRuleWire.Strings` - `triggerWords` absent | an empty list | Refused by the store and by the write gate. This is the site whose grounding half is the recommendation above |
| `RuleCallValidator.ValidateAll(null)` | valid | Correct. A rule with no checks is a legitimate rule; a MISSING `checks` property is refused earlier, by the reader, when it is required |
| `RulePrimitives.MatchesAny` - no text or no terms | false | Correct. Fails closed, and it is the primitive the candidate filter runs |
| `rule_ops.RuleClient.screen` - the `text` field is missing | "empty screen, nothing to write a rule against" | Correct on safety - it is a refusal either way - but it cannot tell a missing field from a genuinely blank screen. Named, not changed: it refuses in both cases |
| `GatewayRuleEnvironment` and `RuleTurnEndLauncher` - `enterTenantScope` is null | no tenant scope is entered | Correct as designed and documented ("self-host has one partition and the scope is inert"). Verified that production always supplies it: `GatewayHost.cs:2419` and `GatewayHost.cs:2591`. The null path is a test seam |

### What this sweep would have missed - stated, because the enumeration is not the territory

1. **An absence-to-value written in a form neither pattern names.** A `switch` default arm, a
   dictionary lookup with a supplied default, a `Convert.ToString` of a null, a value defaulted by a
   constructor rather than at the point of use. Both passes are syntactic.
2. **An absent BEHAVIOUR rather than an absent VALUE.** A code path that is skipped, a gate that is
   never reached, a caller that forgets to call the check at all. That is the shape of the
   `SessionKeyGuard` defect this mission already found, and no search for a coalescing operator can
   see it.
3. **Anything outside the 39 files.** The derived predicate misses the write gate, which had to be
   added by hand - so a future rules type placed outside the namespace and without the `[RuleFeature]`
   marker would be missed the same way. The mobile app was checked and has no rules surface at all.
4. **Cross-file conversions.** A function that correctly returns null, whose CALLER two files away
   coalesces it into a value. Each site was judged with its callers checked where the value could be
   permission-shaped, but that check was by hand and is the weakest part of this method.
5. **The judgement itself.** The enumeration is derived; the verdict on each site is mine.

---

## The gate for round F

| Suite | Result |
| --- | --- |
| `.\scripts\test-local.ps1` (9 projects) at `7d12486e6` | green. Every outcome Completed; 4,928 total, 0 failed, 2 skipped. **Run twice, and the first run is reported as well as the second:** the first flagged OVER BUDGET and printed FAIL and NO-TRX for `Gateway.UnitTests`, which took 2 minutes 18 seconds against the 120-second ceiling. That suite had in fact passed - its result file reads `outcome=Completed total=3635 passed=3633 failed=0` - and the verdict read the file before it was flushed. The re-run came back inside budget at 1 minute 1 second with every outcome Completed. It is a budget flag under load, the same one round E reported at 2 minutes 8 seconds, and it is the Architect's to decide |
| `Gateway.Tests` filtered to `CensusRouteTenancyProbeTests`, `ContextLessRouteCensusTests`, `SessionRuleRouteGuardCensusTests` | 18 passed, 0 failed, 33 seconds |
| `Gateway.Tests`, full parked suite | NOT run by this seat (ruling E4). It is the Architect's, once, on the final merged tree |
| `npm run typecheck` (4 workspaces) | clean |
| Every web workspace | client-core 997, cockpit 298, mobile 14, cc-assistant 106 - all green |
| `pytest tools/cc-devthrottle/tests/` | 279 passed |
| `Gateway.UnitTests` rules filter, run on its own after each change | 328 passed, 0 failed |

The local gate reported the coverage gap it always reports here: `Core.Tests` and `Gateway.Tests` are
parked and did not run. That is ruling E4 operating as intended, not an unnoticed hole.

## Never watched red, stated plainly

These passed but were never observed failing, so they are decoration until something proves otherwise:

- `A_scope_that_says_every_session_out_loud_is_labelled_every_session` - the control beside the label
  fix. It would fail if the stamper refused everything, which is what it is there for, but that was not
  executed.
- `RulesWriteGateTests.A_rule_written_straight_through_the_context_still_has_to_be_a_rule` with its
  "trigger words" case. It is now the ONLY place the store's own no-words rule is proven, and this round
  did not watch it fail - it was already green and was not touched. Named because its importance went up
  in this round while its evidence did not.
- `rules: a scope child of the wrong type is an error naming it` (browser) and
  `test_a_scope_child_of_the_wrong_type_is_an_error_naming_it` (command line). **Both passed on the
  UNFIXED tree.** They are controls, not regressions: the existing readers already refused a child of
  the wrong TYPE, and the defect was only ever about a child that was not there. They are kept so the
  two readers cannot drift apart on the type check either.
- `rules: a scope whose four children are all present is read as what it carries` and
  `test_a_scope_with_all_four_children_is_read_as_what_it_carries` - the valid non-empty controls.
  Also green before the fix, by design.
- Every store, author and write-gate test from rounds D and E that this round re-ran and did not touch.
  Round E's report lists which of those it watched red; this round adds nothing to that list.

## What is NOT proven by this round

- **The full parked `Gateway.Tests` suite has not run against this tree.** Ruling E4 assigns it to the
  Architect on the final merged tree, and this seat did not run it. The three narrow classes above are
  the only host-bound coverage this round has.
- **Nothing here was exercised against a live Gateway.** Every proof in this round is a unit or
  workspace test. The demonstrations remain the deliverable.
- **The sweep's verdicts are judgements**, one per site, against a derived enumeration. The section
  above says what the method cannot see.
