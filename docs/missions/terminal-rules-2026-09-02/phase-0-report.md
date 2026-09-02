# Terminal Rules, phase 0 - the report

Manager's report on the state of phase 0. Branch `mission/terminal-rules`, worktree
`D:\ReposFred\devthrottle-terminal-rules`. Issue #2644.

**All seven acceptance rows are proven and the local gate is green.** The work is complete on the
branch and it is NOT landed - and there is a real difference between those two, kept in view here
rather than collapsed. The migration this branch carries is provisional and will be deleted and
regenerated when pull request 2643 lands, and every row must then be re-run against it. Until that is
discharged, "the rows are proven" is a statement about a shape that is about to be replaced.

This report says what is built, what is proven, what each proof does NOT establish, and what is owed.
It is written to be read by somebody who was not here.

---

## What was built

**The capture already existed and was not rebuilt.** `TurnReviewLogger` has always fired on the one
trigger that means the screen has stopped moving - a session flipping Working to WaitingForInput -
and has always snapshotted the screen there. The push hangs off that same flip. `Session` gained
`SnapshotLiveScreenWithBufferMark`, which takes the grid, its flags, and the terminal's
total-bytes-written mark in one operation; the counter is read FIRST, before the parser lock, because
the parser lags the buffer and an understated mark makes the Gateway refuse a screen it could have
served, while an overstated one would let it certify a screen the terminal had already moved past.

**The transport** is `ScreenPush` on the contracts, `PushScreen` on `DirectorHub` with the tenant and
the Director id taken from the connection binding like every other push, and `GatewayScreenSink` on
the Director. It is fire-and-forget, unlike the prompt sink, and that difference is deliberate: a
missed screen costs a round trip and never a record, because the local turn review still holds it and
the next turn end sends a fresh one.

**The store** is `session_screens`, tenant-scoped through the context's global filter, keyed
(tenant, session, captured-at) so a re-sent capture is idempotent and a session's turn-end screens are
kept in order rather than one row being overwritten. It validates a push whole before writing
anything, bounds each session at 200 rows trimmed inside the push transaction, and has its own
seven-day sweep on the tenant-scoped worker seam. It is a SEPARATE store from the turn-push mission's
conversation store (#2638) and no file of theirs was edited.

**The reader** is `GatewayScreenReader`, and it is the one place the Gateway asks what is on a screen.
It answers two questions that are not interchangeable:

- `ReadStored` - "what was on screen at the end of that turn?" History. No freshness test at all; the
  owning machine being offline is the point of having stored it.
- `ReadLiveAsync` - "what is on screen right now?", which a keystroke may follow. The store may answer
  only when all three hold: the byte mark equals the currently pushed count, the owning Director's
  tunnel is CONNECTED at this instant, and that snapshot is younger than a named twenty-second budget.
  Any one unestablished goes to the tunnel; a tunnel that cannot answer is UNREADABLE, returned as
  unreadable and never as a stored screen.

All six tunnel reads named in the brief now go through it. A derived enumeration confirms it:
`git grep GetScreenGridAsync` over the branch returns the method's definition, one doc comment, and
exactly one real call, inside the reader.

---

## The single most important decision, and why it changed

The first plan served a stored screen whenever the pushed `TotalBufferBytes` still equalled the mark
taken at capture. The Architect refused it (ruling 1) and was right. The pushed byte count is the LAST
VALUE THE DIRECTOR SENT, not a live reading, so when the push stream freezes the mark and the current
value are equal BECAUSE NOTHING IS ARRIVING. That check passes when nothing is happening - and it does
so in exactly the case the feature was built for, where offline becomes indistinguishable from quiet
and a silent session starts getting keystrokes pressed at it.

That defect turned out to be a class, not an incident, and it was hit five more times in one run:

| where | it passed when |
|---|---|
| the freshness check | no bytes were ARRIVING |
| taking the migration slot "unless you know otherwise" | nobody ANSWERED |
| the voice-turn proof | no voice turn RAN |
| the tenant-scoping proof as first written | account B could read NOTHING at all |
| the pull counter, had it been kept per caller | a later caller made round trips it could not see |
| the collation guard | a collation is MISSING rather than added |

The repair is always the same and it is now applied throughout: state the check as a specific
PRESENCE, name the artifact that must exist, and treat an empty result as a broken instrument rather
than a clean run.

---

## The proofs - all seven, and how each was measured

Seven rows plus a partial numbered 0. Every result carries its label.

### Row 4 - the one the store exists for, proven on a REAL Director

`scripts/terminal-rules-screen-proof.ps1` stands up a throwaway Gateway and a throwaway Director, has
the Director really end a turn, really stops it, and only then reads the screen back. In its own words:

```
session activity states observed, in order: Working -> WaitingForInput
STEP 2 PASS: the Gateway logged storing a screen pushed by the rig Director
STEP 2b, machine up:  [GatewayScreenReader] sid=885b9f59...: served the STORED screen captured
    2026-09-02T13:44:30.248Z (tunnel connected, snapshot 1.2s old, terminal unchanged at
    2749589 bytes) - no tunnel read
STEP 3, machine stopped: the route refused before the read: {"error":"session not found on any director"}
STEP 4, read back: session=885b9f59 director=b301be60 agent=RawCli state=WaitingForInput
    bufferBytes=2749589 rows=40 hasGrid=True cursor=(39,72) visible=True alternateScreen=False
      | C:/Windows/System32/WinMetadata/Windows.Security.winmd
      | C:/Windows/System32/winrm/0409/winrm.ini
      ... the terminal's actual content, quoted line by line
```

This is the ONLY row that exercises a real `TurnReviewLogger` capture, the real `GatewayScreenSink`,
the real `PushScreen` hub method, the real store, and a real MIGRATED database. It is also the row
that shows the feature paying off: **"served the STORED screen ... no tunnel read"** on a live
Director. Teardown ran inside the row and left nothing - no rig root, no scheduled task, no process,
verified afterwards.

### Rows proven in process, against the mapped model

- **Row 0 - the real capture fires and reaches its sink.** A real session, real buffer, real flip.
  Stops at the sink CONTRACT; says so.
- **Row 1 - a screen survives the push and comes back whole.** Field by field.
- **Row 2 - retention deletes and does not delete everything.** The sweep RUNS, returns 1 then 0; the
  six-day row survives as the control.
- **Row 3 - tenant scoping.** Account A reads its own, account B reads its OWN, and only then is B
  refused A's - so a broken account B cannot pass.
- **Row 5 - a dropped tunnel stops the store answering the LIVE question but not the history one.**
  Three states in one run, with the reason string naming the deciding fact.
- **Row 6 - a frozen push stream does not certify a stale screen.** The budget asserted one second
  inside and one second outside by an injected clock; one byte of movement sends the read to the tunnel.
- **Row 7 - a voice turn completes AND costs no tunnel screen read.** A conjunction: narration audio
  exists AND the counter is unchanged across that same turn, with two controls - an empty store pulls
  once, and a terminal moved by one byte pulls once. The model and speech legs are STUBBED; that is
  stated in the class and is not a claim about a provider.

### Every instrument was run against known-bad input

- Comment out the capture: two of row 0's three tests go red.
- Disable freshness facts two and three - byte equality alone, the defect ruling 1 named - and exactly
  two of the six reader tests go red; the other four correctly stay green.
- Mutate six store assertions: 14 of the 17 store tests go red.
- Make the turn unable to complete: ALL THREE row 7 tests go red, the first on its narration
  assertion - the hole a counter-only proof would have sailed through.
- Remove the collation from the model and regenerate: the collation check fails naming
  `session_screens.SessionId` exactly.
- The pull counter is shown to MOVE; the sweep answers 1 and then 0.

## The gate

```
scripts	est-local.ps1 : nine projects, 4485 passed, 0 failed, exit 0
```

The two PARKED suites: `Core.Tests` ran green in the parked pass (4374 passed, 0 failed).
`Gateway.Tests` had exactly ONE failure on this branch, and it was the stale collation allow-list
described below - not this mission's code. Its replacement runs 6 of 6 green, including the new
Postgres idempotency proof. The full parked Gateway suite has NOT been re-run end to end since,
because it takes a machine-wide lock currently held by two other worktrees' live runs, and that is
stated rather than glossed.

## The two limits that travel with every result above

1. **Proven against the mapped model, not the migrated schema.** `EnsureCreated` builds from the
   model; the real Gateway builds from the migration file. Different generators, and only the second
   ships.
2. **Proven from the store inwards, with the push path unexercised.** Rows 1, 2, 3, 5 and 6 all seed
   the store BY HAND. If the push were wired to nothing at all, all five would still pass. Row 0
   narrows this and does not close it. **Row 4 is what closes this**, and it is the only row that does: it drives the real capture, the real
   sink, the real hub method and the real store on a real migrated database. Without it, the rest say
   only that the store and the reader behave correctly WHEN HANDED a screen.

---

## Defects found in this mission's own work, and how each surfaced

- **The hub-method fixture called a complete Gateway incomplete.** A hand-kept list in the capability
  handshake test never got `PushScreen`, while its reflection-derived sibling kept passing. Fixed by
  DERIVING the fixture from the hub, not by adding the entry - adding the entry fixes today and leaves
  the mechanism for the next method.
- **The entity without its migration broke every Gateway database open.** 897 of 898 local-gate
  failures, in the tests and in a running Gateway alike, because EF raises `PendingModelChangesWarning`
  on `Migrate()`. The check the Architect scheduled as a one-time gate for later turned out to be
  running continuously on every database open, which is strictly better. **It must never be
  suppressed.**
- **`session_screens.SessionId` had no byte-ordinal collation.** Every sibling of that exact shape has
  one; without it Postgres and SQLite disagree on uniqueness for the key the store's idempotency rests
  on. The suite that polices collations compares the catalog against a hand-kept list, so it fires when
  one is ADDED and is blind when one is MISSING - a blind spot pointing exactly where its own success
  condition is met.

---

## Process notes worth keeping

- **Two agents in one worktree.** The Worker was spawned into the Manager's own checkout, so they
  shared a working tree and an index, and an uncommitted fix was swept into the other's commit.
  Nothing was lost. The rule is one tree per concurrent activity; a Worker gets a worktree cut from
  the mission branch and pushes back to it.
- **A test result read from the shared temp directory belonged to another mission's run**, identified
  by the worktree path in its stack trace. The Gateway suite serialises machine-wide, so more than one
  mission's results sit side by side there. Read your own run's directory, not the newest one.

---

## The one line for the owner

The PARKED Gateway test suite was RED on `main` from 2026-08-05 through the v2.0.0 and v2.0.1 release
tags, on a stale hand-kept list in its own collation check - the file records the list going stale
twice before, and this mission found it stale a third time. That is not this mission's to fix and is
raised once, here, because a suite the release gate depends on was red across two shipped releases.

## What is owed before phase 0 can be called FINISHED

1. Re-run the CORRECTED migration-slot sweep and say what it returned. Not "2643 merged, therefore
   free".
2. Rebase onto main, DELETE this branch's provisional migration, regenerate on the new snapshot, and
   run `has-pending-model-changes` until it reports no pending changes on BOTH providers.
3. Re-run the full local gate green, and re-run EVERY proof row - all seven are proven against a
   migration that will have been replaced.
4. Delete the throwaway `ScreenStoreTestDb` rather than leaving it as an easier path that outlives its
   reason.

5. Re-run the full parked Gateway suite end to end. It could not be re-run here: it takes a
   machine-wide lock held throughout by two other worktrees' live runs. Its only failure on this
   branch was the stale collation check, whose replacement runs 6 of 6 green - but that is a
   statement about one class, not about the suite.

Phase 1 does not start on the strength of this. A fresh Manager is seated for it.
