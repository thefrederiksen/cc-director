# Phase 0 - the proofs, each restated so that nothing happening cannot pass it

Manager, Terminal Rules mission. Written 2026-09-02 after ruling 3; revised by the fix round that
answers `inspection-01.md`, under rulings 12 and 13.

Ruling 3 named a class: **a pass condition satisfied by nothing happening.** Every row below has been
put to its test - *if the thing I am measuring never ran at all, does my check still pass?* - and
rewritten where the answer was yes. The rewrite is always the same: name the artifact that must EXIST,
and make an empty result a broken instrument rather than a clean run.

**Phase 0 is NOT done until every surviving row is proven and a second independent inspection has run,
and no summary may say otherwise.**

---

## What changed in the fix round, and why the row list is shorter

Inspection 01 found six defects. Answering finding 1 removed a mechanism that three of these rows were
written to measure, so the rows changed with it (ruling 13):

- **Row 7 is WITHDRAWN.** It claimed a voice turn completes with no tunnel screen read. Under the fix
  for finding 1 a live screen read ALWAYS goes to the owning Director, so the behaviour it asserted no
  longer exists and the row is false by design. It is not re-scoped and not re-run. Withdrawn, with the
  reason recorded, is worth more than a surviving row nobody can interpret.
- **Row 6 is REPLACED.** It claimed a frozen push stream does not certify a stale screen. There is no
  certification left to defeat, so the row would now pass on the ABSENCE of the mechanism - vacuous. Its
  replacement is strictly stronger: every one of the three facts that used to certify a stored screen is
  satisfied, and the reader must STILL go to the tunnel.
- **Row 5 is RESTATED, not withdrawn.** It is still the right question and its answer is simpler.

Rows 0 to 4 survive unchanged in what they claim. They are about the half that stands: a session's
turn-end screen, stored per account for seven days, readable from anywhere including while the owning
machine is offline.

## The two labels these results used to carry - one is gone, one remains

Every phase 0 result used to travel with both halves of this sentence:

> proven against the mapped model, not the migrated schema; and proven from the store inwards, with the
> push path unexercised except by row 0, which stops at the sink contract.

**The first half is GONE.** It existed because the fleet-wide migration slot was held, so no real
Gateway could open a database containing `session_screens` and the store's tables had to be built from
the mapped model with `EnsureCreated`. Pull request 2643 landed and released the slot; the migration was
deleted and regenerated on the new snapshot; `dotnet ef migrations has-pending-model-changes` reports
*No changes have been made to the model since the last migration* on BOTH providers; and the throwaway
`ScreenStoreTestDb` was deleted rather than left as a second, easier path - the ending
`StatsConcurrencyTestDb` had. Every row now runs on a real `GatewayDatabase` over the real migration set.

**The second half REMAINS and still travels with rows 1, 2, 3, 5 and 6.** They seed the store by hand.
Not one of them drives `TurnReviewLogger` to `GatewayScreenSink` to the hub to the store, so if the push
were wired to nothing at all they would all still pass. What they say is: **the store and the reader
behave correctly WHEN HANDED a screen.** Row 0 takes the one cheap in-process seam and says where it
stops; row 4 is the only row that covers the whole chain.

---

## Row 0 - the real capture fires and reaches the sink (a partial, and it says where it stops)

**Claim.** The real `TurnReviewLogger` really does capture the screen on the real Working ->
WaitingForInput flip, and really does hand it to its sink with the grid and the byte mark taken
together.

**Pass condition, all three:**
1. A real `Session` with a real terminal parser is written to, then flipped to `WaitingForInput`.
2. The sink receives exactly ONE `TurnEndScreen`, whose `Rows` are the rows that were on that terminal,
   content asserted - not merely a non-empty capture.
3. Its `BufferBytes` is the number of terminal bytes THAT FRAME REFLECTS, and `HasGrid` agrees with
   whether there were rows.

**Strengthened by the fix round (finding 2).** Point 3 used to say the mark equals the buffer's own
`TotalBytesWritten`, and the capture read that counter before snapshotting the parser, claiming in a
comment that this could only ever understate the frame. It could not: the buffer increments its total
inside its write lock and feeds the parser afterwards, so the counter runs AHEAD of the parser and the
mark OVERSTATED the frame. `CaptureMarkDescribesTheCapturedFrameTests` drives that exact interleaving -
a subscriber ordered ahead of the parser holds a write open after the counter has moved and before the
parser sees it - and the mark is now taken from the parser's own consumed-byte count inside the same
lock that produces the rows.

**WHERE THIS STOPS.** It covers the flip, the capture, and the sink CONTRACT. It does NOT cover
`GatewayScreenSink.Send`, the `GatewayStreamClient.PushScreen` invoke, the `DirectorHub.PushScreen`
handler, or the store write. The first of those four now has its own instrument (see the instrument
checks below); the other three are row 4's.

Status: **proven.**

## Row 1 - a screen survives the push and comes back whole

**Claim.** A screen pushed by a Director is stored and read back with every field intact.

*Nothing-happened test:* would a store that was never written pass? No - `ReadLatest` answers null and
the assertion on the rows fails. But a test asserting only "not null" WOULD pass on a screen that
arrived mangled, so:

**Pass condition.** `ReadLatest` returns a screen whose rows are byte-identical to the pushed rows,
whose `CursorRow`, `CursorCol`, `CursorVisible`, `IsAlternateScreen`, `HasGrid` and `BufferBytes` each
equal what was sent, and whose `CapturedAtUtc` equals the captured instant to the millisecond. Field by
field, not by count.

**Plus the key's two properties, asserted in the same suite.** The same Director re-sending the same
capture stores ONE row (idempotency, which the byte-ordinal collation exists to protect). Two DIFFERENT
Directors capturing the same session id in the same millisecond keep BOTH rows - which they did not
before the fix round: the key carried no Director, so the second row was answered "already stored" and
silently lost (finding 3).

Status: **proven.**

## Row 2 - retention deletes, and does not delete everything

**Claim.** A screen older than seven days is gone after the retention job RUNS. Proven by running it,
never by reading it.

*Nothing-happened test:* "the old row is gone" passes on a store that was never populated, and on a
sweep that deleted the table. Both are closed below.

**Pass condition, all four:**
1. Before: the store holds exactly two rows - one received eight days ago, one six days ago - and both
   are read back.
2. `SessionScreenSweep.SweepAsync` is invoked and **returns 1**. The return count is the artifact the
   thing under test produced; a sweep that never ran returns 0 and fails here.
3. After: the six-day row is still readable, with its content intact. This is the control. A sweep that
   deleted everything fails this line.
4. After: the eight-day row is gone.

**And the sweep's second job, added by the fix round (finding 6).** A session left OVER the per-session
cap - the state two overlapping Gateway processes can leave behind - is trimmed back to the cap by the
same pass, and a second pass over a session already at the cap removes nothing. Seeded straight through
the context, past the write-time trim, because that is the state a lost race actually leaves.

Status: **proven.**

## Row 3 - tenant scoping, with account B producing a NAMED refusal and a successful read of its own

**Claim.** Only the owning account can read a screen.

*Nothing-happened test:* "account B could not read account A's screen" is absence-shaped by
construction, and a misconfigured account B that can read NOTHING passes it. This is the row ruling 3
called out to look at hardest. The inspection attacked this boundary specifically and it held.

**Pass condition, all four, in ONE run:**
1. Account A stores screen A; account B stores its OWN screen B, for a different session.
2. Account A reads session A and gets screen A, content asserted. (A's read works.)
3. Account B reads session B and gets screen B, content asserted. **This is the line that makes the
   proof mean something** - it establishes that account B's read path is alive, so its failure below is
   about the partition and not about a broken account.
4. Account B reads session A - account A's session id, spelled correctly - and gets NOTHING, in the same
   run where line 3 succeeded.

Status: **proven.**

## Row 4 - a screen captured on one machine, read back while that machine is OFFLINE

**Claim.** The history read works when the owning machine is gone, and what comes back is what was on
that terminal. This is the acceptance row the whole store exists for, and the only row that covers the
capture, the sink, the hub and the store write together.

**Pass condition, all four:**
1. A real Director, running a real session, ends a real turn - the Director's log records the capture
   leaving `TurnReviewLogger` and the Gateway's log records `SessionScreenStore` storing it.
2. The Director is then stopped, and its absence is read POSITIVELY off the Gateway: either the reader
   answers UNREADABLE naming the disconnected tunnel, or the route refuses because the session cannot be
   located at all. Not "we ran the shutdown command".
3. With that established, the Gateway's question-A read answers the screen, with the row printed out of
   the database.
4. **And the rows EQUAL what was on that terminal, both sides quoted.** Added by the fix round; see
   below for why.

**Why point 4 exists, and it is the reason this row was not previously worth what it said.** Inspection
01 replaced every pushed screen's rows with the single constant "MANGLED CONSTANT" and this row still
printed ROW 4 PROVEN, because its read-back asserted only a grid flag, a nonempty list, one nonblank
row, a positive byte mark and a nonblank Director id. Every one of those is satisfied by a push path
that throws the terminal away. Two comparisons replace them:

- The turn now ENDS on three lines the run authored, stamped with its own timestamp, so they are on the
  final screen rather than scrolled away. The read-back requires all three among the stored rows, IN THE
  ORDER they were printed. No constant and no row left behind by an earlier run can satisfy that.
- Every nonblank stored row must appear in the Director's OWN terminal text, read back over the separate
  `buffer` verb while the machine was still up. The capture came from the parser grid and this comes
  from the raw buffer, so agreeing means the stored screen is made of bytes that were really there.

The comparison is a substring match of each stored row against the terminal text rather than a
line-for-line equality of the whole grid: grid rows are trailing-trimmed and the raw buffer keeps its own
line breaks, so the two shapes do not admit a stricter comparison. That limit is written into the test.

Status: **proven**, by `scripts\terminal-rules-screen-proof.ps1` against a throwaway Gateway and a
throwaway Director under ruling 8's constraints, with teardown inside the run - and re-run against the
REGENERATED migration. Run and known-bad run both quoted in `fix-round/red-runs/rig-run.md`.

## Row 5 - the live question and the history question, in one run

**Claim.** A live-truth read is never answered from the store, and a history read still is - including
when the owning machine is gone.

*Nothing-happened test:* "`ReadLiveAsync` returned Unreadable" passes when the store is empty, when the
session does not exist, and when the whole fixture is broken. Closed by asserting the conjunction below,
with the positive first.

**Pass condition, all three, in ONE run:**
1. A screen is stored. With the tunnel answering, `ReadLiveAsync` returns `Source.Tunnel` and the LIVE
   rows, named by their content - which differ from the stored rows on purpose, so a store answer cannot
   be mistaken for a tunnel answer by taking the reader's own label on trust.
2. The tunnel then stops answering. `ReadLiveAsync` returns `Source.Unreadable` with a null grid and a
   reason naming the deciding fact, and the tunnel was positively observed to have been asked.
3. `ReadStored` STILL returns the screen, with its content asserted, in the same run. Both halves: the
   live answer changed, the history answer did not.

Status: **proven.**

## Row 6 - the store never answers the live question, even when every old freshness fact holds

**Claim.** A stored screen is never served as the live screen. This replaces the frozen-push-stream row,
which is vacuous now that there is no certification left to defeat (ruling 13).

*Nothing-happened test:* "the caller did not receive the stored screen" passes when the caller received
nothing at all, and when it was never called. Closed by naming what it DID receive, and by counting the
tunnel calls.

**Pass condition, all four, in ONE run - and this is the strongest temptation the old rule could face:**
1. A screen is stored; the owning Director's tunnel is CONNECTED; its snapshot was pushed one second
   ago; and the pushed byte count is EXACTLY the mark taken at capture. All three facts that used to
   certify a stored screen are satisfied.
2. `ReadLiveAsync` returns `Source.Tunnel`.
3. The rows it returns are the TUNNEL's rows, by content, and are demonstrably not the stored rows.
4. The tunnel was called exactly once - so a reader that answered from the store and merely relabelled
   its answer is caught.

And `ReadStored` still answers with the stored rows in the same run, so the row cannot be passed by a
reader that has simply stopped using the store.

**Why the old row went.** Its own shipped test froze the pushed byte count at 500, moved the clock to
nineteen seconds, and REQUIRED the reader to hand back the stale stored rows while a ready tunnel held
different ones. It asserted the defect. The byte count is refreshed by a ten-second snapshot timer and
by some activity transitions, never by a terminal write, so it could not establish what its name
claimed - and a certification may only rest on a signal refreshed by the event it claims to detect.

Status: **proven.** Against the unfixed reader this test failed `Expected: Tunnel / Actual: Store`.

## Row 7 - WITHDRAWN

**Was:** a voice turn completes with no tunnel screen read.

**Withdrawn by ruling 13, because resolution 2 of ruling 12 removed the behaviour it asserted.** A live
screen read now always goes to the owning Director, so a voice turn costs exactly the tunnel reads it
used to cost before the store existed. The claim is false by design rather than unproven, so it is not
re-run and not re-scoped. Its test class was deleted.

The pull counter it used is KEPT, with its purpose inverted and that inversion written into its own
comment: it existed to show the store SAVED round trips, and now shows that a live read really does
reach the tunnel. A change that quietly reintroduced store-answered live reads would make it move the
wrong way, which is what it is for.

---

## The instrument checks, run against known-bad input

Ruling 3's class applies to the instruments too. Each of these is run against a state where it MUST
fire, so that a later pass means something.

- **The push mapping is mutated exactly as the inspection mutated it** - every screen's rows replaced by
  one constant - and the DEFAULT gate fails: `GatewayScreenSinkMappingTests` goes red in about a minute,
  with no rig. Against the shipped code that same mutation left 3,189 tests passing and exit 0.
- **The rig is run known-bad with the same mutation** and the row FAILS, quoting what was stored:
  *the stored screen does NOT contain the line this run printed ... The rows that were stored are:
  MANGLED CONSTANT*.
- **The capture rendezvous** establishes the bad interleaving positively - the buffer's total really has
  moved past the parser - before asserting anything about it, and releasing the held writer shows the new
  bytes really were in flight.
- The retention sweep is shown to return 0 when nothing is expired and 1 when one row is, and 0 again on
  a second cap pass - so its return tracks reality rather than being a constant.
- `Validate` is run against each malformed push and asserted to name what was wrong.
- The tenant filter is shown to ANSWER for the owning account (row 3, line 3) before it is shown to
  refuse for the other.
- **The screen-drop counter** is shown to move by exactly one on a drop while the delivered counter does
  not, because "nothing was dropped" is satisfied by a Director that never pushed anything.
- **The rig's own verdict** rests on a file the run produced, which must name this run's session and this
  run's three markers - not on matching a word in another tool's summary line, which is what it used to
  do and which failed a run whose comparison had actually passed.
