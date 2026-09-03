# Fix round - the plan

Manager's plan for the round ruled by `rulings/r12-the-fix-round.md`, answering the six findings in
`inspection-01.md`. Written before any code was changed.

Standing rule for the whole round, from r12: **every fix owes a test that FAILED BEFORE IT.** Each
finding below names the test that goes red first, and the report will quote the red run and the green
run for every one of them. A fix whose test passes on the unfixed code has tested nothing.

## Step 0 - done before anything else: the shared throwaway database was reset

The Architect's standing rule: every time the provisional migration id changes, every database it has
touched is reset. Evidence taken before the reset, from `ccpgtest` on the local test server:

```
MigrationId in gateway."__EFMigrationsHistory":  20260902105640_AddSessionScreens
table present:                                   gateway.session_screens
this branch carries:                             20260902115702_AddSessionScreens (Postgres)
                                                 20260902105533_AddSessionScreens (SQLite)
```

So the database held a history row for a migration id that no longer exists in the tree, and the next
migrate attempt therefore tried to create `session_screens` again - SQLSTATE 42P07. The database is the
disposable side, so it was dropped and recreated, NOT renamed to match:

```
DROP DATABASE ccpgtest; CREATE DATABASE ccpgtest;
-> 0 tables outside pg_catalog/information_schema
```

The migration was not renamed and will be regenerated again when #2643 lands (ruling 6/8), so this reset
is expected to be needed at least once more.

## Finding 1 - resolution 2, and the reason is structural rather than a measurement

**Chosen: resolution 2. `ReadLiveAsync` always goes to the tunnel; `ReadStored` keeps serving history.**

r12 permits resolution 1 only on a measurement showing the push volume is small. No such measurement was
made, so resolution 1 is not available to this round. But the choice does not rest on that absence, and
it should not - here is the positive argument.

The store could only ever answer a live question while **fact 2 held: the owning Director's tunnel is
CONNECTED at that instant.** A connected tunnel is exactly the condition under which the tunnel could
have answered the question itself. So the live half never bought availability - not once, by
construction. It bought latency, on a connection that was already up. That is an optimisation, and r12
is right that an optimisation which cannot be made sound is dropped rather than weakened.

Resolution 1 would not remove the hazard either, only bound it: a coalesced push gives "the terminal has
not moved in the last X", never "the terminal has not moved". A keystroke can follow this answer.

What changes: `CertifyStored`, `LiveSnapshotBudget` and the reader's dependency on `PushedSessionStore`
go; `ScreenSource.Store` stops being a possible answer to a live read. History reads are untouched -
that is the half the mission was for, and it stands.

**The failing test.** The negative control is rewritten to FORBID the stale serve rather than assert it.
Its strongest form: a stored screen, a connected tunnel, a fresh snapshot, and the byte marks EQUAL -
every one of the three old facts satisfied - and the reader must still return `Source.Tunnel`, the live
rows by content, and exactly one tunnel call. Against today's code that returns `Source.Store`, the
stored rows and zero tunnel calls, so it is red before the fix. The history half is asserted in the same
run so the test cannot pass on a reader that answers nothing.

## Finding 2 - make the claim TRUE: mark and frame from one observation

The comment claims the mark can only understate the frame. It cannot: the buffer increments its total
under the buffer lock and feeds the parser afterwards, so the counter runs AHEAD of the parser and a mark
read from the counter OVERSTATES the frame.

The fix takes the mark from a counter incremented inside the parser lock at the moment the bytes are
parsed, and reads it in the same locked frame that produces the rows. Mark and frame then come from ONE
consistent observation, which is r12's first option; the claim is made true rather than deleted. The mark
still never exceeds the buffer's own total, so row 0's existing bound assertion is unaffected.

**The failing test.** The inspector's rendezvous, made permanent: a subscriber ordered before the session
parser blocks a second write after the counter has advanced and before the parser sees it; the capture
taken at that instant must return the OLD frame with the OLD frame's mark. Today it returns the old frame
with the new total, so it is red before the fix.

## Finding 3 - the key carries the Director

Under resolution 2 the live certification is gone, so the cross-Director certification defect cannot
occur at all - and the rewritten finding-1 test is the artifact that says so, since it asserts a live read
never returns a stored row. The inspector's own repro is installed permanently as a test in its own right.

The residual half is real and independent of certification: the primary key is
`(tenant, session, captured-at)`, so two Directors capturing the same session id in the same millisecond
collide and one row silently swallows the other. `DirectorId` joins the key.

**The failing test.** Two Directors, same session id, same capture time, different rows: both must be
stored and both readable. Today the second push is answered "already stored" and is lost, so it is red
before the fix.

## Finding 4 - the rig asserts content, and the mapping gets an instrument that always runs

This is the finding that invalidated the phase's headline claim: the inspector replaced every screen's
rows with a constant and the whole Gateway unit project stayed green and the rig still printed
ROW 4 PROVEN. Two fixes, because the two failures are different.

1. **The mapping is testable and tested.** `GatewayScreenSink`'s mapping becomes a named function and a
   test asserts the `ScreenPush` it produces is field-identical to the `TurnEndScreen` it was handed,
   rows element by element. The inspector's exact mutation turns this red. It runs in the default gate,
   so it does not depend on anyone standing up a rig.
2. **The rig compares content, quoted on both sides.** The turn's command ends with a unique per-run
   multi-line marker block so the marker is on the FINAL screen rather than scrolled away; the readback
   requires those exact lines, in order, among the stored rows, and requires every non-blank stored row
   to appear in the Director's own terminal buffer read back over `/sessions/{sid}/buffer` - an
   independent path from the parser grid the capture came from. Both sides are printed to the row file.
   A constant substituted anywhere in the push path fails both checks.

Where it stops is stated in the row: the buffer comparison proves the stored rows are made of bytes that
were really on that terminal; it is not a pixel-for-pixel equality of the whole grid, because the grid's
own trailing-trim and the shell's prompt line have no second source to compare against.

## Finding 5 - describe the real loss boundary, and make the loss countable

r12 allows either durability or an honest boundary. This round takes the honest boundary, because a
durable outbox is a new mechanism and a fix round is new writing that would owe its own proofs - and the
report's claim that a miss costs "never a record" is false today and is the thing that must stop being
said.

But an honest boundary that is invisible at runtime is only half an answer, so the drop becomes a named,
countable event: `GatewayStreamClient.PushScreen` logs the drop with the session and the reason and
increments a counter, instead of returning in silence.

**The failing test.** A client with no connection is handed a screen: the dropped-screen counter must
move by exactly one, and must not move for a push that is handed to a live connection. There is no
counter today, so it does not compile before the fix - red in the strongest sense.

## Finding 6 - the bound is made true by repair, and stated honestly

The per-instance lock cannot bound a count across two Gateway processes, and the code itself expects two
to overlap during a deploy swap. The comment's guarantee is withdrawn and replaced by what the code
actually provides: at most 200 per session, transiently up to 200 plus the number of overlapping writers,
and repaired to exactly 200 by the next append **or by the retention sweep** - which is the new part. The
inspector's residual complaint was precisely that an idle session stays over the bound until retention;
the sweep now trims over-cap sessions as well as expired rows.

**The failing test.** Seed 203 rows for one session past the write-time trim, run the sweep, and require
exactly the newest 200 to remain. The sweep only purges by age today, so it is red before the fix.

## Order of work, and where it happens

Findings are taken in r12's order. Everything is committed and pushed to `mission/terminal-rules` as it
lands. Nothing goes to `main` - that is the Architect's act alone, and a second inspection runs after this
round.

The parked gate's verdict is the runner's own and is reported as the runner gives it, including the exit
code; the previous round's exit 0 and the inspector's exit 1 are resolved by running it here rather than
averaging them.
