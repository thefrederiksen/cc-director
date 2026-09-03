# Fix round E - the Architect's rulings on inspection E

Inspection E read fix round D and returned **four findings: one high, two medium, one low**
(`inspection-e.md`). All four are accepted. The round goes back to the Manager who built it, which is
the workflow's rule: failures go back to the builder, not to the inspector.

**This inspection carries more weight than a code read, and that is worth saying.** It did not assert
these findings, it RAN them: an executable probe that posted `{"rules": null}` and watched the command
line print "No rules yet"; a direct-store positive control proving `SessionRuleStore.Create` persists
without a screen read anywhere in its call path; a temporary browser-client probe that failed with
`promise resolved "null" instead of rejecting`, then removed. Findings proven by execution are not
arguable, and none of these are being argued.

It also cleared a large amount deliberately: the proposal identity path, acknowledgement persistence,
the independent promotion grant, store-level ceiling bounds, malformed-number refusals, the route
census, and the closed primitive-call vocabulary. Seven of the eleven rulings survived an adversarial
read that was actively trying to break them.

---

## RULING E1 - grounding becomes an invariant of the STORE, not a habit of one route

Finding 1, and it is the one that matters. The create endpoint re-reads the screen and grounds before
calling `store.Create` - but `SessionRuleStore.Create` is public, takes no session and no screen, and
its database gate checks only that at least one trigger word exists. So the grounding that ruling D2
made unbypassable is unbypassable **on one route**, by convention, and the comments call that route
"the one door" - a property the persistence layer does not enforce. The round-trip test that persists
five trigger strings straight through the store is a positive control for the bypass.

**Close it at the store, do not narrow the claim.** D2 is the headline safety claim of this feature; a
claim that holds only for the callers that exist today is the kind of claim that stops being true the
first time somebody adds a caller, and nothing fails when it does.

**Use the pattern this codebase already has.** `GatewayDbContext` already refuses a rule moving to live
unless the context carries `PromotionInEffect` - non-forgeable evidence minted by the one path entitled
to mint it. That is exactly the shape needed here, and reusing it means grounding and promotion are
guarded by one mechanism a reader learns once rather than two that resemble each other.

- Grounding evidence is minted ONLY by a fresh Gateway screen read, and `Create` cannot persist trigger
  words without it.
- The database gate refuses a rule whose trigger words carry no evidence, so a direct context write
  fails too.
- The regression test is the one the inspection describes: attempt the public store path AND the direct
  context path without evidence, observe the refusal in both, **and include a control that reaches
  storage through the real grounded route** - so the test cannot pass by refusing everything.
- The existing round-trip test moves onto the grounded path or carries evidence. Do not delete it.

## RULING E2 - a present field of the wrong shape is as broken as a missing one

Finding 2. D8 removed one half of an absence-shaped failure and left the other: both clients reject a
MISSING container field and accept a PRESENT one without checking its shape, so `{"rules": null}` prints
"No rules yet" and `{"firings": null}` prints "It has not fired yet". A malformed or version-skewed
answer is still reported as the positive fact that nothing exists.

- Both clients validate the runtime shape - arrays, booleans, objects, and the required fields inside
  each record - not merely the presence of the key.
- Tests cover **null, wrong type, and a malformed child**, each with a valid non-empty control beside
  it. The current tests cover `{}` and a valid empty array, which is why this survived.
- The command line stops supplying empty strings, `(none)` and `0` for missing fields inside a rule or
  a firing. A field the Gateway did not send is a broken answer, not a zero.

## RULING E3 - extract the provenance join and test it, keeping the production code in the path

Finding 3. `GatewayHost.ReadRuleScreenAsync` is where the caller's tenant, the pushed roster location,
the Director route, the tunnel read and the roster-owned origin all join - and **no test calls it**. The
hosted fixture always substitutes the test reader, so the green HTTP tests prove the handoff to the seam
and replace the exact code that establishes provenance. Even the route-level tenant mutation reached the
fake.

Take the inspection's own remedy: **extract the production composition behind an injectable type and
test that type**, rather than replacing it. Then keep the production reader in the path and observe its
far side, with positive presence controls first:

| Case | Expected |
| --- | --- |
| An owned session | The exact screen rows, and the origin taken from the roster row |
| A second tenant's session | Refused, and the authoring call never made |
| A session that is not on the roster | Refused, and the authoring call never made |
| A Director that vanished between locate and read | Refused with its own sentence, never an empty screen |
| A screen that reads empty | Refused - an empty screen is not a capture |

The fourth row is the one to get right. **A Director disappearing must not be indistinguishable from a
session with nothing on its screen**, because the second one is a state a rule could be authored
against and the first is a failure.

The demonstrations will also exercise this code for real against a live Director, and that is
complementary rather than a substitute: the demonstration proves it works once, the test proves it keeps
working.

## RULING E4 - the placeholder goes, and the Architect takes the parked suite off the seats

Finding 4. `GATEWAY_TESTS_RESULT` is a literal token sitting where a gate result belongs, in a round
that reports the evidence rules as applied throughout. Replace it with the exact command, commit, count,
elapsed time and outcome of a completed run - **or say plainly that the full suite was not run.** Either
is honest. The token is not.

**The cause is mine to fix, not yours.** The inspection's own full run produced no completion in ten
minutes because other mission worktrees were holding the machine-wide lock - and that is a consequence
of the Architect running three and four seats at once, each of which dutifully queues for the same
serialised suite. So:

**No seat runs the full parked `Gateway.Tests` again.** Run the narrow filters that cover your own
change and report those honestly. **The Architect runs the full parked suite once, on the final merged
tree, before anything lands on `main`** - which is where it is worth most anyway, because that is the
only tree that will actually exist on main.

---

## What is NOT reopened

Seven rulings survived an adversarial read and stay closed: the proposal identity path, acknowledgement
persistence, the independent promotion grant, store-level ceiling bounds, malformed-number refusals, the
route census, and the closed primitive-call vocabulary. Do not revisit them. Do not "improve" them while
you are in the area.

## The gate

- `.\scripts\test-local.ps1` green.
- `Gateway.Tests` **filtered to your change only**. Not the full suite - see ruling E4.
- Every web workspace and `npm run typecheck`, because E2 touches both clients.
- `pytest tools/cc-devthrottle/tests/`.
- Watch every load-bearing test fail first, with the command output and the broken commit quoted. Your
  previous round's probe commits stacked because a revert used a flag that does not exist; the
  inspection disclosed it and read around it, but do not repeat it - revert each probe before the next.
- ASCII only. No mention of any assistant, model, vendor or AI tool in a commit message, a document or
  a comment.

## How to finish

Push on `mission/rules-fix-d` and report to the Architect in ONE SINGLE LINE - fleet messages truncate
at the first newline. Append to `fix-round-d-report.md` or write `fix-round-e-report.md`, one section
per ruling, saying whether it is closed and what proves it. Do not open a pull request and do not merge.
