# Session Rules - fix round A report

The disposition of the nine findings in `inspection-a.md`, of the two defects phase 2 found in its own
work, and of the Architect's rulings A11, A12 and A13.

Branch `mission/session-rules-fa`, worktree `D:\ReposFred\devthrottle-session-rules-fa`, cut from
`origin/mission/session-rules`. Head at the time of writing: **`0e67342ed`** (plus this report).

Every number below carries its exit code and the commit it ran on, or the word PENDING.

**Nothing in this round rebuilt the demonstration, started the authoring conversation, started a user
interface, or added a feature.**

---

## A13 first, because it was the worst: two claims in the report were false

The phase 1 report said the validator's 18 tests failed on `84c25911e` and the store's 21 tests failed
on `522b1cee5`. Neither commit contains the test file. Checked again here before anything was written:

```
$ git ls-tree -r --name-only 84c25911e -- src/CcDirector.Gateway.UnitTests/Rules/
src/CcDirector.Gateway.UnitTests/Rules/RulePrimitiveRegistryTests.cs
src/CcDirector.Gateway.UnitTests/Rules/RulePrimitivesTests.cs

$ git ls-tree -r --name-only 522b1cee5 -- src/CcDirector.Gateway.UnitTests/Rules/
src/CcDirector.Gateway.UnitTests/Rules/RuleCallValidatorTests.cs
src/CcDirector.Gateway.UnitTests/Rules/RulePrimitiveRegistryTests.cs
src/CcDirector.Gateway.UnitTests/Rules/RulePrimitivesTests.cs
```

`RuleCallValidatorTests` is absent from the first; `SessionRuleStoreTests` is absent from the second. A
filter naming either exits 0 with `No test matches`. **Both claims were DELETED**, in `phase-1-report.md`
and in `qa-report.md`, in plain words, with a short section saying what happened and why the original
runs can never be reproduced - the tree they ran on is gone.

They were then re-proved properly. A red probe was committed for each feature: the feature's behaviour
deliberately absent while its tests are present, so the red reproduces by checking the commit out. Both
probe commits are left in the history on purpose, exactly as phase 1 left its types-nothing probe.

| Feature | Probe commit (red) | Filter | Result |
| --- | --- | --- | --- |
| The write-time validator | `1eeaca050` | `FullyQualifiedName~RuleCallValidatorTests` | Failed 18, Passed 0, Total 18, **exit code 1** |
| The rule store | `c6bdef6c8` | `FullyQualifiedName~SessionRuleStoreTests` | Failed 21, Passed 0, Total 21, **exit code 1** |

Green on `9412cb2bd`, `FullyQualifiedName~Rules`: **128 passed, 0 failed, total 128, exit code 0**.

### And the instrument that let them stand was fixed

`scripts/test-local.ps1` returned SUCCESS for a run that collected zero tests. That is the fail-open
behind the whole finding: a filter naming a class that is not in the checkout exits 0 from every
project, writes a result file saying `outcome=Completed` with `total=0`, and the script ended on
"RESULT: all projects exited zero". Nothing anywhere said that nothing had run.

Watched failing first, then fixed:

| What ran | Commit | Exit code | Result |
| --- | --- | --- | --- |
| A filter matching nothing, on the old runner | `6467aa69a` | **0** | nine projects, `total=0` each, `RESULT: all projects exited zero` |
| The same input, on the guarded runner | `65e88f4f0` | **3** | `RESULT: ZERO TESTS COLLECTED - nothing ran, so this is not a pass.` |

The pass condition is now a PRESENCE: at least one test must have been COLLECTED across the run. A
per-project zero is normal under a filter and is not failed. Exit 3 means nothing ran anywhere; exit 4
means a project exited zero without writing a result file. Neither is a test failure, and neither is
ever evidence.

---

## The nine findings, and what happened to each

### Finding 1 - the dry-run promotion path had no human boundary. FIXED.

`Promote` took a rule id and a timestamp, so anything able to read rules could make one live, and the
test that claimed "only a person moves it" proved only that a direct call worked.

Three things, together:

- **Promotion requires a `RulePromotionGrant`** - evidence that a person asked. It cannot be
  constructed; the only way to obtain one is from an inbound request the Gateway authenticated, plus a
  sentence saying what is being agreed to. It names ONE rule, so a grant cannot be turned on another.
- **The evaluation path is handed `IRuleReading`**, which has no `Promote`, no `Create` and no
  `Delete` on it. Through phase 2 the production wiring held the concrete store and was one line from
  being able to promote.
- **An assembly guard** asserts nothing in the feature holds the concrete store or a grant. It is a
  TYPE assertion, not a call assertion, deliberately: asking "does anything call Promote" passes
  happily on code that simply has not called it yet.

Proved as the ruling asked - by a caller that is NOT a person being REFUSED: a promotion with no grant,
a grant that cannot be minted because the pipeline named no caller, a grant that cannot be minted
because nothing was said, and a grant for one rule turned on another. The rule now records who promoted
it.

**Stated exactly, because a bound described more broadly than it holds is worse than none.** This is
not proof that a human being was at a keyboard - nothing inside a process can be. It is proof that the
act was carried by an authenticated request, is attributable to the caller the pipeline resolved, and
cannot be performed by the code that evaluates rules. An attacker already holding a device key is
authentication's problem, not this bound's.

### Finding 2 - the validator was not the write gate the code claimed. FIXED, not deleted.

The claim was false: the entity, its setters, the DbSet and the context factory are all public, so a
caller could write an arbitrary call document, an arbitrary account and `State = "live"` without meeting
the validator.

The claim was made TRUE. The validator, dry run and the account check now run in
`GatewayDbContext.SaveChanges`, which every route to the table ends at. Four tests take the bypass
itself: a rule written straight through the data context with an invented check, one that tries to start
live, one that claims another account, and one moved to live without a grant - all refused. A fifth
proves it is a gate and not a wall, because every refusal test above it would also pass on a gate that
refused everything.

The store's own comment was rewritten to say what it actually is - the front door, not the only door -
and to name where the boundary lives.

### Finding 3 - the unreproducible reds. See A13 above. DELETED and re-proved.

### Finding 5 - a missing scope silently widened to every session. FIXED.

`Create` turned a null scope into `AllSessions`, the widest value there is, reached by omission. It now
refuses a missing scope with a reason, and its signature is honestly nullable so the refusal is part of
the contract rather than a guard against something the type denies can happen. On the wire,
`"scope": "all-sessions"` is how a caller says every session on purpose; an object with nothing in it is
the same omission wearing braces, and is refused.

### Finding 6 - the types-nothing guard saw only direct references in one namespace. FIXED.

It now walks the call graph, and it covers the feature's stored rows as well as the rules namespace -
the landing that introduced the guard also added the rule and firing entities in the data namespace, so
the feature's own rows were outside the thing guarding the feature.

Run against a known-BAD input before being trusted, and the old guard's fail-open is on the record: on
`1ee65c3eb`, with a rules type that types one helper away in the build, the guard reported **Passed 12,
Failed 0, exit code 0**. The tightened guard fails on the same input by name.

**What it covers, exactly:** static call edges inside the Gateway assembly, followed transitively. Not
calls into other assemblies. **Not virtual dispatch** - the evaluator reaches the send through
`IRuleEnvironment`, which is the design and is what the dry-run branch sits in front of, so the
assertion says "no route round that seam" rather than something broader that would be false.

### Finding 7 - the suite did not enforce that exactly the approved checks ship. FIXED.

Also run against a known-bad input first: on `1ee65c3eb` a sixth attributed check was in the build and
the registry suite passed it. The suite now compares the approved list with the COMPLETE registry. The
five stay hand-written on one side deliberately - they are an external ruling, and deriving them from
our own code would make them agree with it by construction.

### Finding 8 - a null argument element crashed the validator. FIXED.

A null element in a call's argument list is refused with a stated reason instead of throwing. A null
call in a list of calls, and a null inside an argument's values, are refused too.

### Finding 9 - the firing store accepted an empty or invented record. FIXED.

A firing is refused unless it says which session it fired on, why it decided what it decided, what
happened next, and what the grounding check found. Its decision must be one of the four this build
knows. A check it claims ran must be one the product ships and must say what it answered.

**Two fields are still allowed to be blank, on purpose:** the screen text, because a terminal really can
be blank, and the understanding, because a reply that was refused really did give no understanding.

### Finding 4 - reconciled by ruling A11, and its deeper half is CLOSED.

The inputs stay: ruling 15 ships checks taking a clock and the session's repository root, so removing
them would break the primitives the owner named. What was missing was a bound separating "a clock used
to interpret what the screen says" from "a clock used to decide whether the rule applies".

The bound: **the question the agent is asked carries the screen and the account's own sentence and
nothing else.** It is tested, and the test was watched failing on a known-bad input - `2cb4b0131` put
the repository path and the clock into the question on purpose: **Failed 1, Passed 23, Total 24, exit
code 1**, naming the repository path where it had no business being.

---

## Ruling A12 - an act's reason must be grounded in the screen. CLOSED.

A live run declined while quoting a sentence that was on that session's screen twelve minutes earlier,
in an unrelated run. The decline was safe. The same unfaithfulness pointed the other way is a rule
acting on evidence that was not there.

- **An ACT whose stated reason quotes text the screen does not contain is REFUSED**, recorded with what
  was quoted and where it was not, and nothing is typed.
- **A DECLINE that does the same stands** - declining is the direction that does nothing - but is
  recorded with the mismatch NOTED.
- **Every firing carries what the grounding check found, and the store refuses one that cannot say.**
  That is the PRESENCE the ruling asked for: a run in which the check never executed cannot look
  identical to one in which it ran and found nothing wrong. A firing whose reason is the Gateway's own
  words rather than the agent's says so in those words, which is not the same as saying nothing.

**What it checks and what it does not.** It checks QUOTED passages - text the reason puts in quotation
marks is a claim about the screen, and that claim is checkable. It does not check paraphrase and it
cannot: a reason saying the screen "looks like a limit notice" is a judgement, and judging the judgement
is what the agent was asked for in the first place. **This is a floor, not a proof of faithfulness.**

---

## Red, then green

Each fix was written as a test run against the un-fixed code and WATCHED FAILING, and both the red and
the green are reproducible from the commits named.

| What | Red | Green |
| --- | --- | --- |
| The zero-collection guard on the runner | `6467aa69a` - **nine projects at total=0, exit code 0** (the fail-open) | `65e88f4f0` - **exit code 3, ZERO TESTS COLLECTED** |
| The write-time validator, re-proved | `1eeaca050` - **Failed 18, Passed 0, Total 18, exit code 1** | `47b28c298` / `9412cb2bd` - **128 passed, exit code 0** |
| The rule store, re-proved | `c6bdef6c8` - **Failed 21, Passed 0, Total 21, exit code 1** | `9412cb2bd` - **128 passed, exit code 0** |
| The write gate, the promotion boundary, the grounding bound, the record | `78399eab2` - **Failed 31, Passed 142, Total 173, exit code 1** | `2a82b192d` - **Passed 173, Failed 0, Total 173, exit code 0** |
| The two guards, against a known-BAD input | `1ee65c3eb` - **Passed 12, Failed 0, exit code 0** (the fail-open) | - |
| The tightened guards, on that same bad input | `14a858d19` - **Failed 2, Passed 13, Total 15, exit code 1** | `cdf9a3853` - **Passed 177, Failed 0, Total 177, exit code 0** |
| The bound on what the question carries | `2cb4b0131` - **Failed 1, Passed 23, Total 24, exit code 1** | `8b03acb26` - green in the gate below |

**Why the red commits contain stub types rather than nothing at all.** A test naming a type the compiler
cannot find is a build failure, not a red run. So each red commit carries the signatures and leaves the
BEHAVIOUR absent - the grant mints without checking, the grounding check answers "grounded" whatever it
is given, the data context has no write gate. That is what "the test failed against unwritten code"
honestly means in a language that has to compile first, and it is said here rather than left for a
reader to work out.

**Not every test in a red batch fails, deliberately.** The PRESENCE halves - a gate that still lets a
good rule through, an act on a faithful reason that still acts, all sessions still being a scope a rule
can have, the seam still carrying what the evaluator needs - pass before and after. They are what stops
each fix being a wall, and a wall would satisfy every refusal test above it.

---

## The local gate

`.\scripts\test-local.ps1` on `0e67342ed`: **exit code 0**, all nine projects `outcome=Completed`.

| Project | Result |
| --- | --- |
| CcDirector.Core.UnitTests | 160 passed |
| CcDirector.Gateway.UnitTests | 3401 passed, 2 skipped, total 3403 |
| CcDirector.Avalonia.Tests | 364 passed |
| CcDirector.Engine.Tests | 63 passed |
| CcDirector.HostedAgent.Tests | 88 passed |
| CcDirector.Launcher.Tests | 113 passed |
| CcDirector.Terminal.Avalonia.Tests | 24 passed |
| cc-director-setup.Tests | 25 passed |
| cc-director-setup-engine.Tests | 456 passed |

Both providers report **`No changes have been made to the model since the last migration`**, exit code 0
each, on `0e67342ed`. The mission's migration was regenerated in place on both providers to carry the
two columns the record now needs - who promoted a rule, and what the grounding check found - so the
mission still holds exactly one migration slot.

### The gate was stopped twice before it was green, and that was this round's fault

`CcDirector.Gateway.UnitTests` exceeded the 120-second ceiling and was KILLED, twice. Every test in it
passed - 3413, outcome Completed - but a suite that gets stopped is neither a pass nor a failure, and
raising the ceiling is forbidden.

Measured rather than guessed, by running the suite with this round's tests excluded:

| What ran | Tests | Duration |
| --- | --- | --- |
| The suite WITHOUT this round's tests | 3368 | 1 m 42 s |
| The suite WITH them, before the fix | 3413 | 2 m 02 s |
| The suite WITH them, after the fix | 3403 | 1 m 41 s |

So the suite was already close to the ceiling and this round is what took it over, which made it this
round's to fix. Three changes, none of which drops a case: the built assembly and its call graph are
read once per test process rather than once per test (the guard suite went from 14 seconds to 4); each
test class opens its database once rather than two or three times; and an eight-case theory plus four
one-assertion tests became two tests that loop, because in that class each case costs a whole migrated
database. Every case is still checked and still names itself when it fails.

Wall clock on a machine running other missions is not a precise instrument. The honest statement is that
the suite is back to about where it was before this round.

---

## What is NOT proven

- **The parked `CcDirector.Gateway.Tests` suite did not run. PENDING.** The machine-wide lock was held
  throughout by another session, and the lock file says by whom:

  ```
  processId=24056
  acquiredUtc=2026-09-02T22:23:49.4716478Z
  session=cc-director session f23596b6-c596-4182-85a5-592ab412a12a
  directory=D:\ReposFred\devthrottle-gwtests-main\src\CcDirector.Gateway.Tests\bin\Debug\net10.0
  ```

  It was not fought for. What can be said about the risk, precisely rather than reassuringly: the write
  gate added to `GatewayDbContext.SaveChanges` iterates `ChangeTracker.Entries<SessionRuleEntity>()`
  only, so for a save with no rule rows tracked it is an empty enumeration and no other store's
  behaviour changes. No source file in that suite mentions the rules feature. Neither of those is a run,
  and a regression inside that suite could still reach the mission branch on a green local gate.
- **The original phase 1 red runs remain unproven and always will be.** The commits named for them do
  not contain the test files, so whatever tree produced those numbers cannot be identified. The reds now
  in the report come from probe commits made in this round.
- **The zero-collection guard covers the fleet's runner, not every way evidence is gathered.** A run
  made with a hand-rolled `dotnet test` still exits 0 on `No test matches`.
- **The grounding check is a floor.** It checks quoted passages and cannot check paraphrase. An act
  whose reason is a faithful-sounding paraphrase of a screen it never read passes it.
- **The promotion boundary is not proof of a human.** See finding 1 above for exactly what it is and is
  not.
- **No route was exercised over HTTP in this round.** The promote route's new body and the write
  route's explicit scope are covered by the store's tests and by reading the endpoint, NOT by a request.
  The mission's own documents were corrected so nobody is handed a call that no longer works, but that
  correction is prose, not a run.
- **Nothing ran against Postgres, nothing ran hosted, and no client authentication was exercised.** The
  cross-provider proof lives in the parked suite and is gated on a real connection string.
- **Everything phase 1 and phase 2 listed as unproven is still unproven.** This round fixed defects; it
  did not build the authoring conversation, a user interface, or the real provider-limit recovery, and
  it did not re-run the demonstration.
- **A fix round is new writing.** These fixes have had one author and no independent inspection. The
  round that found nine defects in landing A read work that was also green, also tested, and also
  believed correct by the person who wrote it.
