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
event allowed to remove a session, and it is the single point the whole removal cascade hangs off - session
numbers, snooze rows, and a new `PushedSessionStore.Forget` so entries that survive a disconnect cannot
survive forever. Default twenty-four hours, settable per registry.

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

Two more destructive consumers were checked and are **not reached from this endpoint**:

- the **auto-dismiss sweeper**, which kills sessions, reads `PushedSessionStore.SnapshotFresh` and so still
  sees only connected machines with current pushes;
- the **desktop worktree reaper**, which does consume this endpoint, but aborts while any Director on its
  machine reads other than `online`. This is why the read never reports `online` for a stale serve: the
  reaper has no age check of its own, so the state string is its entire completeness gate.

## What is NOT proven, and what is out of scope

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

**A test run was killed that was not stuck.** The Gateway suite serialises fleet-wide behind a lock, because
concurrent runs destroy it. A queued run looked like a hang and was killed, even though its own log said, in
those words, that it was waiting and not hanging. Cost: one re-run. Read the log before reaching for the
process list.
