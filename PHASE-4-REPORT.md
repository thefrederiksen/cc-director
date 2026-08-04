# Phase 4 - Lifecycle off HTTP

Manager: session 946d35e4. Branch `mission/remove-network-port`, worktree `D:\ReposFred\devthrottle-noport`.
Commits: `d64a2f2e4` (the change), `62cbb1e90` (its callers, scripts and documents), `900c4a3c0` +
`8c76a679b` (this report and the interaction analysis), `502293c3d` + `ce0e9a5dc` (the approved
conflict tie-break and the guard test that found a hole).

---

## What the phase was for, and why it is not plumbing

Everything else in this mission routes through the Gateway, deliberately, so that there is one door.
Lifecycle cannot, and the reason is an availability requirement rather than a preference: **it must work
exactly when the Gateway does not.** It is how the launcher supervises the Director, and how an update
makes the Director exit so its executable can be replaced. A Director that could not be stopped without
the internet could not be updated without it either, and a launcher that could not be quit because a
cloud service was unreachable could not be uninstalled.

So the four lifecycle routes did not move to the Gateway. They moved off the network entirely.

| Was | Is now |
|---|---|
| `GET /healthz` (authenticated: version + session count) | The instance registration the running process wrote, plus the roster it maintains |
| `POST /shutdown` | A named signal keyed to that Director's own identifier |
| `POST /update/check` | A named signal, same scoping |
| `GET /update/status` | **Deleted.** It had no caller outside the Director at all |
| Launcher `POST /shutdown` | A named signal keyed to the storage root the launcher serves |
| Launcher `POST /director/restart` | The same |

`GET /healthz` itself still exists. It is no longer a lifecycle route - nothing outside the Director
reads it - and its only remaining caller is the Director's own startup self-probe, which exists to prove
that the bound port is not being shadowed. That is port machinery, and Phase 5 deletes it with the port.

---

## The correction the Architect expected to be needed, confirmed and then reproduced

The brief originally said the launcher owns the process it started, so liveness needs no network call.
The Architect had already found that false and said so. This phase confirms it and adds the measurement.

`DirectorSupervisor` disposed the process handle at `Process.Start` and afterwards found the Director
again with `Process.GetProcessesByName("cc-director")`, keeping the first match whose image path was the
installed executable. **A named instance is the SAME executable with a different data home.**
`cc-director.exe` and `cc-director.exe --instance work` have the same process name and the same image
path, so a machine running both produced two identical matches and the launcher kept whichever the
operating system listed first.

That is not a wrong answer some of the time. It is an arbitrary answer every time, and it decides two
things that matter: which Director gets a shutdown, and whose session count is read to decide whether an
update may interrupt live work.

**Reproduced with two real processes** (rig A below):

```
Process.GetProcessesByName('cc-director') with image path = the installed exe
  matched 2 process(es): 24040, 71056
```

Both were live Directors from one executable; only 71056 was the supervised instance.

### What answers it now

`DirectorInstanceLocator` reads only the supervised instance's own registration directory, takes the
process id from the file the Director itself wrote, and **never consults the process name or the image
path at any point.**

A registration outlives a Director that was killed, and process ids are reused, so "the file says 34032
and 34032 exists" is not proof either. The identity check is the process's own **start time**: a Director
always registers a moment after it starts, so the true author's start time sits just before the
registration's timestamp, while a recycled id belongs to a process that started later - after the
original died, which is necessarily after the file was written. That one comparison rejects every
recycled id and every leftover registration, using only fields the file already carried. No schema
change, so it also works against a Director older than this launcher - which matters, because the
Director being replaced during an update IS the older build.

**Ambiguity is an answer wherever it is real.** When more than one live process claims the supervised
instance the locator decides it only when it is decidable, and otherwise reports `Ambiguous`, names every
claimant, and every caller refuses to act. Two processes running the SAME image are the defect and stay
refused - picking one there would be the original defect wearing a tidier interface. The full line, and
the Architect's ruling on it, is in item 2 of the interaction section below; the first version of this
phase refused on EVERY conflict, which turned out to be a real cost on the owner's own machine.

That state is real, not theoretical. It exists on this machine right now, in the owner's own storage:

```
instances/default/config/director/instances/
  6d4523e2-....json   Pid 34032   v1.9.7   ALIVE   C:\...\cc-director\app\cc-director.exe
  7a7a040a-....json   Pid 15700   v1.9.1   ALIVE   D:\ReposFred\dt-slot2\local_builds\cc-director2.exe
```

Two live processes, different executables, both claiming instance `default`. Today they are told apart
only by accident (the slot build is named `cc-director2`, so the name scan misses it). **A finding worth
recording separately: `SingleInstanceGuard` is keyed by executable slot, not by instance, so it does not
prevent two different executables from claiming one instance home.** That is not this phase's to fix, and
the locator now refuses rather than guessing when it happens.

---

## A second correction, made on evidence rather than preference

The brief said "version comes from the exe on disk". **That would have silently destroyed the update's
roll-back.**

The version is what certifies a swap: a staged update is newer than what was running, so an answer
carrying the new version cannot have come from the old build - where a liveness check ("something is
there") could. The executable on disk is replaced **before** the new Director is started. Read from
there, the launcher would report the new version whether or not anything came up, the wait for a healthy
build would succeed instantly every time, and the restore of the previous build could never fire.

So the running version is read from the **instance registration the running process wrote**. A Director
that never started cannot supply it.

This is not an argument. It was observed: during the first swap attempt in rig C the new build hung at
startup, the executable on disk WAS 1.9.8 for the whole health wait, and the launcher still concluded the
build had not come up and restored 1.9.7 (`decision=RolledBack`, installed hash back to `A15A5380`).

---

## The session count

`DirectorCrashJournal.ReadLiveRoster` reads the roster the Director maintains at
`<instanceHome>/config/director/crash-journal/<directorId>.json`.

The standing objection to a file is that it says what was true at some earlier moment. That objection does
not apply here, and the reasons are structural: the file is rewritten **atomically on every change to the
session set**, and it is **deleted on a clean shutdown**. So while a Director is alive the file is exactly
as current as the roster itself, and it carries every session's identity, which the health route never
did.

**A missing roster reads as unknown, never as zero.** The update owner turns that into
`HeldBecauseUnknown`. A zero there would restart a Director holding live work.

---

## Proof

Everything below drove the **production** `DirectorSupervisor` / `DirectorInstanceLocator` /
`DirectorUpdateOwner` against a throwaway storage root - no re-implementation, no copies. The harness only
selects a verb.

**No Gateway was reachable in any of it**, and that is stated by the product rather than assumed:

```
[GatewayAccountStatusClient] GetStatusAsync: no gateway.url configured -> not configured
[GatewayRegistrationClient] Gateway not configured; launcher will not register
```

The rig ran as a **single-file self-contained** publish for both the installed and the staged build,
because that is what the release workflow ships (`--self-contained true -p:PublishSingleFile=true`). A
framework-dependent rig would have swapped an executable whose version lives in a separate assembly, and
proved something about a different artifact than the one users get.

### Rig A - liveness resolves the right Director with more than one running

Two real Directors from one executable: pid 71056 as `default`, pid 24040 as `work`.

```
outcome=Running
  candidate: directorId=bdd06541-... pid=71056 version=1.9.7 started=2026-08-03T23:19:31Z
resolved:   directorId=bdd06541-... pid=71056 version=1.9.7 home=...\instances\default
status:     directorId=bdd06541-... pid=71056 version=1.9.7 sessions=0
isRunning=True
```

One candidate, named by identifier, while the old scan matched both.

**The discriminating half.** The default Director was then stopped and the `work` one left running:

```
locator:            NotRunning
old name scan still matches: 24040
```

The old code would have reported the supervised Director as running and gone on to read a session count
from - and potentially shut down - a Director it does not supervise.

### The shutdown signal, and that it is a clean stop

```
before: Running pid=71056
StopAsync returned after 2166ms
after:  NotRunning
crash journal: deleted - the Director cleaned up after itself
```

From the Director's own log:

```
[LifecycleSignal] Listen: name=cc-director-shutdown-bdd06541-...
[LifecycleSignal] cc-director-shutdown-bdd06541-...: signalled
[CcDirector] shutdown requested by lifecycle signal
[CcDirector] Exiting
```

No port, no credential, and the journal deleted - so this stop leaves no phantom "interrupted" entry, the
harm the graceful path exists to avoid.

### The session count is real, not a fixture

A **real session** was created in the rig Director through its own interface, and then read back off disk:

```json
"Sessions": [ { "SessionId": "cee51b26-...", "Name": "phase4 roster proof",
                "Agent": "ClaudeCode", "ClaudeSessionId": "9bc805a7-..." } ]
```

```
status: ... sessions=1
```

This is the one observation that turns the plan's "confirmed" into "witnessed": the roster's count equals
the Director's live session count, read by the launcher with no network.

### Rig C - a self-update, with live sessions and without

**Held, with a session running.** Staged 1.9.8, one live session:

```
staged: version=1.9.8 ...
decision=HeldBecauseBusy
after: version=1.9.7 pid=51400 sessions=1
installed exe hash BEFORE: A15A538031882913
installed exe hash AFTER:  A15A538031882913
```

Byte-identical. No session was interrupted.

**Applied, once idle.** Session closed, roster drained to 0:

```
decision=Applied
after: version=1.9.8 pid=70488 sessions=0
installed exe: A15A538031882913 (v1.9.7)  ->  707D22A465646BB0 (v1.9.8)
staged 1.9.8 hash:                            707D22A465646BB0
```

The executable was swapped for the staged build byte-for-byte, a new Director came up, and the launcher
certified 1.9.8 from the registration the **new process** wrote.

**Rolled back, when the new build did not come up.** Recorded above under the second correction.

### Rig B - the launcher restarts a Director that was killed

A real `cc-launcher.exe` process, its own storage root, no Gateway.

```
Director before: pid=70488 v=1.9.8
FORCE-KILLED it (a crash, not a stop)
locator: outcome=NotRunning

raising cc-director-launcher-restart-director-5565b3daaea0: delivered=True
Director after: pid=66680 (ALIVE) v=1.9.8
```

From the launcher's log:

```
[LifecycleSignal] cc-director-launcher-restart-director-...: signalled
[LauncherCore] Director restart requested by lifecycle signal
[DirectorSupervisor] Start: launched Director pid=66680
```

And the launcher's own quit, by the same mechanism:

```
raising cc-director-launcher-shutdown-5565b3daaea0: delivered=True
launcher exited
```

### The owner's machine was not touched

Verified after teardown - the installed Director, the slot-2 Director and the installed launcher all still
running, and the temporary scheduled task removed:

```
34032 cc-director   C:\Users\soren\AppData\Local\cc-director\app\cc-director.exe
15700 cc-director2  D:\ReposFred\dt-slot2\local_builds\cc-director2.exe
78464 cc-launcher   C:\Users\soren\AppData\Local\cc-director\launcher\cc-launcher.exe
```

---

## Detector validation - four faults injected, each after committing

Every new test was made to fail on purpose, so none of them is a test that cannot fail.

| Fault injected | What went red | Everything else |
|---|---|---|
| Start-time identity check made unsatisfiable | `ProcessStartTime_AfterTheRegistration_IsRejectedAsARecycledProcessId` AND `..._LongBeforeTheRegistration_IsAlsoRejected` | 11 green |
| The OLD behaviour restored - scan every instance home | `ANamedInstanceRunningAlongside_IsNotTheSupervisedDirector`, and only that | 12 green |
| Missing roster returns 0 instead of null | `AMissingRoster_ReadsAsUnknownRatherThanZero`, and only that | 12 green |
| `Raise` reports an undelivered request as delivered | `RaisingASignalNobodyListensFor_ReportsItWasNotDelivered` AND `ADisposedListener_IsNoLongerReachable` | 11 green |
| Tie-break degenerates to "pick the first" when no claimant is the install | `WhenNoClaimantIsTheInstalledDirector_ItStillRefuses`, and only that | 15 green |
| A resolved conflict is swallowed instead of carried | `TwoClaimantsRunningDIFFERENTImages_..._AndStillReportTheConflict` | 15 green |
| An unreadable image no longer forces a refusal | **NOTHING** - see below | all green |

The second one is the one worth reading: restoring exactly the behaviour the old tests asserted turns
exactly one test red - the one written for the defect.

**The last row is the one that found something.** Removing the guard that refuses when a claimant will not
say what image it is running turned NOTHING red - the branch had no test, and its absence was invisible.
That is the fail-open shape this mission keeps finding: a guard nobody can tell is gone. The image read is
now a seam so the branch can be exercised, re-injecting the fault turns
`AClaimantWhoseImageCannotBeRead_ForcesARefusal` red, and what that test proves is stated in the test
rather than implied - the BRANCH, not that Windows really refuses to report the image of an elevated
process, which cannot be produced honestly from a unit test.

**A process note worth recording against myself.** Fault E was injected on an UNCOMMITTED tree and reverted
with `git checkout`, which destroyed the work it was testing and cost a rebuild of the whole tie-break.
This mission already has a law for that - commit before injecting a fault - and it exists because this is
exactly how it goes wrong. Everything after that point was committed first.

---

## What the signal is, and the one place it is not uniform

A named signal: the listener holds it open for its whole life, a sender raises it by name, no payload and
no reply. Every request here is a verb with no arguments ("shut down", "restart the Director", "check for
updates"), and a request with no payload cannot be injected with one.

**Two mechanisms, one per platform - not a fallback chain.** Windows uses a named `EventWaitHandle`:
instantaneous, owned by the kernel, and destroyed when the listening process dies, so a signal can never
be left lying around to fire at the wrong process later. Unix has no named event in .NET (only `Mutex` is
named cross-platform), so it uses a request file that the listener **polls** - deliberately polls, because
a `FileSystemWatcher` silently drops notifications (this mission measured roughly one in five) and the
delivery guarantee must never be the lossy path. A stale request is rejected by an age stamp rather than
by a retry. The platform is chosen once, by `OperatingSystem.IsWindows()`; neither arm is ever tried after
the other fails.

**The caller contract is the same on both:** `Raise` returning true means the request was HANDED OVER,
never that it was carried out. Every caller verifies the effect it wanted. That is deliberate - the
Windows arm can tell you nobody was listening and the Unix arm cannot, and a contract only one platform
can honour would quietly become a Windows-only guarantee that Unix code was written against.

**Every name is scoped**, and that is the whole point. A Director signal is keyed by its identifier, the
only string that names one process; a launcher signal is keyed by the storage root it serves, so a rig
with its own root and the installed launcher never hear each other. Both are read from files, so a sender
needs nothing running to work out what to call.

---

## The callers, and two documents that named a route that no longer exists

- `scripts\agent-session-isolation.ps1` stopped a test Director by posting to its Control API with a
  credential resolved from that instance's storage. It signals by identifier now. The failure it used to
  have - no readable token, silent fall-through to a force-kill that leaves a phantom journal entry -
  cannot occur, because there is nothing to read.
- `CLAUDE.md` rule 0b told **every agent on this machine** to `POST /shutdown`. Corrected.
- The `dev-throttle` skill listed `/shutdown` as a live route **in both of its copies** - the one on disk
  and the one the Gateway serves. Fixing one would have left the served one wrong; this is the standing
  law about that skill body, and it held.
- `LauncherStopper` (the uninstaller) asked politely only when the launcher port was held by a process
  positively identified as ours, because a per-user bearer token would also be accepted by a launcher
  from a developer's checkout. The signal is named for the storage root, so **the scoping moved from a
  runtime check into the address itself** - and a launcher that holds no port at all (still starting,
  failed to bind) can now be asked politely, which the port-gated version could never do.
- The launcher's own `--apply-update` helper posted to its own `/shutdown`, which issue #1609 records
  failing with a 401 and aborting the swap on a locked executable. There is no credential to omit now.

## Tests removed, and why neither is coverage lost

- `DirectorSupervisorCredentialTests` drove the launcher's minting of a credential for calls to
  `/shutdown` and `/healthz`. Both routes and the whole mechanism are gone; no version of it could pass
  without reinstating them.
- `DirectorSupervisorInstanceDiscoveryTests` asserted that the flat root **and every named instance home**
  were scanned - which is precisely the behaviour that made two instances indistinguishable. It was
  correct as a way to find registrations and wrong as a way to identify one Director. Its legitimate half
  (the pre-1.8 flat layout must still be found) is kept and tested:
  `APreInstanceLayoutRegistration_IsStillTheSupervisedDirector` and
  `TheSessionCountOfAPreInstanceLayoutDirector_IsReadFromTheRoot`.

## A stale positive control, caught rather than trusted

`SessionHookRoutesAreGoneTests` validates its 404 detector against routes that still exist, and one of
them was `update/status`. Left alone it would have 404ed like everything else and reported the detector
broken - a true statement about a control that had simply gone stale, and a red that reads as "no 404 in
this class can be trusted". It was replaced, and the lifecycle routes are now asserted **absent** through
that same validated detector.

For the same reason `shutdown` and `update/status` were removed from the hostile-access must-401 tables: a
401 on a path that does not exist is an auth refusal standing in for absence, which is evidence about
nothing.

---

## The gate - comparative, as the mission requires

`obj` and `bin` were deleted on both arms before every run, so no result rode a stale assembly.

| Arm | Failures | Which test | Exception |
|---|---|---|---|
| Mission (pre-tie-break) | 1 | `HostedEnrollmentEndpointTests.WrongAudience_Is401` | `FileLogWriter` completed collection |
| Mission (pre-tie-break), `-Parked` | 1 | `GatewayInputStatsAggregatorTests.AgentTotals_...` | the same |
| Mission (post-tie-break) | 2 | `AuthMiddlewareTests.Bearer_with_a_valid_device_key_is_accepted`, `HostedEntitlementGateTests.IGNORANCE_does_not_deny_and_does_not_mint` | the same |
| Parent `76e9bd25c` run 1 | 1 | `SpokenVoiceTests.A_stored_voice_..._degrades_to_that_languages_default` | the same |
| Parent `76e9bd25c` run 2 | 0 | - | - |
| Parent `76e9bd25c` run 3 | 2 | `GatewayStatsReadTenantScopeTests.SessionCounts_ForATenantWithNoRowsAtAll_ReturnNothing` | the same |
| | | `HostedEntitlementGateTests.Enrollment_grants_..._(tier: "hosted")` | `Cannot access a disposed object` on the test SQLite database |
| Mission (post-tie-break), `-Parked` | 2 | `SuggestionEmailComposerTests.DefaultOn_IncludesTheBlockWithNoSettingWritten`, `DictionarySuggestionServiceTests.RunScan_RejectedTermIsNeverShown` | the same |

**Nine failures over seven runs on two commits. Nine DIFFERENT tests, no repeats. Eight of the nine are
one exception**, and the ninth is its sibling - the same shape of teardown race against a different shared
object. `SuggestionEmailComposerTests` is worth noting because the mission recorded it failing on BOTH
arms back in Phase 3: an arbitrary victim will recur by chance, which is exactly what a single shared race
looks like from a distance and is why it kept being written off as a flaky test.

The parent failed in two runs of three, and was clean in the third. **A single-run control would have been
a coin toss in both directions**: parent run 2 alone would have convicted this phase of a regression it
did not cause, and either of the others alone would have excused one it did. This is the comparative rule
earning its place rather than illustrating it.

**Parked suites, both of which this phase touches and the gate said so itself - GREEN on both mission
commits, run twice:**

```
CcDirector.Gateway.Tests   Passed!  Failed: 0, Passed: 2457, Skipped: 47, Total: 2504
CcDirector.Core.Tests      Passed!  Failed: 0, Passed: 4218, Skipped:  8, Total: 4226
```

### A finding about the gate itself, narrower than "flaky"

This mission has already recorded that the local gate is luck - ten distinct failures over six runs with
no repeats. These seven runs narrow that considerably: **eight distinct failures, all `FileLogWriter.Enqueue`
on a completed `BlockingCollection`, plus one "cannot access a disposed object" against the test database
which is the same shape against a different shared object.** **Filed as issue #2445** with the full run-by-run
evidence, and a SECOND independent line added to it by the Architect: `SkillStoreTests`
`A_dangerous_file_path_is_refused` dying with the same exception INSIDE the `SkillStore` constructor,
nowhere near a path check, in a different suite reached by a different entry path - the clearest
demonstration that the victim is arbitrary, because that victim's NAME points somewhere completely
unrelated. The hypothesis at the time was a shared temporary directory; it was wrong and was struck on
that stack, and the issue records the dead hypothesis on purpose because the next reader will form it too.
The finding is filed rather than fixed on the Architect's ruling that it is not this mission's work to fix - a race in the logging
teardown deserves its own change with its own proof rather than being smuggled in beside a port removal.

That is not randomness, it is one
process-wide race - a test's teardown calls `FileLog.Stop()` (completing the collection) while another
test in the same parallel run is still logging, and whichever test happens to be logging at that instant
is the one that fails. It looks like a different flaky test every time because the victim is arbitrary;
the cause is not. That is a fixable defect in the fleet's own gate rather than a fact of life, and it
belongs in the QA report on those terms.

Targeted runs, all clean before the full gate: `DirectorInstanceLocatorTests` and the rest of
`CcDirector.Launcher.Tests` 114/114 (110 before the tie-break added four); `LifecycleSignalTests` + `NoCrossMachineLoopbackGuardTests` 15/15;
the Control API classes in `CcDirector.Gateway.Tests` 140/140; `LauncherStopperTests` 10/10.

---

## Does this phase make the open items better, worse, or unchanged?

Asked by the Architect, and it is the right question: a pre-existing defect the mission's own changes
INTERACT with is not the same as one it merely walked past. Answered one at a time, with the confidence
of each answer labelled, because two of these are inferences from reading code and two are observations.

### 1. A Director started with no instance flag blocks on the picker - UNCHANGED in outcome

**Every update rolls back on a machine with several named instances, before this phase and after it.**
The launcher starts the Director with no instance flag; on such a machine that Director shows the picker
and never finishes starting; the new build therefore never reports its version and the previous build is
restored. Phase 4 changes neither half of that.

It could not have, and this is worth stating because it looks like the kind of thing this phase would
touch: the OLD path asked `/healthz`, which a Director blocked at the picker never serves because its
Control API is never started; the NEW path reads the registration, which that same Director never writes.
Both are silent in the same way for the same reason. Only the mechanism moved.

**One behavioural difference, and it is a code reading rather than an observation** - I did not run the
old code through this scenario, and say so rather than implying I did. The blocked process holds the
single-instance mutex but never registers. Under the old name scan it MATCHED, so the launcher counted it
as a running Director: `IsRunning` was true, health was null, and every later update pass concluded
`HeldBecauseUnknown` for ever. Under the locator it does not match, so the launcher says
`HeldBecauseDirectorNotRunning` - which is the truer statement, and it does not disguise a hung process
as a healthy Director. In the roll-back itself the restored build is now actually launched and exits
loudly on the mutex (`SingleInstanceGuard: Another instance holds`, observed in the rig) where the old
code would have skipped the start without a word. Same end state, louder trail.

### 2. `SingleInstanceGuard` is keyed by executable slot, not instance - BETTER and WORSE, and the worse one is live on the owner's machine

This is the one that must not be blurred, so both directions are stated.

**BETTER - the blast radius.** When this defect fires, the old launcher picked an arbitrary one of the
claimants and could stop it, or read its session count and update over it. The locator refuses. The
defect can no longer cause an action aimed at the wrong Director. The cause is untouched; what changed is
what it can do.

**WORSE, THEN FIXED - availability, on this machine.** Phase 4 first chose to refuse rather than guess,
and refusing is not free. Run read-only against the owner's real storage root, the launcher decided:

```
outcome=Ambiguous
  candidate: directorId=6d4523e2-... pid=34032 version=1.9.7  ...\cc-directorpp\cc-director.exe
  candidate: directorId=7a7a040a-... pid=15700 version=1.9.1  D:\ReposFred\dt-slot2\...\cc-director2.exe
status: (none - nothing may be done to this machine's supervised Director)
```

Two live processes, both registered in `instances/default`. It would not have stopped, restarted or
updated his Director - every update pass returning `HeldBecauseUnknown` - until one of them left.

It worked before only because the name scan finds 34032 and misses the slot build, which happens to be
called `cc-director2.exe`. Lucky, not correct: two Directors of the SAME executable would have been picked
between arbitrarily. But a behaviour change he would notice, caused by this phase.

**The Architect approved the narrowing, and it is in: `502293c3d` / `ce0e9a5dc`.** Ambiguous ONLY when the
claimants share an executable - the case a path genuinely cannot decide, and the real defect - otherwise
the installed application wins, because a development build is not the machine's Director of record.
Same machine, same read-only probe, after:

```
outcome=Running
resolved: directorId=6d4523e2-... pid=34032 version=1.9.7
status:   ... sessions=3 conflictCarried=True
conflict=2 live processes claim the instance at ...\instances\default: <both named in full>
```

Resolved to the installed Director, and it read **3 sessions** off his real roster while doing it.

**Why this is not a fallback**, stated so nobody later reads it as an exception the mission granted
itself: the no-fallback rule forbids two PATHS to one capability - try one thing, fall back to another -
because the second path is a door. This is one path with a tie-break on an ambiguous input. There is no
second mechanism, nothing is retried, and the undecidable case still ends in a refusal.

**And the conflict does not go quiet.** That was the Architect's condition, and it is the more important
half: a resolved conflict is still a machine in a wrong state, and a tie-break that silently does the
right thing is how this defect stayed unseen long enough for a mission looking at something else to find
it. So the conflict travels on the answer (`conflictCarried=True` above), not only into a log, and the
update owner writes it into the updater state - the file the update display reads - so it reaches a
person.

The underlying cause is untouched and stays open: the single-instance guard is still keyed by executable
slot rather than by instance.

### 3. An unknown `--instance` slug silently becomes the default - same family as 2

Cause untouched. Consequence better for the same reason and at the same cost: it is one of the ways two
processes come to claim one instance home, and that state is now refused rather than resolved
arbitrarily. This is how it happened in the rig, which is how it was found.

### 4. macOS and Linux - WORSE, and not merely "still unproven"

The mission already records macOS and Linux as an open hole from Phase 3. That record understates what
this phase did to them, so it is corrected here rather than inherited.

Before Phase 4, lifecycle on macOS worked over HTTP - the same mechanism as Windows, proven to the extent
that shipping proves anything. After Phase 4, macOS lifecycle depends on a **new** mechanism written for
this phase (a polled request file) that **has never been run on that platform**. A proven-by-shipping path
was replaced with an untested one. That is strictly worse there until somebody runs it, and it is a
different statement from "macOS was already unproven".

---

## Recorded open, not closed

- **Windows only, and this phase made macOS worse rather than leaving it as it found it.** See item 4
  above: a proven-by-shipping HTTP path was replaced there by a mechanism that has never been run on that
  platform.
- **`SingleInstanceGuard` does not prevent two executables from claiming one instance home.** Found while
  building the rig, present on this machine, and outside this phase. The locator refuses rather than
  guessing when it happens, which contains the damage but does not remove the cause.
- **A Director started with no `--instance` flag, on a machine with more than one named instance, blocks
  on the picker.** Observed twice in the rig, and it is what made the first swap attempt roll back. This
  matters to the update path specifically, because the launcher starts the Director with no instance flag:
  on such a machine every update would roll back. Pre-existing, not introduced here, and worth its own
  issue.
- **An unknown `--instance <slug>` silently becomes the default instance** rather than failing. That is
  how two processes came to claim one home in the first rig attempt.
- **`GET /healthz` survives this phase.** It is no longer lifecycle - its only caller is the port's own
  self-probe - and it goes with the port in Phase 5.
- Historical documents (release reports, archived plans, past proof records) still mention `POST /shutdown`
  as it was at the time. Those are records rather than instructions and were deliberately left alone; the
  instruction-carrying ones were all corrected.
