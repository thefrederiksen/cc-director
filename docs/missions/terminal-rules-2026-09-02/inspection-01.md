# Phase 0 inspection 01

Verdict: not ready. I found three ways a stored frame can be accepted as live truth when it is not the
current frame, plus two gaps in the durability/proof claims and one concurrency hole in the advertised
row bound.

Scope: `git diff origin/main...HEAD`, the brief, phase 0 proofs and report, and rulings 1 through 10.
The tenant boundary itself held under inspection: `DirectorHub.PushScreen` enters the tenant bound to
the connection, and `SessionScreenEntity` has the normal global tenant query filter. Finding 3 is a
same-tenant, cross-Director ownership defect, not a cross-account leak.

## Findings

### High 1 - A recent pushed count is not a current count, so the reader knowingly serves a different live screen

File/line: `src/CcDirector.Gateway/Screens/GatewayScreenReader.cs:44-65,77-82,184-211`;
`src/CcDirector.ControlApi/GatewayStreamClient.cs:101-104,134,155-160`;
`src/CcDirector.ControlApi/ControlApiHost.cs:974-1040`;
`src/CcDirector.Gateway.UnitTests/Screens/GatewayScreenReaderFreshnessTests.cs:149-184`.

Claimed: `phase-0-report.md:45-49` says the live read gets a stored screen only when the byte mark
equals the current pushed count, the tunnel is connected, and the snapshot is under twenty seconds old;
otherwise it tunnels, and an unavailable tunnel is unreadable. `phase-0-report.md:123-124` calls the
frozen-stream row proof that a stale screen is not certified.

Actual: terminal bytes do not push a session delta. `WireDoorbellPush` pushes on activity and several
other state events, while the only unconditional refresh is the full snapshot timer every ten seconds.
After a fresh snapshot at N, the real terminal can move to N+1 without an activity transition while the
Gateway still holds N, is connected, and has an age below twenty seconds. `CertifyStored` sees all three
facts as passing and returns the old row. The source label is honest about where the row came from, but
the reason string at line 210 falsely says the terminal is unchanged.

Established: the shipped negative-control test itself freezes the pushed value at 500, advances the
clock to nineteen seconds, and asserts `Source.Store`, the old `StoredRows`, and zero tunnel calls even
though its tunnel is ready with different `LiveRows` (`GatewayScreenReaderFreshnessTests.cs:165-175`).
Only after twenty-one seconds does it require the live tunnel rows. Its one-byte test manually calls
`ApplyDelta` (`:201-214`); it does not establish that a buffer write causes that delta, and the production
subscription list establishes that it does not. A live caller can therefore act on a stale screen for
roughly a timer interval, including when the actual tunnel could have supplied the current one.

### High 2 - The capture can pair an old parser frame with the new byte total

File/line: `src/CcDirector.Core/Memory/CircularTerminalBuffer.cs:113-153`;
`src/CcDirector.Core/Sessions/Session.cs:2045-2057,2169-2189`;
`src/CcDirector.Core.UnitTests/Storage/TurnEndScreenCaptureTests.cs:89-98`.

Claimed: `phase-0-report.md:20-25` and the comment in `Session.cs:2176-2183` say reading the counter
before the parser snapshot can only understate the returned frame, never overstate it. Row 0 claims the
grid and mark are taken together.

Actual: `CircularTerminalBuffer.Write` increments `_totalWritten` while holding the buffer lock, releases
that lock, and only then invokes `OnBytesWritten`. The session parser consumes the bytes from that later
callback. A writer paused between those operations has already exposed total N while the parser still
contains frame N-k. `SnapshotLiveScreenWithBufferMark` reads N first and then locks the parser, so it
returns the old frame with N. When the callback completes, the next pushed `TotalBufferBytes` is also N;
the reader's equality, connection, and age checks all pass for the old frame.

Established: I temporarily added a controlled rendezvous subscriber before the session parser. It
paused a second write after the counter advanced but before the parser callback, then asserted that the
capture mark equalled the buffer's new total while the rows contained `OLD_FRAME_MARKER` and did not
contain `NEW_FRAME_MARKER`. Releasing the writer made the new marker appear. The focused run executed one
test and passed; the temporary test was then removed. The shipped row-0 assertion checks only
`screen.BufferBytes <= buffer.TotalBytesWritten`, which is also true in this bad interleaving and says
nothing about which bytes the returned rows reflect.

### High 3 - Live certification does not bind the stored row to the routed Director

File/line: `src/CcDirector.Gateway/Screens/SessionScreenStore.cs:106-126,166-176`;
`src/CcDirector.Gateway/Screens/GatewayScreenReader.cs:152-211`;
`src/CcDirector.Gateway/Data/GatewayDbContext.cs:513-527`.

Claimed: `phase-0-report.md:33-37,45-47` says the key orders one session's captures and that the owning
Director's connected, fresh snapshot certifies the stored screen. The reader comments consistently call
the route the owning Director.

Actual: the primary key is `(tenant, session, captured-at)`, `ReadLatest` filters only by session, and
`CertifyStored` never compares `stored.DirectorId` with the route's `directorId`. It fetches a row from any
Director in the tenant, then uses the routed Director's connection and pushed byte count to certify it.
Two Directors with the same session id and equal byte totals are enough to return Director B's rows for
a live read routed to Director A. The same missing key component also lets an exact capture-time collision
be treated as Director A's duplicate instead of Director B's distinct row.

Established: I temporarily added a focused reader test that stored the row under `director-2`, registered
and snapshotted `director-1` with the same session id and byte total, and read through a route to
`director-1`. It positively asserted that history named `director-2`, while the live result was
`Source.Store`, returned Director 2's rows, and made zero tunnel calls. The one-test run passed; the
temporary test was then removed. The global tenant filter still prevents cross-account reads, but it
does not repair this same-tenant ownership mismatch.

### Medium 1 - The end-to-end proof accepts a transport that replaces every screen with arbitrary nonblank text

File/line: `src/CcDirector.ControlApi/GatewayScreenSink.cs:48-60`;
`src/CcDirector.Core.UnitTests/Storage/TurnEndScreenCaptureTests.cs:89-103`;
`scripts/terminal-rules-screen-proof.ps1:506-534,584-614`;
`src/CcDirector.Gateway.UnitTests/Screens/StoredScreenRigReadTests.cs:65-79`.

Claimed: `phase-0-proofs.md:101-110` requires every field and byte-identical rows, and row 4 at
`:154-172` requires the rows that were on the real terminal, quoted against the database row.
`phase-0-report.md:130-140` says every instrument was run known-bad.

Actual: row 0 stops before `GatewayScreenSink`, as its own comment correctly says. The store tests seed
the store by hand. The rig accepts either `STORED` or `TUNNEL` while the Director is up, then its offline
readback asserts only `HasGrid`, a nonempty list, one nonblank row, a positive byte mark, and a nonblank
Director id. It prints rows but never compares them with the terminal marker or the capture-side rows.
Those checks all pass if the sink replaces the terminal content with any nonblank string.

Established: I changed only `GatewayScreenSink`'s mapping to
`Rows = new List<string> { "MANGLED CONSTANT" }` and ran the complete Gateway unit project. The known-bad
build completed with 3,189 passed, 3 skipped, 0 failed, exit 0. The mutation was reverted. Inspection of
the rig's positive predicates shows the same mutation satisfies its source check and every readback
assertion, so the script's final `ROW 4 PROVEN` line can certify a mangled push path. This is a broken
instrument for the report's field-integrity and real-content claim.

### Medium 2 - A reconnect-window failure permanently removes that turn from Gateway screen history

File/line: `src/CcDirector.ControlApi/GatewayScreenSink.cs:16-21,32-61`;
`src/CcDirector.ControlApi/GatewayStreamClient.cs:791-815`;
`src/CcDirector.Core/Storage/TurnReviewLogger.cs:116,125-155`.

Claimed: `phase-0-report.md:27-31` says fire-and-forget is deliberate because a missed screen costs only
a round trip and "never a record"; the next turn sends a fresh one. The production comments repeat that
nothing is silently degraded.

Actual: if `_connection` is absent or not connected, `PushScreen` returns without retaining the capture.
If `InvokeAsync` fails, `SendAsync` logs and discards it. There is no sequence, outbox, retry, or reconnect
replay for screens. The next turn sends a different immutable history row; it cannot reconstruct the
missed turn. `TurnReviewLog` is a separate local file write and no code replays it into
`SessionScreenStore`. Thus the local record may survive, but the Gateway-wide history record promised by
this phase is permanently absent. If the Director then goes offline, the history read has no fallback at
all. The report's statement is not an honest description of the loss boundary.

Established: the complete `ScreenPush` usage enumeration has one producer path -
`TurnReviewLogger -> GatewayScreenSink -> GatewayStreamClient.PushScreen` - and no recovery reader from
`TurnReviewLog`. The explicit early return at `GatewayStreamClient.cs:806-807` and swallowed send fault at
`:811-814` are the two permanent-loss paths.

### Low 1 - The 200-row cap is not exact across the overlapping Gateway processes the store acknowledges

File/line: `src/CcDirector.Gateway/Screens/SessionScreenStore.cs:42,45-47,87-98,101-158`;
`src/CcDirector.Gateway.UnitTests/Screens/SessionScreenStoreTests.cs:122-146`.

Claimed: `phase-0-report.md:33-37` says each session is bounded at 200 rows inside the push transaction;
the store comment says the cap holds even under a burst.

Actual: `_gate` is per `SessionScreenStore` instance, not cross-process. The code itself expects two
Gateway processes to overlap during a deploy. With 200 committed rows, two processes can begin distinct
capture transactions, each insert one row, each count only its own 201 visible rows, and each select the
same oldest row for deletion. Only one deletion removes that row; both new rows can commit, leaving 201.
There is no database constraint, advisory lock, or post-commit repair that enforces the count invariant.
A later isolated write repairs the count, but an idle session remains above the advertised bound until
then or until retention.

Established: the transaction is visibly insert/save, count/select/delete, then commit; the only lock is
the instance field. The sole cap test appends 203 rows sequentially through one store instance and proves
only that serial case. The overlapping-process condition is not hypothetical infrastructure invented by
the inspection; `SessionScreenStore.cs:89-91` names it as the reason for the duplicate retry.

## Validation record

- Focused unmodified branch: row-0 capture tests, 3 passed; all screen-namespace tests, 26 passed and the
  rig-only readback explicitly skipped because no rig database was supplied.
- Known-bad capture/parser rendezvous: 1 executed, 1 passed, proving the bad state was reached; reverted.
- Known-bad cross-Director certification: 1 executed, 1 passed, proving the bad state was returned; reverted.
- Known-bad sink content replacement: full Gateway unit project, 3,189 passed, 3 skipped, 0 failed; reverted.
- Default local gate: eight projects passed, but the runner exited 1 because
  `InstallAsync_FailedVenvRebuild_LeavesNoManagedShim` failed after about thirty seconds. That unrelated
  case passed by itself immediately afterward (1 executed, 1 passed), but the gate result remains red and
  must not be reported as a clean run.
- Full parked gate: Core completed with 4,374 passed, 8 skipped, 0 failed. Gateway completed with
  2,316 passed, 4 skipped, 2 failed after 39m40s, so the parked gate is red. Both failures were configured
  Postgres entitlement proofs opening the shared throwaway database: migration failed after 21 attempts
  with SQLSTATE 42P07, `relation "session_screens" already exists`. A read-only catalog query established
  the exact disagreement: the database contains `gateway.session_screens` and history row
  `20260902105640_AddSessionScreens`, while this branch carries
  `20260902115702_AddSessionScreens`. This is consistent with the report's warning that the migration is
  provisional and will be deleted/regenerated, but it means the current full parked gate and configured
  Postgres-open proof are not clean and must not be reported as such.

No production fix was made. All temporary repro and mutation edits were reverted. This inspection file
is intentionally left uncommitted for the Manager.
