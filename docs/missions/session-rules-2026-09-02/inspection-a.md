# Session Rules - landing A inspection

- Landing: A
- Branch inspected: `mission/session-rules-p1`
- Commit inspected: `654c6c04d`
- Diff inspected: `git diff origin/main...654c6c04d`

## Verdict

CHANGES REQUIRED. I found nine findings. The first three block this landing: the dry-run boundary
has no enforced human gate, the claimed write-time gate can be bypassed through the public data
surface, and two of the five red-first claims are not reproducible from the commits named in the
report.

## Findings

### 1. The dry-run promotion path has no human boundary

**What is wrong:** `SessionRuleStore.Promote` is a public method that accepts only a rule id and a
timestamp. It takes no authenticated actor, human grant, capability, or authorization callback.
Any code that can read or record through this store can also move a rule to live. The test named
`A_new_rule_is_always_in_dry_run_and_only_a_person_moves_it` proves only that calling `Promote`
changes the state; the caller in that test has no property that makes it a person.

**Where:** `src/CcDirector.Gateway/Rules/SessionRuleStore.cs:154-172` and
`src/CcDirector.Gateway.UnitTests/Rules/SessionRuleStoreTests.cs:200-210`.

**Why it matters:** dry run is the owner's most important safety boundary. The future evaluator
needs to read rules and record firings. Giving it this concrete store also gives it the ability to
promote its own rule, which is one of the actions the instruction is explicitly forbidden to take.

**What would have to be true for this to be fine:** promotion would have to sit behind a distinct,
authenticated human-only capability that automated evaluation code cannot obtain, while evaluation
receives a narrower interface with no promotion method. A test would need to show that a non-human
caller is refused, not merely that a direct call succeeds.

### 2. The validator is not the write gate claimed by the code and report

**What is wrong:** the rule entity, its `Calls` and `State` setters, the `SessionRules` `DbSet`, and
the database context factory are all public. A Gateway caller can create a context, construct a
`SessionRuleEntity` with an arbitrary call document, arbitrary `tenant_id`, and `State = "live"`,
then add and save it without calling `SessionRuleStore.Create` or `RuleCallValidator`. There is no
database constraint, `SaveChanges` interceptor, or architecture guard that closes this route. This
contradicts the source claim that nothing reaches `session_rules` without the validator.

**Where:** `src/CcDirector.Gateway/Data/Entities/SessionRuleEntity.cs:20-58`,
`src/CcDirector.Gateway/Data/GatewayDbContext.cs:228`,
`src/CcDirector.Gateway/Data/GatewayDatabase.cs:478-507`, and the contradicted claim at
`src/CcDirector.Gateway/Rules/SessionRuleStore.cs:13-17`.

**Why it matters:** one bypass defeats three properties at once: calls need not name a shipped
primitive, a new rule need not begin in dry run, and a write need not belong to the active tenant.
The current store path behaves correctly, but the claimed structural boundary does not exist. A
later authoring or evaluator path can accidentally persist a shape that the executor must never
trust.

**What would have to be true for this to be fine:** direct writes of these entities would have to be
made inaccessible or mechanically rejected outside the validated store, including a check that the
written tenant equals `ActiveTenant`; alternatively, every reader would have to revalidate and
refuse the row before any use, and an architecture test would have to prove that no direct write
route exists.

### 3. Two claimed red commits contain no tests to run

**What is wrong:** the report says the validator's 18 tests failed on `84c25911e` and the store's 21
tests failed on `522b1cee5`. Clean checkouts of those commits do not contain the named test classes.
At `84c25911e`, filtering for `RuleCallValidatorTests` exits 0 with `No test matches`; at
`522b1cee5`, filtering for `SessionRuleStoreTests` does the same. `git ls-tree` confirms the validator
test file first appears after `84c25911e`, and the store test file first appears after `522b1cee5`.
These are zero-test runs, not red runs.

**Where:** the claims are in
`docs/missions/session-rules-2026-09-02/phase-1-report.md:9,112-119` and repeated in
`docs/missions/session-rules-2026-09-02/qa-report.md:109-111`.

**Why it matters:** red-first is a binding acceptance condition for the mission, and the report says
each number identifies the commit it ran on. A dirty, uncommitted working tree might have produced
the quoted failures, but the named commit does not identify that tree and cannot reproduce the
evidence. Worse, the filtered runner returns success when the tests are absent, which is exactly the
zero-work condition the mission's proof rules require treating as a broken instrument.

**What would have to be true for this to be fine:** an immutable artifact would have to identify the
complete dirty tree that actually ran, including the missing test files and exact command. Under the
mission's stated rule, the straightforward repair is committed red probe commits for both features,
with a positive collected-test count and the failing results recorded from those commits.

### 4. The stored-call contract admits inputs beyond the terminal screen

**What is wrong:** `RuleInput` exposes `SessionRepositoryPath`, `Now`, and `FirstFailure` alongside
`ScreenText`. A stored argument can bind to any of them, and the validator accepts the binding when
its value kind matches. The single `Calls` collection has no type or stage that limits these inputs
to a post-decision safety check, so the eventual evaluator can use repository state or elapsed time
to decide whether a rule applies.

**Where:** `src/CcDirector.Gateway/Rules/RuleInput.cs:15-32`,
`src/CcDirector.Gateway/Rules/RulePrimitiveCall.cs:51-56`, and
`src/CcDirector.Gateway/Rules/RuleCallValidator.cs:113-129`. The owner ruling it conflicts with is
`docs/missions/session-rules-2026-09-02/brief.md:54-56`.

**Why it matters:** the owner explicitly deferred waiting time and machine state and ruled that the
terminal screen is the only input. This phase creates the wider contract now, even though no
evaluator invokes it yet. Once rows using those bindings exist, later phases inherit a condition
shape that can exceed the owner's boundary.

**What would have to be true for this to be fine:** the owner would have to rule that these values
are allowed inputs, or the contract would have to distinguish screen-only applicability from a
separate action-safety stage and mechanically prevent the extra inputs from affecting whether the
instruction applies.

### 5. A missing scope silently widens to every session

**What is wrong:** `Create` accepts a nullable value at runtime despite its annotation, then turns
`null` into `RuleScope.AllSessions`. The contract therefore cannot distinguish "the account chose all
sessions" from "the authoring output omitted scope." No test supplies a missing scope.

**Where:** `src/CcDirector.Gateway/Rules/SessionRuleStore.cs:98` and the scope contract at
`src/CcDirector.Gateway/Rules/SessionRule.cs:18-26`.

**Why it matters:** scope is a real safety bound, and the instruction is forbidden to widen its own
scope. Defaulting malformed or incomplete authoring output to the widest possible scope is a
fail-open choice.

**What would have to be true for this to be fine:** `null` would have to be an explicit, reviewed
wire representation for a confirmed account choice of all sessions. Otherwise the store must refuse
a missing scope, with all sessions represented by a distinct non-null value.

### 6. The types-nothing guard sees only direct references in one namespace

**What is wrong:** `TypesReachingTheSeam` examines only each selected method's immediate IL operands
and reports a hit only when the operand's declaring type starts with the hand-kept
`DirectorCommandRouter` name. It does not traverse calls. A rules method can call
`SessionVerbClient.PostPromptAsync`, an HTTP helper, or any helper in another namespace that reaches
the router, and the guard stays green. The final assertion also selects only the
`CcDirector.Gateway.Rules` namespace, although this landing adds feature entities in
`CcDirector.Gateway.Data.Entities`.

**Where:** `src/CcDirector.Gateway.UnitTests/Rules/RulesTypeNothingGuardTests.cs:21-28,38-64,113-122`.
The overclaim that this solves a call sitting one helper away is in
`docs/missions/session-rules-2026-09-02/phase-1-report.md:98-106`.

**Why it matters:** the known-positive probe proves only that the scanner recognizes one direct
reference spelling. It does not prove reachability, exhaustive typing seams, or coverage of all
phase-1 product types. The guard can pass while typing happens.

**What would have to be true for this to be fine:** every possible typing route would have to be
mechanically forced to reference the named router directly from a type in the rules namespace. No
such architecture boundary is present. Otherwise the check must derive the changed product surface
and follow the call graph to a derived set of sending seams.

### 7. The suite does not enforce that exactly the five approved primitives ship

**What is wrong:** the registry test carries the five approved signatures, but only checks that each
is present. It never compares that external contract with the complete registry. The other
completeness test compares two sets derived from the same attributes. Adding a sixth attributed
public static method with supported parameter types makes it legal and leaves both tests green.

**Where:** `src/CcDirector.Gateway.UnitTests/Rules/RulePrimitiveRegistryTests.cs:41-80` and the act
that expands the surface at `src/CcDirector.Gateway/Rules/RulePrimitiveAttribute.cs:8-12`.

**Why it matters:** a new general-purpose or pattern-taking primitive is the route by which an
interpreter can return under another name. The plan says five primitives ship in this phase and that
adding one is a reviewed product change; the suite does not detect that change. The five current
implementations do use fixed reviewed patterns, and I found no caller-supplied pattern among them.

**What would have to be true for this to be fine:** the five-item ruling would have to be a minimum,
not a closed approved set, and primitive expansion would have to rely entirely on review policy.
Otherwise the external expected set must be compared with every registry entry, while registry
construction itself remains derived.

### 8. A null argument element crashes the write-time validator instead of producing a refusal

**What is wrong:** `RulePrimitiveCall.Arguments` is a mutable JSON-shaped list. If it contains a null
element, `Validate` dereferences that element in the first `GroupBy` and throws
`NullReferenceException`. The store catches no such exception and therefore does not return the
stated `RuleRejectedException` reason promised for malformed calls. The existing wrong-argument
cases do not include a null element.

**Where:** `src/CcDirector.Gateway/Rules/RulePrimitiveCall.cs:71-77`,
`src/CcDirector.Gateway/Rules/RuleCallValidator.cs:56-60`, and
`src/CcDirector.Gateway/Rules/SessionRuleStore.cs:90-96`.

**Why it matters:** later authoring output is precisely the boundary at which malformed collection
elements must be expected. A malformed call can turn a stated refusal into an unhandled Gateway
failure.

**What would have to be true for this to be fine:** a schema or deserialization layer that cannot
produce null collection elements would have to be the exclusive route to this public store API and
be tested as such. No such route exists in landing A.

### 9. The firing store accepts an empty or invented record

**What is wrong:** `RecordFiring` changes null session, understanding, decision, reason, and outcome
values into empty strings and accepts them. It also accepts arbitrary primitive-run names,
arguments, and answers without checking them against the rule or registry. A decline with no reason
and a claimed check that never ran are both valid writes.

**Where:** `src/CcDirector.Gateway/Rules/SessionRuleStore.cs:198-248`, especially lines 231-241. This
contradicts the entity's own statement at
`src/CcDirector.Gateway/Data/Entities/SessionRuleFiringEntity.cs:27-37` that the record includes why
the decision was made and which verified checks ran.

**Why it matters:** the owner ruled that the record is the product. A schema that merely has columns
for the record but accepts absence and self-testimony cannot establish what happened. The positive
round-trip test supplies constants for the primitive run; it does not tie the recorded answer to a
primitive execution.

**What would have to be true for this to be fine:** a single trusted evaluator would have to be the
only caller and would have to produce a validated firing type that cannot represent blank required
fields or unearned primitive results. The public store contract currently enforces neither condition.

## What I examined and ran

I read the complete landing diff, the brief, plan, phase report, accumulating QA report, all
non-generated rule/store/entity code, both migrations and their model-snapshot changes, all five new
test files, and the existing tenant-scope guards reached by the new entities.

Short filtered runs only:

- `a8259bcbb`: all then-present rules tests, exit 1, 33 failed and 1 passed. Reproduces the first red.
- `84c25911e`: validator-test filter, exit 0, no matching tests. Does not reproduce the claimed red.
- `522b1cee5`: store-test filter, exit 0, no matching tests. Does not reproduce the claimed red.
- `c991921d2`: types-nothing guard, exit 1, 1 failed and 2 passed. Reproduces the named probe red.
- `7a7422119`: the tenant-primary-key guard alone, exit 1, 1 failed. It names both new entities.
- `654c6c04d`: all rules tests, exit 0, 76 passed; all tenant-scope guards, exit 0, 5 passed.
- `654c6c04d`: SQLite and Postgres `has-pending-model-changes`, exit 0 for each, both reporting no
  model changes since the last migration.

I did not run the long parked Gateway suite. Within the intended `SessionRuleStore` path, I confirmed
that rule and firing reads are scoped by the active-tenant query filters, foreign-tenant rule ids are
not visible, both entities use Gateway-minted ids, and both provider migrations carry tenant-leading
read indexes. I found no caller-supplied regex, expression, format string, compiler, evaluator, or
runtime text interpreter in the five current primitive implementations; their regexes are fixed in
reviewed source. Those positive observations do not close findings 1, 2, 4, 6, or 7 above.
