# The rule routes reach the guard - fix report

Branch `mission/rules-guard`, worktree `D:\ReposFred\devthrottle-rules-guard`, written 2026-09-03.
Read it against the code, not against this document.

---

## What was broken

`SessionKeyGuard` is a deliberate literal allow list of the routes a session key may call. Nothing
under `/gateway/rules` was on it, so every rule route was refused with HTTP 403 and the code
`session_key_out_of_scope`. The whole rule command group authenticates with a session key, so
"set up a rule" could not work at all - not partly, not sometimes, at all.

Every suite was green throughout, and that is the part worth keeping. The guard's own unit tests are
written from the guard's own list, so they agreed with it. The command line's tests mock the
transport, so they never sent a request. Nothing anywhere connected "a route was added" to "the guard
was told about it". This is the third time the same mechanism has bitten this one file: the skill
catalogue and the schedule surface each shipped refused-by-accident before it, and each was fixed by
adding rows to the hand-kept list, which fixes the instance and leaves the mechanism running.

---

## What was built

### The ruling, implemented exactly as the owner gave it

An agent credential may do everything with rules EXCEPT move one out of dry run.

| Verb | Route | Ruling |
| --- | --- | --- |
| GET | `/gateway/rules` | allowed |
| GET | `/gateway/rules/{id}` | allowed |
| GET | `/gateway/rules/{id}/firings` | allowed |
| POST | `/gateway/rules/draft` | allowed |
| POST | `/gateway/rules` | allowed - lands in dry run by the store's own shape |
| DELETE | `/gateway/rules/{id}` | allowed |
| POST | `/gateway/rules/{id}/promote` | REFUSED, deliberately and permanently |

It is a literal switch in a named helper (`RuleRoute`, with `IsRuleRoute` as the boolean the allow
list calls), not a prefix rule, and the comment says why in terms of this surface rather than in
general: `promote` sits one path parameter under the routes being opened, so a prefix on
`/gateway/rules` would have handed it over on the same day it handed over the list, silently, with
nothing written down that said so.

The refusal also carries its own sentence rather than the guard's general one. The general sentence
talks about the admission surface, which promoting a rule is not, and an agent handed the wrong
reason goes hunting the wrong problem.

### The test that would have caught it

`src/CcDirector.Gateway.Tests/SessionRuleRouteGuardCensusTests.cs`, built the way
`ContextLessRouteCensusTests` and `CensusRouteTenancyProbeTests` are built: it starts a real
`GatewayHost` and reads the FINALISED route table off it, so the subject of the test is the
application as built and never a list somebody maintains. It runs for both deployments - hosted and
self-host - because they map different route tables.

For that it needed a third state. An allow list denies a route it has never heard of, which is the
right default and a useless signal: from outside, a route nobody classified looks exactly like a
route somebody refused, and `Check` returns 403 for both. So `SessionKeyGuard` now exposes
`ClassifyRuleRoute`, returning `Allowed`, `RefusedOnPurpose`, or `Unclassified`. The test asserts
that no mapped `/gateway/rules` route is `Unclassified`. A route added to this surface tomorrow
cannot be forgotten - only classified.

Two further assertions, both there because of how this class of test fails open:

- **The surface must not be empty.** The headline assertion is "nothing was unclassified", and a
  pass condition that is an absence certifies a run that never happened: an enumeration that returned
  nothing - a filter typo, a route table that failed to build - would find no unclassified route and
  report success without having looked at anything. The presence of the surface is asserted first.
- **Both verdicts must actually occur.** Deleting every arm of the switch is caught by the
  unclassified assertion, but collapsing the switch to one blanket verdict would not be - and a
  blanket "allowed" is exactly the prefix rule the guard exists to avoid.

### The rows in the fast suite

`SessionKeyGuardTests` (in `Gateway.UnitTests`, which is not parked) gains the six allowed rows, the
promote refusal with its reason, ten shapes under `/gateway/rules` that the Gateway does not route
and must therefore not be authorized, the three-state classifier, and case and trailing-slash
handling.

Where those rows came from matters more than the rows: every one is read off the OTHER side - the
route table in `SessionRuleEndpoints`, and the client's own `RuleClient` - and never off the guard. A
test written from the implementation agrees with the implementation's mistakes.

---

## Watching each new test fail first

Three red runs, on the real suites, quoted from the console.

### Red run 1 - the pre-fix state: nothing on the surface classified

Every arm of the rules switch deleted, which is the state main was in.

Fast suite, `SessionKeyGuardTests`:

```
  Failed ...An_agent_may_draft_store_read_and_delete_a_rule(method: "GET", path: "/gateway/rules")
  Failed ...An_agent_may_draft_store_read_and_delete_a_rule(method: "POST", path: "/gateway/rules")
  Failed ...An_agent_may_draft_store_read_and_delete_a_rule(method: "POST", path: "/gateway/rules/draft")
  Failed ...An_agent_may_draft_store_read_and_delete_a_rule(method: "GET", path: "/gateway/rules/6b2f...")
  Failed ...An_agent_may_draft_store_read_and_delete_a_rule(method: "GET", path: "/gateway/rules/6b2f.../firings")
  Failed ...An_agent_may_draft_store_read_and_delete_a_rule(method: "DELETE", path: "/gateway/rules/6b2f...")
  Failed ...An_agent_may_not_move_a_rule_out_of_dry_run
   Assert.Contains() Failure: Sub-string not found
  Failed ...Case_and_a_trailing_slash_do_not_open_or_close_a_rule_route
  Failed ...The_classifier_tells_a_deliberate_refusal_apart_from_one_nobody_decided
Failed!  - Failed:     9, Passed:   154, Skipped:     0, Total:   163
```

Parked Gateway suite, the census test, which names every route it read off the built application:

```
 hosted=True: 7 mapped routes under /gateway/rules
 Unclassified     GET /gateway/rules
 Unclassified     POST /gateway/rules
 Unclassified     POST /gateway/rules/draft
 Unclassified     DELETE /gateway/rules/{id:guid}
 Unclassified     GET /gateway/rules/{id:guid}
 Unclassified     GET /gateway/rules/{id:guid}/firings
 Unclassified     POST /gateway/rules/{id:guid}/promote
  Error Message:
   Assert.Empty() Failure: Collection was not empty
Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2
```

That is the reported symptom of the original defect, printed route by route.

### Red run 2 - one route added and forgotten

Restored, then only the `promote` arm deleted. This is the case the census test exists for: six
routes classified, one nobody looked at.

```
 hosted=True: 7 mapped routes under /gateway/rules
 Allowed          GET /gateway/rules
 Allowed          POST /gateway/rules
 Allowed          POST /gateway/rules/draft
 Allowed          DELETE /gateway/rules/{id:guid}
 Allowed          GET /gateway/rules/{id:guid}
 Allowed          GET /gateway/rules/{id:guid}/firings
 Unclassified     POST /gateway/rules/{id:guid}/promote
  Error Message:
   Assert.Empty() Failure: Collection was not empty
Collection: ["POST /gateway/rules/{id:guid}/promote"]
Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2
```

### Red run 3 - the boundary given away

Restored, then `promote` flipped from `RefusedOnPurpose` to `Allowed`.

```
  Failed ...An_agent_may_not_move_a_rule_out_of_dry_run
   Assert.False() Failure
  Failed ...Case_and_a_trailing_slash_do_not_open_or_close_a_rule_route
   Assert.False() Failure
  Failed ...The_classifier_tells_a_deliberate_refusal_apart_from_one_nobody_decided
   Assert.Equal() Failure: Values differ
Failed!  - Failed:     3, Passed:   160, Skipped:     0, Total:   163
```

and the census test's blanket-verdict assertion:

```
  Error Message:
   Assert.NotEmpty() Failure: Collection was empty
Failed!  - Failed:     2, Passed:     0, Skipped:     0, Total:     2
```

### Restored - green

```
Passed!  - Failed:     0, Passed:   163, Skipped:     0, Total:   163  (SessionKeyGuardTests)
Passed!  - Failed:     0, Passed:     2, Skipped:     0, Total:     2  (the census test, both deployments)
```

and the census test's own output on the restored guard, which is the classification as it now
stands, read off the built application rather than off this document:

```
 hosted=False: 7 mapped routes under /gateway/rules
 Allowed          GET /gateway/rules
 Allowed          POST /gateway/rules
 Allowed          POST /gateway/rules/draft
 Allowed          DELETE /gateway/rules/{id:guid}
 Allowed          GET /gateway/rules/{id:guid}
 Allowed          GET /gateway/rules/{id:guid}/firings
 RefusedOnPurpose POST /gateway/rules/{id:guid}/promote
```

---

## The gate

- `.\scripts\test-local.ps1` - GREEN. Nine projects, every one `outcome=Completed`, 4863 tests, no
  failures. The Postgres proof rig (`cc-pg-test`, port 55432) was up for the run.
- `.\scripts	est-local.ps1 -Gateway` - GREEN. The parked suite, which is where the new census
  test lives: `outcome=Completed`, 2331 tests, 2327 passed, 0 failed, 4 skipped, 33 minutes 13
  seconds. It holds the machine-wide lock for the whole run; the wait is the design, not a hang.
- No non-ASCII bytes in any file touched (checked byte by byte, not by eye). No assistant, model,
  vendor or tool named anywhere in the change.

---

## What is NOT proven, said plainly

- **Nothing here was exercised against the live hosted Gateway.** The proof is the guard as a pure
  function and the route table of a locally-started host. The observed 403 that started this task was
  against the live service; the fix has not been re-observed there, because that needs a deploy, and
  this branch is not landed.
- **The census covers `/gateway/rules` and nothing else.** Every other surface on this guard is still
  a hand-kept list with no derived check behind it. The mechanism that produced this defect three
  times is closed for one surface, not for the file. Widening it to the whole route table is real
  work and was not in this task.
- **The test says every route has a verdict. It does not say the verdicts are right.** Whether an
  agent should be able to delete a rule is the owner's ruling, and it lives in the guard's comments
  and in the hand-written unit rows, both of which a determined mistake can change.
- **The guard is the only thing standing between an agent credential and promotion.**
  `RulePromotionGrant.FromAuthenticatedRequest` refuses a request with no caller it can name, but a
  session key resolves to a named caller like any other credential, so that factory would accept one.
  This was read in the code, not executed. It is why the refusal is in the guard and why the test
  that pins it matters.
- **The client-side `rule` command group is not on this base.** `tools/cc-devthrottle/src/rule_ops.py`
  exists on `origin/rule-authoring-by-conversation` and on neither `origin/main` nor this branch. The
  six routes above were read off that file to make sure the guard opens what the client actually
  sends, but nothing in this change tests the client, and the end-to-end path is only real once both
  land.
