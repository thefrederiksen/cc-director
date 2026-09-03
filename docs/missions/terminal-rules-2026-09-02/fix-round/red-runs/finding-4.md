# Finding 4 - the known-bad run, before the fix

This is the finding that invalidated the phase's headline claim, and its red run is shaped
differently from the others: the defect was not that a test failed, it was that the inspector's
mutation kept the whole suite GREEN. So the red run here is the mutation itself.

## The mutation

Exactly the inspector's, one line in `GatewayScreenSink`'s mapping - every screen's rows replaced
with a single constant:

```
-        Rows = screen.Rows.ToList(),
+        Rows = new List<string> { "MANGLED CONSTANT" },
```

## What it did before this fix

Inspection 01, recorded by the inspector against the shipped code:

```
full Gateway unit project: 3,189 passed, 3 skipped, 0 failed, exit 0
```

A push path that replaced every terminal screen with arbitrary text was indistinguishable, to the
entire suite, from a correct one. The rig would have printed ROW 4 PROVEN on it too.

## What it does now

The mapping is a named function and `GatewayScreenSinkMappingTests` compares the push with the
screen it came from, field for field and row by row. Run here with the same mutation applied:

```
dotnet test src/CcDirector.Gateway.UnitTests/CcDirector.Gateway.UnitTests.csproj --nologo -v q

Failed!  - Failed: 3, Passed: 3253, Skipped: 3, Total: 3259

  GatewayScreenSinkMappingTests.The_push_carries_the_captured_screen_across_field_for_field [FAIL]
  GatewayScreenSinkMappingTests.The_push_holds_its_own_copy_of_the_rows [FAIL]
  SessionHistoryStoreTests.Two_tenants_never_see_each_others_history [FAIL]
```

The third failure is NOT the mutation and is not claimed as one: `Two_tenants_never_see_each_others_history`
passed in the run immediately before this one and in the run immediately after, both on the same
commit. It is reported here as observed rather than averaged away or left out.

## The green run, with the mutation reverted

```
dotnet test src/CcDirector.Gateway.UnitTests/CcDirector.Gateway.UnitTests.csproj --nologo -v q
Passed!  - Failed: 0, Passed: 3256, Skipped: 3, Total: 3259
```

So the mutation is now caught by the DEFAULT gate, without a rig, in about a minute.

## The rig half

The unit-level mapping test covers the seam where a screen becomes a push. It does not cover the hub
method, the transport, or the store write - and inspection 01's point was that nothing did. The rig
now compares content across the whole chain:

- The turn's command ENDS on three lines this run authored and stamped with its own timestamp
  (`TR_SCREEN_PROOF_<stamp>_ALPHA/BRAVO/CHARLIE`), so they are on the final screen rather than
  scrolled away, and no constant and no leftover row from an earlier run can satisfy them. The
  read-back requires all three among the stored rows, in the order they were printed.
- While the machine is still up the rig reads the session's terminal text over the `buffer` verb - a
  DIFFERENT path from the parser-grid snapshot the capture took - and the read-back requires every
  nonblank stored row to appear in it.
- The rig also checks its own instrument first: the three markers must be present in that terminal
  text before any comparison is trusted, so a run whose command did not do what this row assumes
  fails as a broken instrument rather than as a defect in the store.
- Both sides are written to `stored-row.txt`, quoted line by line, and the script prints them.

The comparison is a substring match of each stored row against the terminal text rather than a
line-for-line equality of the whole grid, and that limit is stated in the test: grid rows are
trailing-trimmed and the raw buffer keeps its own line breaks, so the two shapes do not admit a
stricter comparison. It is enough to defeat every substitution the inspection demonstrated.

The rig run itself is recorded in `rig-run.md`.
