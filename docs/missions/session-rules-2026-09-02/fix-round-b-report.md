# Session Rules - fix round B report

The answer to `inspection-b.md`, which returned **CHANGES REQUIRED** with twelve findings, four of them
critical.

Branch `mission/session-rules-fb`, worktree `D:\ReposFred\devthrottle-session-rules-fb`, cut from
`origin/mission/session-rules`. Nineteen commits, pushed. Head **`51e3f4f4a`**.

**The gate at the head, `.\scripts\test-local.ps1`, no filter: all nine projects Completed, 4,749 passed,
2 skipped, 0 failed, exit code 0.** The two parked suites did not run and are the Architect's to cover
through the pull request; this round was told to keep every run short and filtered and did.

Every fix below owes a test that was watched FAILING first, from a committed state, with a named exact
command. The red and the green are both quoted with the commit they ran on. Where a claim could not be
made true it was DELETED rather than softened, and those are listed at the end.

---

## What was fixed

### 1. CRITICAL - the phase 2 red evidence (finding 1)

**Repaired by rerunning it, and by deleting the part that cannot be rerun.**

The first claim was corrected on a rerun. At `62133c497`,
`.\scripts\test-local.ps1 -Filter "FullyQualifiedName~Rules"` reports **47 failed, 78 passed, total 125,
exit code 1**. Both reports said 55 passed. The failure count and the exit code were right and the passed
count was not; the observed number now stands in `phase-2-report.md` and in `qa-report.md`, each marked as
a correction rather than quietly swapped.

The second claim was **deleted**. The send-outcome red named `(working tree)` rather than a commit, so
nobody can check it out and nobody can reproduce it. It is not restated in softer words: the row says
WITHDRAWN and the claim is in "what is not proven". The behaviour it was evidence for has since been
replaced, with its own committed red, by finding 9 below.

Both reports now name the exact command every filtered number came from. A number whose command is not
written down cannot be rerun by the person reading it, which is the whole point of writing it down.

Commit `9fb3bc767`.

### 2. CRITICAL - the human promotion boundary (finding 2)

The mint was public and took the caller's identity and acknowledgement as STRINGS, so it proved that two
strings a caller invented were not blank. Any Gateway code could invent both.

It is now internal - nothing outside the assembly can reach it at all - and it takes **the inbound request
itself** rather than a description of one, reading the caller from what the pipeline authenticated. Code
with no request has nothing to pass. A grant is **single use**, spent by the promotion it was obtained for,
so evidence cannot be captured and replayed. The constructor stays private, and a structural test asserts
nothing calls it.

The structural negative the inspection asked for is there and it has been **watched catching the thing it
is for**. A probe was committed that minted a grant from ordinary rules code with no request anywhere near
it, and the guard named it:

```
The_only_production_code_that_can_obtain_a_promotion_grant_is_the_promote_endpoint
  Expected: ["...Api.SessionRuleEndpoints"]
  Actual:   ["...Api.SessionRuleEndpoints", "...Rules.GatewayRuleEnvironment"]
```

Red at `a852a7be4`,
`.\scripts\test-local.ps1 -Filter "FullyQualifiedName~RulesPromotionBoundaryGuardTests"` - Failed: 3,
Passed: 4, Total: 7, exit 1. Green after `d8a4cd3cc`.

**What it does NOT enforce is written on the type in the same detail as what it does.** Inside one
assembly, access modifiers cannot make a capability physically unreachable: Gateway code could fabricate a
request object and stamp an identity on it. What stands against that is not a hope, it is the structural
test - doing it would be a visible, reviewable act rather than a quiet one.

### 3. CRITICAL - overlapping passes could type twice (finding 3)

A pass now takes a per-session place in an in-flight set **before it reads anything**, and a pass that
cannot take it does nothing and says so. It does not queue: a queued pass would carry a decision about a
screen from before the one just acted on, which is the staleness the pre-keystroke re-read exists to
refuse, and a queue of stale passes behind a slow model call is a pile-up nobody asked for. Dropping the
overlapping pass acts LESS, and the next turn-end brings another.

The probe holds the first pass on its pre-write firing snapshot - the exact window the inspection used -
and runs a second pass to completion while it is held. Red at `daa391437`,
`.\scripts\test-local.ps1 -Filter "FullyQualifiedName~RuleEvaluatorTests"` - Failed: 1, Passed: 24, Total:
25, exit 1:

```
Expected: "already-evaluating"
Actual:   "acted"
```

Both passes acted. Green after `a745da684`.

The first shape of that probe held the pass just BEFORE the firings read, and it went red for the wrong
reason - the held pass saw the other's act on resuming and the cooldown turned it away, which shows an
ordering rather than the double action. That is recorded in `daa391437` rather than tidied away.

### 4. CRITICAL - typing before the record was accepted (finding 4)

An act is written down as an INTENT before anything is sent, and the row is told afterwards what became of
the send. A store refusal is therefore a reason not to act rather than something discovered once the text
has gone. Only the store's own stated refusal is caught, and only so the pass can say what happened in
words; any other failure propagates, which stops the send just as effectively because it happens first.

An ACT with a blank reason is now refused at the reply boundary, because such an act cannot be recorded at
all and nothing downstream can put the reason back.

Four probes, two of them against the **real store on the real migrated schema**, as the inspection asked.
Red at `9881f628a`, `.\scripts\test-local.ps1 -Filter "FullyQualifiedName~Rule"` - Failed: 3, Passed: 196,
Total: 199, exit 1:

```
A_rule_deleted_while_the_model_was_being_asked_types_nothing
  RuleRejectedException : there is no rule with the id f2dd6ba1-...   (thrown AFTER the text was typed)
The_record_of_an_act_is_written_before_the_keystroke_and_completed_after_it
  Expected: 1  Actual: 0    (records in existence when the send seam was reached)
```

Green after `4656577bf`.

### 5. HIGH - the write gate could be gone round (finding 5)

What a rule is and what a firing is now live in ONE place that both the store and the context's gate ask.
The gate checked four things while the store refused nine, and the firing table was not gated at all.

Bulk statements never reach `SaveChanges`, so they are closed at the last place every route passes - the
command itself. An UPDATE or DELETE naming either rule table is refused unless it comes from a save that
has already been through the gate. It matches command text, which is blunt and deliberate: there is no
earlier hook that sees a bulk operation, and a check that cannot see the thing it guards is not a check.
Only UPDATE and DELETE, so migration is untouched.

The interceptor is installed from one method called at both provider sites, because SQLite and Postgres
are configured separately and a guard present in one of two places is absent on the one that runs in the
cloud. A structural test counts the two and fails if they differ. A second structural test fails if any
production type starts issuing bulk SQL against a rule or its record - with its own instrument, because
that guard's pass condition is an absence. Both DbSets are now internal.

Red at `55b41ebf8`, `.\scripts\test-local.ps1 -Filter "FullyQualifiedName~RulesWriteGateTests"` - Failed:
10, Passed: 11, Total: 21, exit 1: the two bulk routes, four unguarded firing shapes, and the five parts of
a rule the gate never looked at. Green after `a3d59a25e`.

### 6. HIGH - idle was checked before a long gap (finding 6)

The session's own facts are re-read immediately before the keystroke, not only its screen. A new owner turn
makes a session Working before any of its output appears, so the rows can be identical while the session is
no longer idle. The roster and the rule's scope are re-checked in the same place, through the same scope
code the free checks use rather than a second copy.

### 7. HIGH - grounding accepted an act with no citation (finding 7)

An ACT now needs a citation present AND correct. The check refused a reason quoting words the screen does
not contain and called a reason quoting NOTHING grounded, so an agent could avoid the bound entirely by
citing nothing. A decline still needs neither, and its record says which it had.

### 8. HIGH - malformed check collections became no checks (finding 8)

One strict reader, used by the agent's reply and by the write route, so the comment claiming one meaning is
now true. A collection that is not a list is refused; an entry that is not a check is refused rather than
dropped; an empty list still means what it says. On a reply it is required whenever something will ACT on
it and optional on a decline, where nothing follows either way - malformed is refused in both.

### 9. HIGH - an unreachable prompt was recorded as typed (finding 9)

The verb client keeps three outcomes apart where it collapsed an absent tunnel result and a remote refusal
into one boolean. Typed text is this product's word for "it reached the session", so it is written only
when something said so. A send nobody answered for names what went on the wire, in the outcome, and says
plainly that nothing confirmed it.

This revisits a decision made from a live run on 2 September, and deliberately. That run proved the route
answers "never started a turn" for a shell whose turn was over in milliseconds while the text HAD landed -
so calling it a failed send was wrong. But the same answer comes back when the Director refused the command
outright, and then nothing was typed at all. Writing "typed into the session" for both is the first mistake
wearing the other coat.

Findings 6, 7, 8 and 9 share one red: `322acc84b`,
`.\scripts\test-local.ps1 -Filter "FullyQualifiedName~Rule"` - Failed: 12, Passed: 181, Total: 193, exit 1.
Green after `41aafd4fd`. Among the twelve:

```
An_act_whose_reason_cites_nothing_from_the_screen_is_refused        Expected "ungrounded"  Actual "acted"
A_session_that_started_working_during_the_agent_call_is_abandoned   Expected "abandoned"   Actual "acted"
A_send_nobody_answered_for_names_the_text_it_sent...                Expected ""            Actual "/usage-credits"
A_command_that_produced_no_result_at_all_means_nothing_was_typed    Expected "not-sent"    Actual "unknown"
```

The transport half is driven through a **real verb client over a fake tunnel**, one case per
distinguishable event, rather than through a fake of the seam.

### 10. HIGH - the type-nothing guard was narrower than the feature (finding 10)

The launch is now a type of its own carrying a feature marker; the rule endpoints carry the same marker;
the guard reads the marker rather than a namespace. The scope is still a scope - the Gateway host is
asserted NOT to be part of this feature, so a marker that swallowed it would make the guard meaningless in
the other direction.

**Mutation-proved, as the inspection asked.** A send was placed in each newly-covered piece and the guard
named both. Red at `29c421ec8`,
`.\scripts\test-local.ps1 -Filter "FullyQualifiedName~RulesTypeNothingGuardTests"` - Failed: 2, Passed: 6,
Total: 8, exit 1:

```
Expected: ["...Rules.GatewayRuleEnvironment"]
Actual:   ["...Api.SessionRuleEndpoints", "...Rules.GatewayRuleEnvironment", "...Rules.RuleTurnEndLauncher"]
```

Green after `ed8339b08`, probes removed.

The guard also gained a limit it did not previously state: an async method's body lives in a compiler
generated state machine that nothing calls by name, so a backward call-graph walk stops dead at any async
wrapper. Both ways into the keystroke are therefore named rather than one being reached from the other.

### 11. HIGH - the runner passed partial evidence (finding 11)

Each `FullyQualifiedName~` term the caller names must now have collected at least one test whose name
contains it, or the run exits 5. The terms are derived from the filter the caller passed, never a second
list kept in the script; a term in a form that cannot be checked - a trait, an equality match, a negation -
is left alone rather than guessed at. `-ExpectTests` declares a count when a claim rests on one.

Watched against the same known-BAD input the inspection used. Command both times:

```
.\scripts\test-local.ps1 -Filter "FullyQualifiedName~RuleReasonGroundingTests|FullyQualifiedName~DefinitelyNoSuchTest_dnkeyz"

  at 89284f24d: 10 collected, RESULT: all projects exited zero, exit code 0
  after 098c06969: 10 collected, RESULT: PART OF THE FILTER MATCHED NOTHING, exit code 5
```

And it is not a wall: two real terms still exit 0, and `-ExpectTests 99` against a 10-test run exits 5.

### 12. MEDIUM - the public projection hid the accountability fields (finding 12)

The rule now says who moved it out of dry run and the firing says what grounding found. Both were stored
and neither was delivered, which for a reader is the same as not existing. The projections were lifted out
of the route lambdas into a type ordinary unit tests can read, because they lived somewhere only the parked
host-bound suite could reach and so were in practice tested by nothing.

Red at `e2316465b`, `.\scripts\test-local.ps1 -Filter "FullyQualifiedName~SessionRuleWireTests"` - Failed:
8, Passed: 4, Total: 12, exit 1. Green after `89284f24d`.

---

## One thing fixed that the inspection did not ask for

**The full local gate was red at `9fb3bc767`, twice, on a test this round never touched:**

```
DeviceKeyAtRestTests.Migration_RecordsTheMaskedKeyIdentity_FromThePlaintextBeforeDroppingIt
InvalidOperationException : CC_GATEWAY_DB_CONNECTION is set but blank
```

It passed in isolation, and the same suite is green at the parent `715325455` (3,356 passed). The cause was
established by reachability rather than by repetition: one test has to blank the process-global
`CC_GATEWAY_DB_CONNECTION` to prove a blank connection string still fails in the constructor, the suite runs
in parallel, and any database opened inside that window fails with a message about the OTHER test's fault.
This round did not create the hazard - it added tests that open databases, which made it land. It is the
same shape the test harness already carries a comment about from a process-global connection pool clear:
every full run fails exactly one database test, a different one each time, all passing in isolation.

The two are now serialised asymmetrically: many tests may open databases together, exactly one mutates the
variable and must do it alone. The three metadata guards also now share one read of the built Gateway
assembly instead of three. Commit `51e3f4f4a`; the gate is green after it.

---

## What was DELETED rather than fixed

- **The send-outcome red-first claim.** It named `(working tree)`, so it cannot be reproduced by anybody.
  The row says WITHDRAWN. No number was softened and none was invented to replace it.
- **The old grounding statement that "there was nothing to check".** It read an absence as a positive
  result. It is not reworded, it is a different verdict: a reason that cites nothing cannot carry an act.
- **The claim that an unconfirmed send was text typed into a session.** It is now recorded as text SENT
  with nothing confirming it, and the typed-text field is left empty.

---

## What remains NOT proven

- **Neither parked suite ran.** `CcDirector.Gateway.Tests` and `CcDirector.Core.Tests` were not run at all,
  on the Architect's instruction: a release gate held the machine-wide lock, and a queued run waits at most
  45 minutes for a suite that takes 48.88, so it would have collected zero tests. Every host-bound endpoint,
  tenancy and boundary test in this repository is therefore uncovered by this round's evidence. The rule
  endpoints have unit coverage of their MAPPING through `SessionRuleWire` and none of their HTTP behaviour.
- **No HTTP request was made and nothing ran hosted.** The promotion boundary is proved against a request
  object the tests construct, not against the live device-key middleware. That the middleware really does
  leave an authenticated caller where the grant reads it is unverified here.
- **Nothing ran against Postgres.** The write-gate interceptor is proved on SQLite. Its installation on the
  Postgres path is proved structurally - the call sites are counted - and not by executing it.
- **The demonstration was not re-run.** It is captured in `qa-report.md` as it happened on 2 September, and
  this round changed how an unanswered send is recorded, so the second firing quoted there is not the shape
  the product produces today. That is stated in the report rather than left for a reader to trip over.
- **A rule row cannot express "no scope was said".** All four scope parts empty IS "all sessions", a legal
  value, so the gate has nothing to refuse; the distinction between a choice and an omission exists only at
  the wire, where the omission exists, and it is refused there. Nothing was invented to make the row appear
  to enforce it.
- **The promotion bound is not proof that a person was at a keyboard.** Nothing in a process can be, and
  within one assembly a capability cannot be made physically unreachable. The bound is: no inbound request,
  no grant; and a structural test that fails on any type but the endpoint reaching the mint.
- **Grounding is a floor, not faithfulness.** A citation that IS on the screen does not make the conclusion
  drawn from it correct. It makes it checkable by a person reading the record.
- **The evaluator drops an overlapping pass rather than queueing it.** That is the safer direction and it is
  deliberate, but it means a turn-end signal arriving during a slow pass produces no evaluation at all. No
  measurement was taken of how often that happens in practice.

---

## Runs

Every number carries its exit code, the commit it ran on, and THE COMMAND THAT PRODUCED IT. Two commands
appear and they are not the same instrument, so the column says which:

- **`gate`** is `.\scripts\test-local.ps1 -Filter "<filter>"` from the repository root - the named gate,
  nine projects, the TRX verdict.
- **`project`** is `dotnet test src\CcDirector.Gateway.UnitTests\CcDirector.Gateway.UnitTests.csproj
  --no-build --filter "<filter>"` - the one project, used while iterating. It is recorded as what it is
  rather than written up as a gate run it was not. **Every red in this round was watched through the
  gate**; some of the intermediate greens were not, and the row says so. The green that covers all of them
  is the full gate at the head.

| What ran | Commit | Command | Filter | Exit | Result |
| --- | --- | --- | --- | --- | --- |
| Phase 2 red, rerun to reconcile finding 1 | `62133c497` | gate | `FullyQualifiedName~Rules` | 1 | 47 failed, 78 passed, total 125 |
| Overlapping passes, red | `daa391437` | gate | `FullyQualifiedName~RuleEvaluatorTests` | 1 | 1 failed, 24 passed, total 25 |
| Overlapping passes, green | `a745da684` | gate | `FullyQualifiedName~Rule` | 0 | 178 passed, 0 failed |
| Citation, idleness, send, checks - red | `322acc84b` | gate | `FullyQualifiedName~Rule` | 1 | 12 failed, 181 passed, total 193 |
| The same four, green | `41aafd4fd` | project | `FullyQualifiedName~Rule` | 0 | 195 passed, 0 failed |
| Record before keystroke, red | `9881f628a` | gate | `FullyQualifiedName~Rule` | 1 | 3 failed, 196 passed, total 199 |
| Record before keystroke, green | `4656577bf` | project | `FullyQualifiedName~Rule` | 0 | 199 passed, 0 failed |
| Promotion boundary, red | `a852a7be4` | gate | `FullyQualifiedName~RulesPromotionBoundaryGuardTests` | 1 | 3 failed, 4 passed, total 7 |
| Promotion boundary, green | `d8a4cd3cc` | project | `FullyQualifiedName~Rule` | 0 | 206 passed, 0 failed |
| Write gate, red | `55b41ebf8` | gate | `FullyQualifiedName~RulesWriteGateTests` | 1 | 10 failed, 11 passed, total 21 |
| Write gate, green (full gate) | `a3d59a25e` | gate | none | 0 | all nine Completed; 4,839 passed, 2 skipped |
| Feature guard, red | `29c421ec8` | gate | `FullyQualifiedName~RulesTypeNothingGuardTests` | 1 | 2 failed, 6 passed, total 8 |
| Feature guard, green with the probes removed | `ed8339b08` | project | `FullyQualifiedName~Rule` | 0 | 220 passed, 0 failed |
| Public projection, red | `e2316465b` | gate | `FullyQualifiedName~SessionRuleWireTests` | 1 | 8 failed, 4 passed, total 12 |
| Public projection, green | `89284f24d` | project | `FullyQualifiedName~Rule` | 0 | 232 passed, 0 failed |
| Runner fail-open, known-BAD input | `89284f24d` | gate | the composite filter above | 0 | 10 collected, reported as a pass |
| Runner refusing it | `098c06969` | gate | the same composite filter | 5 | 10 collected, part of the filter matched nothing |
| Runner is not a wall: two real terms | `098c06969` | gate | two real class filters | 0 | 31 collected |
| Runner on a declared count that is wrong | `098c06969` | gate | one class filter, `-ExpectTests 99` | 5 | 10 collected, 99 expected |
| Parent, to judge the env-var red against | `715325455` | gate | `FullyQualifiedName~CcDirector.Gateway.Tests` | 1 (over budget, not a failure) | Gateway.UnitTests 3,356 passed, 0 failed |
| Full gate, still red on the env-var race | `9fb3bc767` | gate | none | 1 | 1 failed, 3,455 passed, total 3,458 |
| **Full gate at the head** | **`51e3f4f4a`** | gate | none | **0** | **all nine Completed; 4,749 passed, 2 skipped, 0 failed** |
| Parked `CcDirector.Gateway.Tests` | | | | | **PENDING** - not run; the machine-wide lock was held by a release gate |
| Parked `CcDirector.Core.Tests` | | | | | **PENDING** - not run |

An attribution audit was run over all nineteen commits and over the whole diff: no hit for a
co-authorship trailer, a generated-with footer, or any assistant, model or vendor name. The grep was first
run against a planted trailer and printed it, so a clean result is a result and not an empty instrument.
