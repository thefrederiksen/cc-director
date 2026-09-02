# Finding 3 - the red run, before the fix

`SessionScreenStoreTests.Append_TwoDirectorsCapturingOneSessionAtTheSameInstant_KeepsBothRows`.

Command:

```
dotnet test src/CcDirector.Gateway.UnitTests/CcDirector.Gateway.UnitTests.csproj \
  --filter "FullyQualifiedName~Append_TwoDirectorsCapturingOneSession" --nologo -v n
```

Result against the unfixed key:

```
Failed!  - Failed: 1, Passed: 0, Skipped: 0, Total: 1

SessionScreenStoreTests.Append_TwoDirectorsCapturingOneSessionAtTheSameInstant_KeepsBothRows [FAIL]
  Assert.True() Failure
  Expected: True
  Actual:   False
  SessionScreenStoreTests.cs(120)
```

Line 120 is the SECOND Director's append. It returned false - "this exact capture is already stored" -
because the key was (tenant, session, captured-at) and carried no Director, so director-2's distinct
capture read as director-1's duplicate and was dropped without an error.

## What else finding 3 needed, and why the live half needed nothing

The other half of the finding was that live certification never compared the stored row's Director with
the routed one. There is no live certification any more (finding 1), so that cannot occur - and it is
asserted rather than argued: `GatewayScreenReaderLiveReadTests`
`.A_row_captured_by_another_Director_is_never_returned_to_a_live_read_routed_elsewhere` is inspection
01's own repro, and it failed "Expected: Tunnel / Actual: Store" against the shipped reader.

## The green run, after the fix

`DirectorId` joins the primary key as its last component, so the (tenant, session, captured-at) prefix
still answers "this session's captures, newest first" directly; the duplicate test in `AppendOnce`
compares the Director too; the two reads break a same-millisecond tie on the Director so "the newest" is
deterministic; and `DirectorId` gets the explicit `C` collation every caller-supplied natural-key string
column in this schema carries.

The model change made the migrated-database tests fail with EF's own pending-model-changes warning -
which is that check working, and is why the migration was regenerated immediately afterwards.

## The green run

```
dotnet test src/CcDirector.Gateway.UnitTests/CcDirector.Gateway.UnitTests.csproj \
  --filter "FullyQualifiedName~Append_TwoDirectorsCapturingOneSession" --nologo -v q
Passed!  - Failed: 0, Passed: 1, Skipped: 0, Total: 1        exit 0
```

On commit `43694cffa`, and inside the green tip gate on the same commit (`Gateway.UnitTests`
outcome=Completed, total 3,262, 0 failed, runner exit 0). See `../runs.md`.

An earlier draft of this page pointed at a file called `regenerated-gate.md` that was never written.
That pointer was removed rather than left dangling: a reference to a proof that does not exist reads
exactly like a proof.

**One thing this green does NOT cover.** The new key component is exercised against SQLite here. The
only place it is exercised on POSTGRES - the provider the hosted Gateway runs - is
`SessionScreens_IdempotentOnTheNaturalKey_AndByteOrdinalAboutIt_OnRealPostgres`, which lives in
`Gateway.Tests`, and that suite has not run. See `../runs.md`.
