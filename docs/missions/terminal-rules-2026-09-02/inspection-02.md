# Fix round inspection 02

Verdict: not ready. The three High findings from inspection 01 are no longer present in the product
paths I inspected, and each of the six fix-specific tests goes red when its production fix is removed.
I found four Medium proof or observability defects and one Low defect in the repaired row bound.

Scope: `git diff origin/main...HEAD` at `9e44f7cb8`, the inspection 02 brief, inspection 01, rulings 12
and 13, the fix-round plan, report, runs, all six red-run records, and the affected source and tests.
No production fix was made. Every temporary mutation and repro was restored.

## Findings

### Medium 1 - The real-Postgres natural-key proof never varies DirectorId

File/line: `src/CcDirector.Gateway.Tests/Data/PostgresProviderProofTests.cs:453-503`;
`src/CcDirector.Gateway/Screens/SessionScreenStore.cs:115-149`;
`src/CcDirector.Gateway/Data/GatewayDbContext.cs:516-533`;
`src/CcDirector.Gateway.Migrations.Postgres/Migrations/20260902154819_AddSessionScreens.cs:14-37`;
`docs/missions/terminal-rules-2026-09-02/fix-round/runs.md:335-350`.

Claimed: the runs say the six local PostgreSQL tests include the screen store's key with `DirectorId`
in it on real PostgreSQL. The provider test's summary says that the key permits two Directors to keep
distinct captures of one session at one instant.

Actual: the test deliberately holds the Director constant. All three appends at lines 493, 494 and 499
use `d-idem`; only the case of the session id changes. It proves session-id collation and same-Director
idempotence, not the new Director component's behavior. The product shape is otherwise right:
`GatewayDbContext.cs:516-533` declares `(TenantId, SessionId, CapturedAtUtc, DirectorId)`, and
`20260902154819_AddSessionScreens.cs:19-36` creates that primary key and applies `C` collation to both
string components.

Established: I removed only `s.DirectorId == directorId` from `AppendOnce`. The SQLite test
`Append_TwoDirectorsCapturingOneSessionAtTheSameInstant_KeepsBothRows` went red (`Expected: True`,
`Actual: False`). With the same broken production code, the named real-PostgreSQL test still passed
1/1. The current tree therefore has a live SQLite instrument for the behavior, but no live PostgreSQL
instrument for it.

### Medium 2 - A turn-end screen dropped before a stream client exists is not counted

File/line: `src/CcDirector.ControlApi/GatewayScreenSink.cs:28-32,43-59`;
`src/CcDirector.ControlApi/ControlApiHost.cs:763-769`;
`src/CcDirector.ControlApi/GatewayStreamClient.cs:831-837,843-917`;
`src/CcDirector.Gateway.UnitTests/Screens/ScreenPushLossBoundaryTests.cs:59-101`;
`docs/missions/terminal-rules-2026-09-02/fix-round/report.md:139-154`.

Claimed: the sink says every drop is logged and counted by `ScreenPushesDropped`; the report calls the
loss a named, counted, logged event. It also says the old claim that a miss costs only a round trip was
deleted.

Actual: `ControlApiHost` always installs `GatewayScreenSink`, but its resolver can return null while no
stream client is configured or available. That branch logs `NOT pushed` and returns without incrementing
the counter. The two shipped boundary tests start with an existing, unconnected client and never enter
this earlier loss branch. Separately, `GatewayStreamClient.cs:831-837` still says a miss loses only the
round trip and that nothing is silently degraded, directly contradicting the corrected loss description
at lines 903-917.

Established: I temporarily sent a valid turn-end screen through `new GatewayScreenSink(() => null)` and
asserted a one-count delta. The focused test went red (`Expected: 1`, `Actual: 0`). As controls, disabling
the increment inside `DropScreen` made both shipped loss tests red, and a temporary successful live-hub
push passed with delivered `+1` and dropped `+0`. The counter is exact in the paths it covers; the
pre-client drop is outside that coverage.

### Medium 3 - CollationExtras still fails open when a required extra loses its collation

File/line: `src/CcDirector.Gateway.Tests/Data/PostgresProviderProofTests.cs:230-256,317-327,373-399`;
`docs/missions/terminal-rules-2026-09-02/fix-round/runs.md:216-220`.

Claimed: each `CollationExtras` entry records a deliberate reason that a non-key column must use exact
`C` collation. The catalog proof obtains the live set of explicitly collated columns and presents its
reverse comparison as protection against a broken derivation.

Actual: `CollationExtras` is subtracted only from unexpected live columns. Nothing asserts that its
members remain present in the live catalog. If a future migration removes `C` from, for example,
`device_credentials.DeviceKeyHash`, the pair disappears from `actual`, is not model-derived `required`,
and satisfies every assertion. The runs document this exposure but leave it unresolved.

Established: immediately after the real catalog query I temporarily removed
`("device_credentials", "DeviceKeyHash")` from the returned live set, simulating that column losing
its explicit collation. The real-PostgreSQL collation test still passed 1/1. This is the old fail-open
shape for the reasoned extras, even though the main key-column population is now derived.

The new `InheritedCollatedNonKeyColumns` debt set did not reproduce that defect. I independently armed
both staleness paths. Adding `session_screens.SessionId` failed by name because it is model-derived;
adding `known_repositories.NoSuchColumnAnyMore` failed by name because it is absent from the live
catalog. Both mutations were restored and the six PostgreSQL provider proofs passed afterward.

### Medium 4 - The published gate verdict equates six local tests with 48 skips and contradicts itself

File/line: `docs/missions/terminal-rules-2026-09-02/fix-round/report.md:6-15,38-39,61-70,230-250`;
`docs/missions/terminal-rules-2026-09-02/fix-round/runs.md:322-351,374-401`;
`docs/missions/terminal-rules-2026-09-02/phase-0-report.md:132-134,244-256`.

Claimed: the report says the continuous-integration run's 48 PostgreSQL skips are the six locally run
`PostgresProviderProofTests`, so the two runs together cover the suite. Its opening also says every green
is in hand except one suite that has not run, then six lines later says that suite is now run. The old
gate section remains below and still calls the gate red and `Gateway.Tests` outstanding.

Actual: run 33694484448 is genuinely successful at `e63879af0`, and only mission documents changed
between that commit and this inspection tip. Its `Gateway.Tests` result is 2,328 total, 2,280 passed,
48 skipped, 0 failed. But the 48 source attributes are distributed 15 + 8 + 8 + 6 + 4 + 2 + 2 + 2 + 1
across nine classes. The local filter ran only the six in `PostgresProviderProofTests`; it did not replace
the other 42 skips.

The inventory printed in `runs.md:327-333` is also not the 48 skips from that project. It sums to 54,
includes six `HostedSchemaRefusesAnUnownedRowTests` and one `StoredScreenRigReadTests` from
`Gateway.UnitTests`, and omits the one PostgreSQL-gated test at
`Stats/TheRejectedChainUpgradesToTipTests.cs:107`. Thus `report.md:39` ("the 48 CI skips are these") and
`runs.md:349` ("the suite is covered") are false. The report was appended with a new verdict rather than
rewritten into one internally consistent account, contrary to the inspection brief's explicit question.

Established: `gh run view 33694484448` confirmed the successful commit and jobs; source enumeration of
the three PostgreSQL Fact attributes in `Gateway.Tests` totals exactly 48; and the documented local
command's filter and output are six tests. Forty-two skipped tests have no same-tree replacement in the
evidence presented here.

### Low 1 - The over-cap repair cannot trim through a capture-time tie

File/line: `src/CcDirector.Gateway/Screens/SessionScreenStore.cs:159-178,243-264`;
`src/CcDirector.Gateway.UnitTests/Screens/SessionScreenStoreTests.cs:330-376`.

Claimed: the sweep repairs an over-cap session to exactly the newest 200 rows and returns the number it
removed. The shipped control verifies the boundary with distinct capture times.

Actual: `TrimToCap` orders only by `CapturedAtUtc`, selects the 200th row's timestamp, then deletes rows
whose timestamp is strictly less than that cutoff. `DirectorId` makes equal-time rows distinct and the
read path already uses it as a tie-breaker, but the trim path does not. If the cutoff crosses two rows
with one timestamp, neither tied row is strictly older, so the session remains over the bound.

Established: I temporarily seeded 201 valid rows directly: 199 newer rows plus two oldest rows with the
same capture instant and distinct Director ids. The shipped sweep test went red (`Expected: 1`,
`Actual: 0`). This needs only the two-Director same-instant shape that finding 3 explicitly made valid,
not hundreds of Directors. Removing the new sweep trim also made the original distinct-time repair test
go red, so the fix itself is live; its boundary algorithm is incomplete.

## Checks that held

- Finding 1: every production live-screen caller found by complete call-site search goes through
  `ReadLiveAsync`, whose only data call is `GetScreenGridAsync`. Reintroducing a stored-screen return made
  three of the five focused live-reader tests red; all five pass restored.
- Finding 2: `_streamBytesReflected` has one write, inside the same parser lock used by both frame reads.
  Replacing the returned mark with the buffer total reproduced `Expected: 18`, `Actual: 36`; the four
  capture tests pass restored.
- Finding 3: the two-Director SQLite behavior test is live, and the model, migration, and real catalog all
  carry the new key component and byte-ordinal collation. The provider-behavior gap is Medium 1.
- Finding 4: replacing the sink's mapped rows with a constant made both default-gate mapping tests red.
  No rig was required.
- Finding 5: removing the drop increment made both shipped loss tests red. Successful delivery changed
  only the delivered counter. The uncovered earlier loss is Medium 2.
- Finding 6: deleting the sweep's over-cap call made its focused test red. The distinct-time newest-row
  control passes; the equal-time boundary is Low 1.
- Ruling 13: `phase-0-proofs.md:209-238` replaces row 6 with the stronger always-live-tunnel question and
  marks row 7 WITHDRAWN. Neither row survives by rewording the old certification.

## Validation record

- Restored focused Gateway unit set: 11 passed, 0 failed, 0 skipped.
- Restored capture set: 4 passed, 0 failed, 0 skipped.
- Restored `PostgresProviderProofTests` against the real local PostgreSQL server: 6 passed, 0 failed,
  0 skipped.
- Both `InheritedCollatedNonKeyColumns` guard-arm mutations failed by the injected column's name.
- Every known-bad edit and temporary repro was removed. The worktree was clean before this report was
  added, and this report is intentionally left uncommitted for the Manager.
