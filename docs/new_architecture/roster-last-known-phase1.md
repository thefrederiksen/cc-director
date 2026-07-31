# Gateway Read Model, phase 1 - the roster stops deleting

Epic #1159, step A. Branch `roster-asof`. This is the phase record: what changed, what is proven and how,
and - at least as importantly - what is NOT proven. The mission brief is in `devthrottle_internal` at
`docs/missions/gateway-read-model/BRIEF.md`.

## The defect

The Gateway holds each Director's sessions in memory and re-reads them on every roster request. It refused
to serve any machine whose last push was older than twenty seconds. A Director re-pushes every ten seconds,
so two missed ticks blanked a machine - every session, every colour - and with the tunnel dropping dozens of
times a day the owner's roster emptied several times an hour, while the Gateway sat holding the very data it
had just refused to show.

There were **two** staleness authorities stacked in the one path, and both of them deleted:

1. the pushed store was read through `TryGetFresh`, which returns null past the window, and that null was
   rendered as "unreachable, no sessions";
2. the result was handed to `FleetRosterCache`, which granted three poll cycles of grace and then declared
   the machine offline and dropped its sessions for good.

And a third, further out, which is the one that would have made a partial fix look like a whole one: the
registry swept a Director's entry sixty seconds after its last heartbeat, taking its cached sessions, its
snooze rows and its session numbers with it. Restoring the read alone would have bought about a minute
against an acceptance of five.

## What changed

**The roster read serves last-known state, unconditionally.** `GET /sessions` takes its sessions from
`PushedSessionStore.GetLastKnown` and reports how old they are. Age decides what the roster SAYS about a
machine; it no longer decides whether the machine is on it. `TryGetFresh` is untouched and keeps every one of
its callers - reads that ACT still refuse a stale answer, which is right, because acting on one could route a
command at a machine that is no longer there.

**Link state comes from the tunnel, not a countdown.** Online is tunnel up and a current push; wobbly is
tunnel up with nothing recent; offline is tunnel down - and offline now still serves every session. The three
wire values are unchanged because the Cockpit already renders them.

**`FleetRosterCache` is deleted, not bypassed.** Its only production consumer was this handler. A second
authority that can still be wired back in is a defect waiting to be re-introduced, and its whole job - keep
serving a machine that just went quiet - is now the unconditional behaviour of the read. (The brief warned it
was also used by the Car Mode loopback fleet. It is not: that is an unrelated private field of the same name,
a two-second response cache. Checked before deleting.)

**The eviction horizon replaced the sixty-second registry sweep.** Passing it is now the ONE elapsed-time
event allowed to remove a session.

**Eviction drops a machine from the read model and does nothing else.** This is the single most important
sentence in this document for anyone maintaining the code, because the obvious-looking change is to put the
cleanups back.

It began as a removal cascade: passing the horizon released the machine's session numbers, cleared its
snooze rows, and forgot its pushed entry. An independent inspection found that a cascade cannot be made safe
this way. Each step was a liveness check followed by a destructive action, and those are two operations, so a
Director that reconnected in between was destroyed while it was live - numbers freed and re-handable to a new
session, snoozes deleted outright. Guarding it harder does not help; the window is between the guard and the
act. So the destruction was removed rather than protected. What remains is one atomic operation,
`PushedSessionStore.ForgetIfDisconnected`, which takes the store's membership gate that registration also
takes.

**That gate is REASONED, NOT PROVEN, and this document previously stated it as a guarantee.** The argument
is that a reconnect must land entirely before the removal (so the entry survives, because a connection is
active) or entirely after it (so it re-creates the entry), leaving no in-between. Reading the source, that
argument holds. But **no test exercises the interleaving**: deleting the gate outright leaves the entire
repository suite green, which the store's own source comment says in those words. There is no concurrency
test and no surviving seam anywhere in this branch, so nothing here would notice if the reasoning were
wrong. Treat it as the best available argument, not as a demonstrated property - and if you are about to
rely on it for something new, that is the moment to write the test that does not exist.

**What that costs, at its real size.** Both leftovers are permanent, and one of them grows:

- A permanently retired machine keeps **every session number it held**, one per session it was running, out
  of a pool of nine hundred. `Adopt` only ever marks a number in use, so nothing reclaims them.
- Its **snooze rows stay in the database indefinitely**. `PruneNotLive` clears a Director's rows when that
  Director answers, and a retired machine is precisely the one that never answers again - so no prune ever
  reaches them, and every further retirement adds its own set. There is no ceiling and nothing that removes
  them.

That is accepted deliberately. Dead rows and marked-in-use numbers can be reclaimed later by something that
establishes the machine is gone before it acts; a snooze destroyed by the race cannot be reconstructed by
anything, because it was the owner's intention to set that machine aside until a particular time.

`FleetSessionNumberAllocator.ReleaseForDirector` and `SnoozeRegistry.ClearForDirector` still exist and now
have **no production caller** - only tests. **Do not wire either back to `OnDirectorRemoved`.** That restores
the race, and `EvictionRaceAndCompositionTests.EvictionLeavesSnoozesAndNumbersAlone_OnTheRealHost` exists
specifically to redden when it happens: it evicts a disconnected Director from a real host and then asserts,
separately, that its armed snooze and its session number are both still there. Re-adding the snooze clear
fails the first assertion; re-adding the number release fails the second.

The default is twenty-four hours, confirmed by the owner, and it is read from
`gateway.directorEvictionHorizonHours` in `config.json`. It started as a compile-time constant that only a
test could move; that would have made the word "configurable" untrue the moment this went live, since
changing it would have needed a release. A zero or negative value is refused and the default stands - a zero
horizon would evict every machine on the next thirty-second sweep, which is the deleting roster this read
model exists to end, and it would arrive as a silent typo rather than as a decision anybody made.

**Every serve that is not confirmed current is marked stale**, and the destructive consumers stay behind that
guard.

**Two different questions, deliberately two different flags.** `Stale` (not confirmed current) governs what
may be acted on. `MachineReachable` (tunnel up) governs whether a session may nag. Collapsing them would make
the badge flicker off whenever a push ran a few seconds late - this mission's own defect in a third disguise.
This distinction was missed on the first pass and caught in review; see "what we got wrong" below.

## The consumers of the served roster, enumerated

The brief called this the sharpest risk, and it is: the same list feeds things that ACT.

| Consumer | Destructive? | Placement |
|---|---|---|
| `owners.RetainForDirector` | yes - evicts ownership records | inside the `!stale` guard |
| `snoozeRegistry.PruneNotLive` | yes - deletes database rows | inside the `!stale` guard |
| `inputStats.ObserveSnapshot` | no, but inflates | confirmed-live subset only |
| `concurrency.Observe` | no, but inflates | confirmed-live subset only |
| `owners.Remember` | additive | everything served (correct - keeps "owner offline") |
| `sessionNumbers.Adopt` | additive, never frees | everything served (correct - holds the number) |

### The cost of serving more sessions through this path

**This change makes every roster request do more synchronous database work, and the amount scales with how
many sessions are retained.** It is stated here because this is the document a maintainer or a hosted
operator reads, and without it the eviction horizon looks like a retention setting when it is also a
performance dial.

The per-session fold on the read path calls `HoldStateFor`, `IsExpired` and `SnoozeUntilFor`. Each of those
opens a database context and queries, and each does so inside the snooze registry's **process-wide lock** -
so the work is at least three synchronous database reads per served session, serialised across the whole
process, on every roster request.

Before this change a machine whose tunnel dropped silently shrank the list, which took its sessions out of
that work. Now they stay in it. Five machines left off overnight keep their sessions in every roster request
for the whole eviction horizon, so **raising the horizon raises the per-request database work**, not just how
long cards linger.

Magnitude, honestly: negligible on a personal fleet, materially worse at hosted scale. It was **not measured
and not fixed here** - it is disclosed, not solved. Reducing it is owned by the Gateway remediation mission,
which is rebasing onto this branch; that mission's existence is not evidence that this cost is gone.

Two more destructive consumers were checked and are **not reached from this endpoint**:

- the **auto-dismiss sweeper**, which kills sessions, reads `PushedSessionStore.SnapshotFresh` and so still
  sees only connected machines with current pushes;
- the **desktop worktree reaper**, which does consume this endpoint, but aborts while any Director on its
  machine reads other than `online`. This is why the read never reports `online` for a stale serve: the
  reaper has no age check of its own, so the state string is its entire completeness gate.

## What is NOT proven, and what is out of scope

- **The membership gate that makes eviction atomic is REASONED, NOT PROVEN.** This is the most load-bearing
  unproven thing on the branch, and it was missing from this list while the body of this document asserted
  it as fact. Removing the gate entirely leaves every test in the repository green. No concurrency is
  tested anywhere in this phase; the sweep seam proves ORDERING, not scheduling. An attempted two-thread
  test was written and DELETED because its assertion also passed when the competing thread was merely slow,
  which would have been worse than no test - a green that proves the absence of the thing it was written to
  prove the presence of.
- **`GET /sessions/{sid}` still refuses a single session on age.** It is a display read with the same flaw.
  It sits on the per-session enrichment lines the brief put out of bounds for this phase, so it was left
  alone rather than half-fixed. It should be step A's first follow-up.
- **`/healthz` counts only fresh sessions.** Left as it is: that endpoint answers "how much is live right
  now", which is a liveness question, not a roster question.
- **The phone's badge call site has no test.** `apps/mobile` has no test runner configured, so the one-line
  call in the home page is verified by reading. The rule it calls is unit tested, and the desktop's
  equivalent call site is tested.
- **Nothing was exercised against a running Gateway, a real phone, or a real disconnected machine.** All
  proof here is unit, endpoint and rendered-component level.
- **A restarted Director that changes its identifier would leave a ghost lane for a day.** Identifiers are
  stable in practice, so this is a reasoned expectation rather than a measured one.
- **The generated client schema was deliberately not regenerated.** A dump taken from a test-booted Gateway
  omits about twenty routes that are still mapped - `/shutdown` and the whole `/ingest/recording` family
  among them, the latter called by our own client package - because those routes register conditionally.
  Taking it as truth would have deleted live routes from the typed contract. The one additive field rides in
  the hand-written layer instead, with a note to remove it once someone regenerates from a fully-wired
  Gateway and reads the diff.

## What we got wrong along the way

**The first cut keyed "may this session nag" to staleness rather than to the tunnel.** That would have
dropped a merely-late machine out of the badge and the voice queue, so the count would have blinked off
whenever a push ran a few seconds behind - the same transient-staleness-destroys-information defect, one
disguise further on. It was caught by the Worker doing the client half, reading the Gateway's stamp and
asking whether wobbly really belonged in it. Fixed before either half landed.

**The retention hypothesis in the brief was half right, and the half that was wrong mattered.** The brief
supposed the phone had nothing to retain against because the stale Director was filtered out of the response
entirely. The Director was in fact still named in the reachability list, and the retention retains against
its own cache rather than the envelope, so in the warm case the rows survived. The real mechanism is a **cold
start**: the cache lives in a reference inside the home page, and that page is a leaf route, so opening a
session and coming back discards it - as does any reload or relaunch. With an empty cache and a Gateway
serving nothing, the roster came up blank. Recorded because the brief's version is written down and someone
will otherwise believe it.

**A live worktree was deleted twice, and the cause is NOT established.** Mid-mission, this branch's
worktree was emptied at 20:08 and again about seven minutes after it was rebuilt - `.git`, the solution file
and most top-level directories gone, and the worktree's admin record removed from the shared checkout, with
the branch neither merged nor carrying a pull request. Nothing was lost: every commit was already pushed, and
the one uncommitted change was salvaged. Work moved to `dt-roster-asof2`.

A tempting reading is that the thing deciding a worktree is safe to remove asks the roster whether a live
session is using it - the same roster this mission is fixing - which would make the incident evidence for the
mission. **That reading is unverified and should not be repeated as fact.** Every Director log on the machine
was searched for reap and worktree-removal activity and for the path itself, and there was nothing. So the
mechanism is unobserved, and a plausible cause written down without observation is precisely the failure this
repository has laws about. Someone should establish it properly before it is cited.

**A test run was killed that was not stuck.** The Gateway suite serialises fleet-wide behind a lock, because
concurrent runs destroy it. A queued run looked like a hang and was killed, even though its own log said, in
those words, that it was waiting and not hanging. Cost: one re-run. Read the log before reaching for the
process list.
