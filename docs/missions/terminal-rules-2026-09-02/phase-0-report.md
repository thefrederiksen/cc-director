# Terminal Rules, phase 0 - the report

Manager's report on the state of phase 0. Branch `mission/terminal-rules`, worktree
`D:\ReposFred\devthrottle-terminal-rules`. Issue #2644.

**This report was REWRITTEN, not amended.** Its earlier version described a mechanism that no longer
exists - a stored screen serving the live-truth question when three freshness facts held - and an
independent inspection established that mechanism could not do what its name said. Correcting a document
whose opening still teaches the wrong architecture would leave every later reader learning it. The
history of how it changed is in `inspection-01.md` and in `rulings/r12` and `r13`; this file describes
what is there.

**Phase 0 is NOT complete.** Six defects were found by inspection 01, all six are answered here, and a
SECOND independent inspection runs before anything may be called finished. Nothing has landed on `main`
and nothing may - landing is the Architect's act alone.

---

## What phase 0 delivers, in one sentence

**A session's turn-end terminal screen, stored per account for seven days, readable from anywhere -
including while the machine that produced it is offline.**

That is the whole of it, and it was always the half the mission was for. An earlier design also tried to
answer "what is on screen RIGHT NOW?" from that store when it could prove the screen was still current.
It could not prove it, and that half has been removed rather than weakened. See "the decision that
shaped the phase" below.

---

## What was built

**The capture already existed and was not rebuilt.** `TurnReviewLogger` has always fired on the one
trigger that means the screen has stopped moving - a session flipping Working to WaitingForInput - and
has always snapshotted the screen there. The push hangs off that same flip. `Session` gained
`SnapshotLiveScreenWithBufferMark`, which takes the grid, its flags, and a byte mark in ONE locked
observation: the mark is the number of terminal bytes the returned frame REFLECTS, counted inside the
same lock that produces the rows.

**The transport** is `ScreenPush` on the contracts, `PushScreen` on `DirectorHub` with the tenant and
the Director id taken from the connection binding like every other push, and `GatewayScreenSink` on the
Director. It is fire-and-forget, unlike the prompt sink. What that costs is stated exactly below, under
"the loss boundary" - it is not free, and the earlier version of this report said it was.

**The store** is `session_screens`, tenant-scoped through the context's global filter, keyed
(tenant, session, captured-at, director) so a re-sent capture is idempotent, two Directors cannot
collide, and a session's turn-end screens are kept in order rather than one row being overwritten. It
validates a push whole before writing anything, bounds each session at 200 rows, and has its own
seven-day sweep on the tenant-scoped worker seam - a sweep that also repairs a session left over the
cap. It is a SEPARATE store from the turn-push mission's conversation store (#2638) and no file of
theirs was edited.

**The reader** is `GatewayScreenReader`, and it is the one place the Gateway asks what is on a screen.
It answers two questions that are not interchangeable:

- `ReadStored` - "what was on screen at the end of that turn?" History, from the store, with no
  freshness test at all. The owning machine being offline is the point of having stored it.
- `ReadLiveAsync` - "what is on screen right now?", which a keystroke may follow. It asks the OWNING
  DIRECTOR, always. The store is never consulted. A Director that cannot answer is UNREADABLE, returned
  as unreadable and never as a stored screen.

All six tunnel reads named in the brief go through it. A derived enumeration confirms it:
`git grep GetScreenGridAsync` over the branch returns the method's definition, one doc comment, and
exactly one real call, inside the reader.

---

## The decision that shaped the phase

The first design served a stored screen as the LIVE screen whenever three facts held: the byte mark
taken at capture still equalled the session's pushed `TotalBufferBytes`, the owning Director's tunnel
was connected, and that snapshot was recent. Ruling 1 required all three and was right to. Inspection 01
then established that the FIRST of them was not measuring what its name said.

`TotalBufferBytes` reaches the Gateway on the session snapshot, which is refreshed by a ten-second timer
and by some activity transitions - **never by the terminal being written to.** So after a fresh snapshot
at N the real terminal can move on while the Gateway still holds N, is connected, and is well inside any
age budget. All three facts pass, and the reader hands back a screen the terminal has already scrolled
past. Worse, the shipped negative-control test ASSERTED that stale serve rather than forbidding it,
which is why nobody noticed.

**The principle, and it generalises past this feature: a certification may only rest on a signal that is
refreshed by the event it claims to detect.** The byte count claimed "the terminal has not moved since
capture" and is not refreshed when the terminal moves, so it could not establish that - and connection
state and snapshot age cannot repair it, because they answer different questions.

**Why the mechanism was removed rather than fixed.** Making the count current would give a bound, not a
guarantee: a coalesced push says "the terminal has not moved RECENTLY", and a keystroke follows this
answer. And the optimisation was worth nothing in the first place - the store could only ever answer
while the owning Director's tunnel was CONNECTED, which is exactly the condition under which the tunnel
could have answered the question itself. So it never bought availability, not once, by construction; it
bought latency on a connection that was already up. Ruling 13 withdrew the alternative as an option for
this mission and for any later phase tempted to revive it.

**What it cost.** One tunnel round trip per live screen read, which is what every caller paid before
this store existed. Nothing else. The history half - a screen visible from anywhere, surviving the
machine going offline, still there in the morning - is untouched.

---

## The class of defect this mission kept finding

The freshness check passed when nothing was ARRIVING. That turned out to be a class, not an incident:

| where | it passed when |
|---|---|
| the freshness check | no bytes were ARRIVING |
| taking the migration slot "unless you know otherwise" | nobody ANSWERED |
| the voice-turn proof | no voice turn RAN |
| the tenant-scoping proof as first written | account B could read NOTHING at all |
| the pull counter, had it been kept per caller | a later caller made round trips it could not see |
| the collation guard | a collation is MISSING rather than added |
| the end-to-end row 4 proof | the push path replaced every screen with a constant |
| the migration-slot sweep, three times | a directory was missed, a context was confused, a branch had merged |
| the rig's own verdict | another tool's summary line moved |

The repair is always the same and is now applied throughout: state the check as a specific PRESENCE,
name the artifact that must exist, and treat an empty result as a broken instrument rather than a clean
run.

---

## The proofs

The row-by-row plan, with each row's pass condition, is `phase-0-proofs.md`. Row 7 is WITHDRAWN because
the fix to finding 1 removed the behaviour it asserted, and it is not re-scoped.

**Rows 0 to 6 pass, and three of the fix round's six green runs are still PENDING.** The mission was
stood down from the machine-wide test lock mid-round while a live production outage was fixed, so some
runs it owes have not been taken. `fix-round/report.md` carries the table of which greens are in hand
and which are pending, and `fix-round/red-runs/` carries every red run. Nothing here quotes a run that
was not made.

### Row 4 - the one the store exists for, proven on a REAL Director

`scripts\terminal-rules-screen-proof.ps1` stands up a throwaway Gateway and a throwaway Director, has
the Director really end a turn on three lines the run authored, reads the Director's own terminal buffer
while it is up, really stops it, and only then reads the screen back. It is the ONLY row that exercises
a real `TurnReviewLogger` capture, the real `GatewayScreenSink`, the real `PushScreen` hub method, the
real store and a real MIGRATED database. Full transcript in `fix-round/red-runs/rig-run.md`; in its own
words:

```
session activity states observed, in order: Working -> WaitingForInput
STEP 2 PASS: the Gateway logged storing a screen pushed by the rig Director
STEP 2b PASS, machine up: [GatewayScreenReader] sid=5552a4de-...: pulled the screen over the TUNNEL
STEP 3 PASS, machine stopped: the route refused before the read: {"error":"session not found on any director"}
read-back verdict rests on ...\stored-row.txt, which names session 5552a4de-... and all three of this run's markers
ROW 4 PROVEN.

  stored   | TR_SCREEN_PROOF_20260902-121724_ALPHA   -> stored row 35
  terminal | TR_SCREEN_PROOF_20260902-121724_ALPHA
```

**And it is now capable of failing.** With the inspection's mutation applied - every pushed screen's
rows replaced by one constant - the same script fails the row and says why:

```
the stored screen does NOT contain the line this run printed: 'TR_SCREEN_PROOF_20260902-121354_ALPHA'.
The rows that were stored are: MANGLED CONSTANT
ROW 4 FAILED
```

Against the shipped code that same mutation left the row printing PROVEN.

### The other rows

- **Row 0 - the real capture fires and reaches its sink.** A real session, real buffer, real flip. Stops
  at the sink CONTRACT; says so.
- **Row 1 - a screen survives the push and comes back whole.** Field by field, plus both key
  properties: the same Director's re-send is one row, and two Directors at the same instant are two.
- **Row 2 - retention deletes and does not delete everything.** The sweep RUNS, returns 1 then 0; the
  six-day row survives as the control. It also repairs a session left over the cap.
- **Row 3 - tenant scoping.** Account A reads its own, account B reads its OWN, and only then is B
  refused A's - so a broken account B cannot pass. This boundary was attacked by the inspection and held.
- **Row 5 - the live question and the history question, in one run.** The live read is answered by the
  tunnel, by content; the tunnel then stops answering and the live read is UNREADABLE with a reason;
  `ReadStored` still returns the screen throughout.
- **Row 6 - the store never answers the live question, even when every old freshness fact holds.** A
  stored screen, a connected Director, a one-second-old snapshot, and equal byte marks - and the reader
  still returns the tunnel's rows with exactly one tunnel call.
- **Row 7 - WITHDRAWN.** See above.

### The limit that still travels with rows 1, 2, 3, 5 and 6

**Proven from the store inwards: they seed the store BY HAND.** If the push were wired to nothing at
all, all five would still pass. Row 0 narrows that and does not close it. **Row 4 is what closes it**,
and it is the only row that does.

The OTHER label these results used to carry - *proven against the mapped model, not the migrated schema* -
**is gone.** The migration slot freed, the migration was regenerated, `has-pending-model-changes`
reports no changes on both providers, and the throwaway model-built database was deleted. Every row now
runs on a real `GatewayDatabase` over the real migration set.

---

## What is NOT guaranteed, said plainly

**The loss boundary.** A screen is pushed fire-and-forget with no outbox, no sequence and no reconnect
replay. If the tunnel is absent or the send fails, that turn's screen has NO row in the Gateway's
history and never will - the next turn sends the NEXT turn's screen. The Director's own local
turn-review file still holds it, and nothing replays that file into the store. If the machine then goes
offline, the history read has no fallback for that turn at all.

The earlier version of this report said a miss cost "a round trip, never a record". That was wrong and
is withdrawn. The hole is accepted for now and named: a durable outbox is a mechanism that would owe its
own proofs. What WAS fixed is that the loss is no longer silent - every drop is logged with its session,
capture time and reason, and counted, with a delivered counter beside it because "nothing was dropped"
is satisfied by a Director that never pushed anything. Before that, a Director dropping every screen it
captured looked exactly like one that had captured none.

**The per-session bound is approximate between writers.** After any write that is not racing another
Gateway process, a session holds at most 200 screens. While two Gateway processes overlap - which
happens during a deploy swap, and which the store's own duplicate-retry names as a real case - each can
insert a row, count only its own view, and select the same oldest row to delete, so a session can
transiently hold up to 200 plus the number of overlapping writers. There is no cross-process lock and
the code does not pretend there is one. The excess is repaired by the next ordinary append, and - for a
session that has gone idle and gets no next append - by the retention sweep.

**Row 4's content comparison is a substring match**, not a line-for-line equality of the whole grid.
Grid rows are trailing-trimmed and the raw buffer keeps its own line breaks, so the two shapes do not
admit a stricter comparison. It is enough to defeat every substitution the inspection demonstrated.

**Row 7's claim no longer exists**, so nothing in this phase says anything about what a voice turn costs.

---

## The gates

**The default local gate, run here:**

```
scripts\test-local.ps1 : nine projects, every TRX outcome Completed, 4,556 tests, 0 failed, exit 0
```

That resolves a discrepancy the Architect asked to be resolved rather than averaged. The previous round
reported exit 0; inspection 01 observed exit 1 because
`InstallAsync_FailedVenvRebuild_LeavesNoManagedShim` failed after about thirty seconds and then passed
alone on retry. Run here, that suite is fully green (456 passed) and the runner exits 0. Both
observations were honest; the case is intermittent.

**The parked gate is RED, because one of its two suites DID NOT RUN.** `Core.Tests` passed - 4,374
passed, 8 skipped, 0 failed. `Gateway.Tests` executed zero tests: it waited thirty minutes in the
machine-wide lock queue and was then killed by the Architect, because it was queued ahead of the session
fixing a live production outage. A suite that did not run is not a suite that passed, and the runner
says so itself by requiring a total at or above the baseline. `Gateway.Tests` is therefore the one
outstanding item on this work: it holds the only live-Postgres exercise of the screen store's key and
the endpoint harness that changed with finding 1. Both projects build; their behaviour is unproven.
Full detail in `fix-round/parked-gate.md`.

The shared throwaway Postgres database `ccpgtest` was DROPPED AND RECREATED before that run. This is a
standing rule for the rest of the mission: every time the provisional migration id changes, every
database it has touched is reset, because a stale history row for an id that no longer exists fails in a
way that looks exactly like a defect in this code and is not. That is not hypothetical - it is what made
the parked gate red for inspection 01.

---

## Defects found in this mission's own work

Found by the mission itself:

- **The hub-method fixture called a complete Gateway incomplete.** A hand-kept list never got
  `PushScreen`, while its reflection-derived sibling kept passing. Fixed by DERIVING the fixture from
  the hub, not by adding the entry.
- **The entity without its migration broke every Gateway database open.** EF raises
  `PendingModelChangesWarning` on `Migrate()`. The check scheduled as a one-time gate turned out to run
  continuously on every database open, which is strictly better. **It must never be suppressed.**
- **`session_screens.SessionId` had no byte-ordinal collation**, and the check that polices collations
  compared the catalog against a hand-kept ALLOW-list - loud when one is added, blind when one is
  missing. It was inverted into an exception list derived from the model.

Found by the independent inspection, and answered in this round:

1. A recent pushed count is not a current count, so the reader knowingly served a different live screen.
2. The capture paired an old parser frame with the new byte total, and its comment claimed the opposite.
3. Live certification did not bind the stored row to the routed Director, and the key could not have
   made that comparison meaningful.
4. The end-to-end proof accepted a transport that replaced every screen with arbitrary text.
5. A reconnect-window failure loses that turn permanently, and the report denied it.
6. The 200-row cap is not exact across overlapping processes, and the comment asserted it was.

Found while ANSWERING the inspection, in the instruments rather than the product:

- **The rig's failure path printed nothing.** A failing read-back aborted the script with its own
  diagnosis inside an uncaptured stream. A proof whose failure path prints nothing is the same defect as
  a proof that cannot fail.
- **The rig's verdict was parsed out of another tool's console wording**, and a run whose comparison had
  genuinely passed still failed. It now rests on an artifact the run produced.
- **The migration-slot sweep was wrong a third time**, reporting the very branch whose merge freed the
  slot as still holding it, because a three-dot diff compares against a merge base that a squash merge
  does not move. Corrected in `rulings/r2` to ask whether the migration is PRESENT ON MAIN.

---

## Process notes worth keeping

- **Two agents in one worktree.** A Worker was spawned into the Manager's own checkout, so they shared a
  working tree and an index and an uncommitted fix was swept into the other's commit. One tree per
  concurrent activity.
- **A test result read from the shared temp directory belonged to another mission's run**, identified by
  the worktree path in its stack trace. Read your own run's directory, not the newest one.
- **The 33-commit rebase onto the new main was abandoned for a merge.** Replaying every mission commit
  re-raised the same conflicts repeatedly, and on one step a truncated console view hid a third conflict
  whose markers were then committed into a file. A merge resolves the same content once. The branch is
  squash-merged by the Architect either way.

---

## The one line for the owner

The PARKED Gateway test suite was RED on `main` from 2026-08-05 through the v2.0.0 and v2.0.1 release
tags, on a stale hand-kept list in its own collation check - the file records the list going stale twice
before, and this mission found it stale a third time. That is not this mission's to fix and is raised
once, here, because a suite the release gate depends on was red across two shipped releases.

Also recorded and deliberately not fixed here: the inverted collation check found THIRTEEN string key
columns across five other features' tables with no byte-ordinal collation, listed as inherited debt.

---

## What is owed before phase 0 can be called FINISHED

1. **A second independent inspection**, from a different agent family, of this fix round. A fix round is
   new writing and carries a new writer's risk.
2. Nothing else on ruling 6's list: the corrected sweep was re-run and reported two holders (this branch
   and pull request 2379); the branch was brought onto the new main; both provisional migrations were
   deleted and regenerated; `has-pending-model-changes` reports no changes on BOTH providers; every
   surviving proof row was re-run against the regenerated migration; and the throwaway model-built
   database was deleted.
3. **Pull request 2379 still holds three `GatewayDbContext` migrations from August.** It does not block
   this mission - the rebase and the rows are done - but it is a future collision, and it becomes one
   plain question for the owner if it is ever the only thing standing between this mission and a merge.

Phase 1 does not start on the strength of this.
