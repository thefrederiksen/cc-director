# Finding 1 - the red run, before the fix

The finding-1 assertions run against the UNFIXED reader, on a temporary class
`RedRunFinding1Tests` holding exactly the assertions that ship in
`GatewayScreenReaderLiveReadTests`. The temporary class was deleted after this run.

Command:

```
dotnet test src/CcDirector.Gateway.UnitTests/CcDirector.Gateway.UnitTests.csproj \
  --filter "FullyQualifiedName~RedRunFinding1Tests" --nologo -v n
```

Result:

```
Failed!  - Failed: 2, Passed: 0, Skipped: 0, Total: 2

RedRunFinding1Tests.The_store_never_answers_the_live_question_even_when_every_freshness_fact_holds [FAIL]
  Assert.Equal() Failure: Values differ
  Expected: Tunnel
  Actual:   Store

RedRunFinding1Tests.A_row_captured_by_another_Director_is_never_returned_to_a_live_read_routed_elsewhere [FAIL]
  Assert.Equal() Failure: Values differ
  Expected: Tunnel
  Actual:   Store
```

That is the defect stated in one line by the runner. With a screen stored, the owning Director's
tunnel connected, its snapshot one second old, and the pushed byte count exactly equal to the mark
taken at capture, the shipped reader answered the LIVE question from the STORE - and in the second
case it did so with a row captured by a different Director entirely.

## The green run - PENDING

**The green side of this finding is NOT in hand and is not quoted here.**

A green run WAS taken when the fix landed, and it passed: `--filter "FullyQualifiedName~Screens"` gave
24 passed and 1 skipped, and the full Gateway unit project gave 3,185 passed and 3 skipped. Those runs
were made at 11:0x-11:3x, BEFORE the provisional migration was deleted and regenerated on the new main
snapshot at 11:48. A green taken against a migration that no longer exists is not evidence about the
tree that ships, so those numbers are withdrawn as this finding's green rather than left standing.

Later runs did exercise this class after the regeneration - the full Gateway unit project at 3,259
passed, and the default local gate - but as suite totals, not as this finding's own quoted run, and the
newest of them predates the last three commits on the branch.

**What is owed:** re-run the filter above and quote it. The mission was stood down from the machine-wide
test lock mid-round while a production outage was fixed, so this waits on the Architect's clearance. It
is PENDING, not failed and not assumed.
