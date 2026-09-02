# Session Rules - phase 1 report

Phase 1: the rule store, the contract, the primitive registry, the five primitives, and the
write-time validator. Dry run only; nothing in this phase types anything.

Branch `mission/session-rules-p1`, worktree `D:\ReposFred\devthrottle-session-rules-p1`, cut from
`origin/mission/session-rules`. Head at the time of writing: **`48eeb1e83`** (plus this report).

Every number below carries its exit code and the commit it ran on. Every feature was written as a
test that was watched FAILING first, and both the red and the green are quoted.

---

## The migration slot, swept by presence on main (ruling A10)

`git fetch origin --prune`, then the tip of the migration chain compared by PRESENCE ON MAIN, not by
difference from the merge base:

- `origin/main` is `fac79fb56`.
- The last migration on `origin/main` is `20260902063350_AddKnownRepositories`, and it IS present in
  this worktree. The instrument found five migration names on both sides, so it was reading
  something - an empty listing would have been a broken instrument, not a free slot.
- Neither of the other two holders is on main: the sister mission's screen-store migration on
  `mission/terminal-rules`, and the three from 2 August on pull request 2379. Both remain holders;
  whoever lands last regenerates the model snapshot.

The sister mission gave this one priority, so the slot was taken now:

| Provider | Migration |
| --- | --- |
| SQLite | `20260902191922_AddSessionRules` |
| Postgres | `20260902191946_AddSessionRules` |

Both providers report **`No changes have been made to the model since the last migration`**, exit
code 0, on `48eeb1e83`.

---

## The acceptance rows phase 1 owed

### 1. A rule round-trips through the store

The account's sentence, the derived screen description, the derived trigger words, the derived
primitive calls, scope, cooldown, daily cap and state all survive a write and a read through a
SECOND store opened over the same database - a real restart, not the object that was just handed
out.

Test: `SessionRuleStoreTests.A_rule_round_trips_through_the_store_with_every_part_intact`, plus
`A_rule_scoped_to_one_repository_round_trips_its_scope`, `All_returns_the_rules_newest_first`, and
`One_account_never_reads_another_accounts_rules`.

### 2. A rule naming a primitive that does not exist is refused at write time, with a reason

Proved by WRITING a refused rule and reading the reason, not by grepping the schema for the absence
of a code column - an absence proves nothing about what the writer would accept.

Test: `SessionRuleStoreTests.A_rule_naming_a_check_that_does_not_exist_is_refused_at_write_time_with_a_reason`.
The reason names the check that was asked for and lists the ones that exist, and nothing is written.

### 3. A rule supplying the wrong arguments to a real primitive is refused, with a reason

Test: `SessionRuleStoreTests.A_rule_supplying_the_wrong_arguments_to_a_real_check_is_refused_with_a_reason`,
plus twelve cases in `RuleCallValidatorTests`: a missing argument, an argument for a parameter that
does not exist, the same parameter twice, an argument of the wrong kind, a list where one value was
wanted, an empty term list, an extract kind outside the closed set, a runtime input that does not
exist, a source that is neither a written-down value nor a runtime input, and a timestamp that is not
a moment.

### 4. The primitive registry is derived, non-empty, and complete

`RulePrimitiveRegistryTests` discovers the attributed methods with its OWN reflection scan and
requires every one of them to be reachable through the registry, with the counts equal. Two
instrument checks sit above it: the assembly really does carry attributed primitives, and the
registry really is non-empty - so an empty set fails rather than passing vacuously. The refusal of a
signature outside the closed set is proved against a deliberately WRONG primitive that lives in the
test assembly, so the refusal is a real observation and not an assertion.

### 5. The five primitives have their own unit tests

`is_path_inside` carries the three cases named: a `..` that walks out of the root (and one that stays
inside), a LINK that points out of the root (and one that stays inside), and a PREFIX COLLISION -
`repo-other` beside `repo` is not inside it, which a string prefix test gets wrong. The link case
creates a real reparse point and asserts it EXISTS and RESOLVES before asserting behaviour; if the
machine will not create one the test fails loudly rather than skipping, because a skipped link case
would leave this row passing over something that never ran.

`matches_any` is proved to treat its terms as LITERAL text - `.*` is two characters, not "anything" -
which is the property that keeps it from being a pattern language.

### 6. A new rule is created in dry run, and nothing in phase 1 types anything

Two separate proofs, because the second one is the kind that fails open.

**Dry run is enforced, not documented.** `Create` takes no state parameter at all, so no caller can
create a live rule; a person promotes it. And a firing recorded against a dry-run rule may not claim
to have typed anything - the store refuses it with a reason rather than silently blanking the field,
because a store that quietly edits what it was told cannot be read as evidence.

**Nothing types.** A grep over the rules directory for "prompt" would pass just as happily if the
directory were EMPTY or if the call sat one helper away in another file. So this is a reference
assertion read out of the BUILT assembly with Mono.Cecil, and the scanner is proven on a
known-positive first: it is pointed at the endpoint code that really does send commands to sessions
and required to find the seam there, and it is required to find types in the rules namespace at all.
Then it is required to find nothing in the rules namespace reaching that seam.

That guard was itself run against a known-BAD input before it was trusted - see the red below.

---

## Red, then green

Each feature was written as a test that failed against unwritten code, and the red was watched and
recorded before the code existed.

| What | Red | Green |
| --- | --- | --- |
| The five primitives and the derived registry | `a8259bcbb` - **33 failed, 1 passed, exit code 1** | `84c25911e` - **34 passed, 0 failed, exit code 0** |
| The stored call shape and the write-time validator | `84c25911e` - **18 failed, 0 passed, exit code 1** | `5523025ec` - **52 passed, 0 failed, exit code 0** (all rules tests) |
| The rule store | `522b1cee5` - **21 failed, 0 passed, exit code 1** | `515759985` - **21 passed, 0 failed, exit code 0** |
| The types-nothing guard, against a known-bad input | `c991921d2` - **1 failed, 2 passed, exit code 1** | `7a7422119` - **76 passed, 0 failed, exit code 0** (all rules tests) |
| The tenant-scope guard on the two new tables | `7a7422119` - **1 failed, 3310 passed, exit code 1** | `48eeb1e83` - **81 passed, 0 failed, exit code 0** |

The one pass in the first red is deliberate: it is the instrument check saying the assembly really
does carry attributed primitives. If THAT had failed, every completeness check below it would have
been measuring an empty set.

The two passes in the guard's red are the same thing: the scanner does find the typing seam where it
really is, and the rules namespace is not empty.

### The red on the types-nothing guard, quoted

Commit `c991921d2` deliberately put a rules type that reaches the typing seam into the build. The
guard failed on it by name:

```
phase 1 types nothing, but these reach CcDirector.Gateway.Api.DirectorCommandRouter:
CcDirector.Gateway.Rules.ZzTemporaryTypistProbe.SeamName
```

`Failed: 1, Passed: 2, exit code 1`. The probe was removed in `7a7422119` and the guard went green on
real code. The probe commit is left in the history on purpose, so the red is reproducible by checking
it out rather than taken on trust.

### The red found by the tenant-scope guard, quoted

The local gate on `7a7422119` caught a real defect that had not occurred to the author: both new
tables were keyed on a bare `Id`.

```
These tenant-scoped tables have a PRIMARY KEY that does not include tenant_id and is not the
Gateway-minted Id, so one tenant can squat a key value for every other tenant and learn that
another tenant holds it: SessionRuleEntity (key: Id); SessionRuleFiringEntity (key: Id).
```

`Failed: 1, Passed: 3310, exit code 1`. Fixed in `48eeb1e83` by deriving both entities from
`GatewayMintedKeyEntity`, whose `Id` setter is private - so "the Gateway mints this key" is enforced
by the compiler rather than claimed in a comment. The schema did not change, so the migration
committed earlier stands, and both providers still report no pending model changes.

---

## The local gate

`.\scripts\test-local.ps1` on `48eeb1e83`: **exit code 0**, all nine projects `outcome=Completed`.

| Project | Result |
| --- | --- |
| CcDirector.Core.UnitTests | 160 passed |
| CcDirector.Gateway.UnitTests | 3311 passed, 2 skipped |
| CcDirector.Avalonia.Tests | 364 passed |
| CcDirector.Engine.Tests | 63 passed |
| CcDirector.HostedAgent.Tests | 88 passed |
| CcDirector.Launcher.Tests | 113 passed |
| CcDirector.Terminal.Avalonia.Tests | 24 passed |
| cc-director-setup.Tests | 25 passed |
| cc-director-setup-engine.Tests | 456 passed |

Of those, 81 are the phase's own tests.

---

## What is NOT proven

Stated plainly, because a report that only lists successes has not shown where the edge is.

- **The parked `CcDirector.Gateway.Tests` suite did not run.** The gate flags it as a coverage gap,
  and the flag is generic - any file under `src/CcDirector.Gateway` maps to that suite, so this is
  not a specific test known to be affected. It was not run because the machine-wide test lock was
  held by another mission for about an hour and a healthy run of that suite measures 48.88 minutes
  against a 45-minute maximum wait, so a queued run cannot acquire the lock at all. **PENDING**, for
  the Architect to schedule.
- **Nothing was proven against a live Postgres.** The cross-provider proof in that parked suite is
  gated on a real connection string and skips without one. What IS proven is that the Postgres
  migration exists and is in sync with the model: `has-pending-model-changes` reports no pending
  changes, exit code 0.
- **No rule has ever fired.** Phase 1 stores rules and records firings; nothing evaluates a screen
  and nothing types. The firing record was exercised by writing firings directly, not by a rule
  running.
- **No model has ever built a rule from English.** Every rule in these tests was constructed by the
  test. Authoring is phase 2.
- **The decline is recorded but not yet earned.** A declined firing round-trips through the store,
  but no agent has been given a screen its instruction does not cover and chosen to decline. Ruling
  A6 owes that from phase 2 onward, and it is the bound that decays quietly.
- **Nothing is on a user interface.** There is no Rules page, no endpoint and no client code in this
  phase.
- **The five primitives are as good as their patterns.** `retry_delay_from` reads two forms of wait
  and `extract_first` three kinds of thing; both answer nothing rather than guessing when the screen
  says nothing they recognise, but neither has been run against a corpus of real screens. The one
  real fixture the mission has was not used in phase 1.
