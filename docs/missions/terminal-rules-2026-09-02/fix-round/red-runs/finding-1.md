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

## The green run, after the fix

The same assertions, now in the shipped class `GatewayScreenReaderLiveReadTests` against the fixed
reader (`ReadLiveAsync` takes only the route, so the construction line is shorter; the assertions are
identical):

```
dotnet test src/CcDirector.Gateway.UnitTests/CcDirector.Gateway.UnitTests.csproj \
  --filter "FullyQualifiedName~Screens" --nologo -v q
Passed!  - Failed: 0, Passed: 24, Skipped: 1, Total: 25

dotnet test src/CcDirector.Gateway.UnitTests/CcDirector.Gateway.UnitTests.csproj --nologo -v q
Passed!  - Failed: 0, Passed: 3185, Skipped: 3, Total: 3188
```

The one skip is `StoredScreenRigReadTests`, which is gated on a rig database and reports SKIPPED
without one. A skip is not a pass and the rig script asserts that test actually ran.
