# Phase 0 - the proofs, each restated so that nothing happening cannot pass it

Manager, Terminal Rules mission. Written 2026-09-02, after ruling 3.

Ruling 3 named a class: **a pass condition satisfied by nothing happening.** Every row below has
been put to its test - *if the thing I am measuring never ran at all, does my check still pass?* -
and rewritten where the answer was yes. The rewrite is always the same: name the artifact that must
EXIST, and make an empty result a broken instrument rather than a clean run.

Seven rows. Five are provable now and two are blocked on the migration slot; the blocked two say so
rather than being quietly dropped.

**Phase 0 is NOT done until all seven are proven, and no later summary may say otherwise - not even
"done pending the migration" (ruling 4).**

---

## What is blocked, and why, established by reading rather than assumed

`GatewayDatabase` creates its schema with `Database.Migrate()` on both providers
(`GatewayDatabase.cs:346` for Postgres, `:414` for SQLite). There is no `EnsureCreated` path in it.
So `session_screens` does not exist in ANY real Gateway until the migration lands, and the migration
slot is held (ruling 2).

That splits the proofs in two, and the split is not a matter of convenience:

- Rows 1, 2, 3, 5 and 6 are provable NOW, against a throwaway SQLite database built with
  `EnsureCreated` from the same mapped model the store's own statements are generated from. That
  instrument is not invented here - `StatsConcurrencyTestDb.cs` established it for exactly this
  situation, and its class comment says why.
- Row 4 needs a real Gateway with a real Director attached. It was blocked while no Gateway on this
  branch could open a database at all; ruling 6 removed that, and it is now driven by
  `scripts	erminal-rules-screen-proof.ps1` against a throwaway Gateway and Director.
- Row 7 was planned as a live-rig run and is proven IN PROCESS instead, with the model and speech legs
  stubbed. The difference is stated in the row itself rather than glossed.

### The limit on the five, stated in the same breath as the instrument (ruling 4)

`EnsureCreated` builds the tables from the mapped MODEL. The real Gateway builds them from the
MIGRATION FILE. Those are two different generators, and only the second one ships. So the five rows
below are

> **proven against the mapped model, not the migrated schema, and proven from the store inwards
> with the push path unexercised.**

BOTH halves, always together (ruling 5). The first half is the schema: they prove the store's LOGIC
and NOTHING about the shape the migration will produce.

The second half is the one that is easier to miss, and it follows from something row 4 already says.
Rows 1, 2, 3, 5 and 6 all seed the store BY HAND. Not one of them drives
`TurnReviewLogger` to `GatewayScreenSink` to the hub to the store - so **if the push were wired to
nothing at all, all five would still pass.** Row 4 is the only row that covers that path, and row 4
is blocked.

So nobody may say "the store works" on the strength of the five. What the five say is: **the store
and the reader behave correctly WHEN HANDED a screen.**

Row 0 below takes the one cheap seam that exists and says exactly where it stops. It does not close
the gap; it narrows it, and naming what is still open beats a contrived test that hides it.

### Three things are owed the moment the slot frees

1. **Re-run the ruling 2 sweep and say what it returned.** Not "2643 merged, therefore the slot is
   free" - that is inferring a fleet-wide fact from one workstream again.
2. **Assert the migration and the model agree**, with a pending-model-changes check. If they
   disagree, the five rows above were proven against a shape that does not exist, and they are void
   rather than merely stale.
3. **Re-run all seven rows against a MIGRATED database**, then delete the throwaway instrument.

And: **phase 1 does not start on the strength of the five.** It would be built on a schema the
pending-model-changes check has not yet confirmed.

---

## Row 0 - the real capture fires and reaches the sink (a partial, and it says where it stops)

**Claim.** The real `TurnReviewLogger` really does capture the screen on the real Working ->
WaitingForInput flip, and really does hand it to its sink with the grid and the byte mark taken
together.

This is the cheap in-process seam ruling 5 asked for. `TurnReviewLogger` takes an
`ITurnEndScreenSink`, and `TurnReviewLogTests.cs` already builds a real `Session` and flips its
activity state, so no Gateway and no hub are needed to drive the Director half of the capture.

**Pass condition, all three:**
1. A real `Session` with a real terminal parser is written to, then flipped to `WaitingForInput`.
2. The sink receives exactly ONE `TurnEndScreen`, whose `Rows` are the rows that were on that
   terminal, content asserted - not merely a non-empty capture.
3. Its `BufferBytes` equals the session buffer's own `TotalBytesWritten`, and `HasGrid` agrees with
   whether there were rows.

**WHERE THIS STOPS, stated so it is not stretched into a bigger claim.** It covers the flip, the
capture, and the sink CONTRACT. It does NOT cover `GatewayScreenSink.Send`, the
`GatewayStreamClient.PushScreen` invoke, the `DirectorHub.PushScreen` handler, or the store write.
Those four links remain unexercised until row 4 runs against a real Gateway.

Status: **provable now.**

## Row 1 - a screen survives the push and comes back whole

**Claim.** A screen pushed by a Director is stored and read back with every field intact.

*Nothing-happened test:* would a store that was never written pass? No - `ReadLatest` answers null
and the assertion on the rows fails. But a test asserting only "not null" WOULD pass on a screen
that arrived mangled, so:

**Pass condition.** `ReadLatest` returns a screen whose rows are byte-identical to the pushed rows,
whose `CursorRow`, `CursorCol`, `CursorVisible`, `IsAlternateScreen`, `HasGrid` and `BufferBytes`
each equal what was sent, and whose `CapturedAtUtc` equals the captured instant to the millisecond.
Field by field, not by count.

Status: **provable now.**

## Row 2 - retention deletes, and does not delete everything

**Claim.** A screen older than seven days is gone after the retention job RUNS. Proven by running
it, never by reading it.

*Nothing-happened test:* "the old row is gone" passes on a store that was never populated, and on a
sweep that deleted the table. Both are closed below.

**Pass condition, all four:**
1. Before: the store holds exactly two rows - one received eight days ago, one six days ago - and
   both are read back.
2. `SessionScreenSweep.SweepAsync` is invoked and **returns 1**. The return count is the artifact the
   thing under test produced; a sweep that never ran returns 0 and fails here.
3. After: the six-day row is still readable, with its content intact. This is the control. A sweep
   that deleted everything fails this line.
4. After: the eight-day row is gone.

Status: **provable now.**

## Row 3 - tenant scoping, with account B producing a NAMED refusal and a successful read of its own

**Claim.** Only the owning account can read a screen.

*Nothing-happened test:* "account B could not read account A's screen" is absence-shaped by
construction, and a misconfigured account B that can read NOTHING passes it. This is the row ruling 3
called out to look at hardest.

**Pass condition, all four, in ONE run:**
1. Account A stores screen A; account B stores its OWN screen B, for a different session.
2. Account A reads session A and gets screen A, content asserted. (A's read works.)
3. Account B reads session B and gets screen B, content asserted. **This is the line that makes the
   proof mean something** - it establishes that account B's read path is alive, so its failure below
   is about the partition and not about a broken account.
4. Account B reads session A - account A's session id, spelled correctly - and gets NOTHING, in the
   same run where line 3 succeeded.

Status: **provable now.**

## Row 4 - a screen captured on one machine, read back while that machine is OFFLINE

**Claim.** The history read works when the owning machine is gone. This is the acceptance row the
whole store exists for.

*Nothing-happened test:* a read that returns a screen cannot be satisfied by nothing happening,
provided the content is asserted and the machine's absence is positively established rather than
assumed from having issued a stop.

This row also carries the link no in-process test can cover: that the REAL `TurnReviewLogger` flip
actually reaches the real `GatewayScreenSink` and the real hub. Every other row drives the store and
the reader directly, so every other row can pass with the Director half never having run.

**Pass condition, all three:**
1. A real Director, running a real session, ends a real turn - and the Director's log records the
   capture leaving `TurnReviewLogger` while the Gateway's log records `SessionScreenStore` storing
   it, with a capture time inside that turn's window. Three artifacts, one moment.
2. The Director is then stopped, and its absence is read POSITIVELY off the Gateway: the connection
   registry reports the Director not connected. Not "we ran the shutdown command".
3. With that established, the Gateway's question-A read answers screen A with the rows that were on
   that terminal, quoted against the row printed out of the database.

Status: **UNBLOCKED and driven by `scripts	erminal-rules-screen-proof.ps1`.** Ruling 6 removed the
blocker by allowing the migration to be held locally, so a Gateway built from this branch now migrates
cleanly. The script stands up a throwaway Gateway and a throwaway Director under ruling 8's constraints,
with teardown inside the run.

## Row 5 - the same request as a question-B read is refused, and says why

**Claim.** The live-truth question does not get served a stored screen from a machine that is gone.

*Nothing-happened test:* "`ReadLiveAsync` returned Unreadable" passes when the store is empty, when
the session does not exist, and when the whole fixture is broken. Closed by asserting the conjunction
below.

**Pass condition, all three, in ONE run:**
1. With the Director CONNECTED and its snapshot fresh, `ReadLiveAsync` returns `Source.Store` and
   the stored rows. The store is populated, the reader works, and the certification passes. Positive.
2. The Director's connection is then dropped, and nothing else changes - no bytes written, no time
   skipped.
3. `ReadLiveAsync` now returns `Source.Unreadable` with the reason string naming the deciding fact
   ("owning Director's tunnel is not connected"), while `ReadStored` STILL returns the screen. Both
   halves asserted: the live answer changed, the history answer did not.

Status: **provable now** (the reader's three facts are driven directly; the live-rig version of the
same flip belongs with row 4).

## Row 6 - a frozen push stream does not certify a stale screen

**Claim.** The freshness rule does not fail open when the push stream stops. This is the negative
control for the whole slice - ruling 1 says the slice is not done without it.

*Nothing-happened test:* "the caller did not receive the pre-freeze screen" passes when the caller
received nothing at all, and when it was never called. Closed by naming what it DID receive.

**Pass condition, all four:**
1. The store holds a screen with byte mark N. The Director is connected, its snapshot is fresh and
   reports N. `ReadLiveAsync` returns `Source.Store` and the stored rows. Positive - the certification
   is live.
2. The push stream is frozen: the snapshot stops being refreshed. The pushed byte count therefore
   STAYS at N, which is exactly the condition that would make a byte-equality-only check pass.
3. Time advances past the freshness budget. `ReadLiveAsync` now returns either `Source.Tunnel` with
   the CURRENT rows, or `Source.Unreadable` with a reason naming the stale snapshot - and the test
   asserts which, by name. It never returns the stored screen.

   **HOW time advances, settled before writing it (ruling 5): by INJECTING THE CLOCK.** Both
   `GatewayScreenReader` and `PushedSessionStore` already take a `Func<DateTime>` seam, so the test
   moves its own clock forward and the suite pays nothing. NOT a real twenty-second sleep, and NOT
   by editing `LiveSnapshotBudget`, which is the constant under test - a test that moves the rule to
   make itself pass has stopped testing the rule.

   **And the boundary is asserted from BOTH sides**, or the row passes on a rule that always
   refuses: at one second INSIDE the budget the same call still returns `Source.Store`, and at one
   second outside it does not. One assertion each, in the same test.
4. Separately, with the snapshot fresh but the byte count moved to N+1, `ReadLiveAsync` again does
   not return the stored screen, and the reason names the moved terminal.

Status: **provable now.**

## Row 7 - a voice turn completes with no tunnel screen read

**Claim.** A voice turn costs zero tunnel screen pulls.

*Nothing-happened test:* the counter also fails to move when the turn crashed on its first line, was
never triggered, or produced nothing. Ruling 3. The pass condition is therefore a conjunction of two
positive artifacts.

**Pass condition, all four - and it is met:**
1. **The turn COMPLETED**, evidenced by what it PRODUCED: narration audio exists for that session
   afterwards, and the model leg was actually reached. Never "no error appeared".
2. **AND** the tunnel pull counter, read immediately before and immediately after THAT turn, differs
   by zero.
3. **AND the known-bad control**: the same turn with the store EMPTY moves the same counter by one,
   so the zero is a number shown to be capable of rising.
4. **AND a second control about the RULE rather than the instrument**: the same turn with a stored
   screen whose terminal has moved by ONE BYTE also pulls once. A stored screen saves the round trip
   only while the Gateway can still prove it describes that terminal.

**The conjunction was measured, not assumed.** With the model leg throwing so that no turn can
complete, ALL THREE tests fail - the first on its narration assertion, which is exactly the hole a
counter-only proof would have sailed through.

**HOW IT IS PROVEN, AND HOW THAT DIFFERS FROM THE PLAN.** This row was planned as a live-rig run and is
instead proven IN PROCESS, through the real `WingmanVoiceService`, with the model leg and the speech
leg stubbed. That is a real difference and it is stated rather than glossed:

- What IS exercised: the real voice service, the real `GatewayScreenReader`, the real store, the real
  freshness rule, and the real `SessionVerbClient` whose tunnel sends are what the counter counts.
- What is NOT: a real provider, real speech, and a real Director. This is not a claim about any
  provider, and the narration text is a fixture rather than a model's answer.

The alternative was to put the owner's provider key into a throwaway Gateway and spend on real calls
to prove a claim that is about the SCREEN READ rather than about the model. That trade was not worth
it, and the limit is written down instead of being bought.

Status: **proven, in process, with the model and speech legs stubbed** - and against the mapped model
rather than the migrated schema, like the rest.

---

## The instrument checks, run against known-bad input

Ruling 3's class applies to the instruments too. Each of these is run against a state where it MUST
fire, so that a later pass means something:

- The pull counter is shown to increment - row 7's control run.
- The retention sweep is shown to return 0 when nothing is expired, and 1 when one row is - so its
  return value is known to track reality rather than being a constant.
- `Validate` is run against each malformed push and asserted to name what was wrong, so a later
  "valid push accepted" is not a check that never fires.
- The tenant filter is shown to ANSWER for the owning account (row 3, line 3) before it is shown to
  refuse for the other.
