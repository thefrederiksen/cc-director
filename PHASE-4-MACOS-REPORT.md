# Phase 4 on macOS - the same proofs, and the defect they found

Prover: session 4bfcab29 "Remove Network Port - Mac Prover", machine Sorens-Mac-mini.
Worktree `devthrottle-noport-mac`, cut from `origin/mission/remove-network-port` at `502293c3`.
Reporting to Architect bc291ea4. Nothing was merged and nothing was pushed.

The brief was exact: Phase 4 replaced a lifecycle path that WORKED on macOS over HTTP with a named
signal whose Unix arm had never been executed, so run the Windows report's four proofs here and expect
the defects to be in that arm. That expectation was correct, and the defect is a single, precise one.

---

## The verdict in one paragraph

**The thinking parts of Phase 4 all work on macOS. The delivery part is broken in every direction that
matters, by one line.** The locator resolves the right Director among several, the session count comes
off the roster, a busy Director holds the update, an idle one is updated with the version certified
from the new process's registration, a dead build rolls back to the previous one, and a crashed
Director is restarted by the launcher - all of it proven here with no Gateway configured anywhere. But
every signal that crosses the launcher-Director boundary is silently lost, because the Unix request
file's PATH is derived from each process's own redirected storage root while the signal's NAME is
correctly derived from the shared root. The launcher and the Director literally write and poll in two
different directories. On Windows the name is the whole address, so Windows never sees this. On macOS
it means: every stop of the Director is a 20-second stall followed by a force-kill and a phantom
crash-journal entry, every update applies by force-kill, and the "install it now" button tells the
user the install is happening while nothing will ever happen.

---

## The defect, localized to the line

`LifecycleSignal.UnixRequestPath` resolves the request file as
`CcStorage.ToolConfig("lifecycle-signals")`, and `CcStorage` follows the calling process's
`CC_DIRECTOR_ROOT`. A real Director redirects that variable to its own instance home at startup
(`Program.cs` line 196: `CC_DIRECTOR_ROOT = InstanceContext.InstanceHome`); the launcher never
redirects. So:

- The Director LISTENS in `<sharedRoot>/instances/default/config/lifecycle-signals/`.
- The launcher RAISES into `<sharedRoot>/config/lifecycle-signals/`.

Same name, different directories, in both directions. The design already contains its own correction:
`LifecycleSignalNames.RootKey` deliberately uses `InstanceContext.SharedRoot` - captured BEFORE the
redirect - and its comment explains exactly why per-process `CcStorage` is the wrong value for scoping
a launcher-Director signal. The NAME got that reasoning; the PATH did not. The fix direction is to
derive the Unix request directory from the same shared root the name is derived from. I have not made
that change: I was seated to prove, and a fix must carry the cross-process test this defect proves the
suite lacks.

**Witnessed, not inferred** (rig transcript, no Gateway configured in any rig root):

```
[DirectorSupervisor] StopAsync: asked 05281140-... (pid=31557) to shut down
    via cc-director-shutdown-05281140-...; delivered=True
[DirectorSupervisor] StopAsync: pid=31557 was asked to shut down and was still running 20s later.
    Force-killing it; expect a phantom crash-journal entry...
```

The request file sat, and still sits, in `<sharedRoot>/config/lifecycle-signals/` - the Director was
polling its instance home. The Director's log shows no shutdown request ever arriving. Its crash
journal was left behind. `delivered=True` is the Unix arm keeping its documented contract ("handed
over, never carried out"), which is why nothing anywhere notices.

**The reverse direction is worse because it is user-facing.** The production `LauncherRestartClient`,
called exactly as a real Director calls it (shared root captured, then redirected, then raised):

```
ok=True  message=Installing the update now - the Director will restart.
raise wrote into: <sharedRoot>/instances/default/config/lifecycle-signals
```

The launcher polls `<sharedRoot>/config/lifecycle-signals` and will never see it. The person was told
the install is happening. Nothing will happen, and nothing will ever say so.

**The mechanism itself is exonerated.** With sender and listener at the SAME root, delivery works: the
launcher quit cleanly on its shutdown signal (`LauncherStopper`'s shape - both processes un-redirected),
and a Director asked to stop via a request written into its own instance home exits cleanly, deletes
its crash journal and removes its registration. The defect is purely the root divergence.

Affected pairs: launcher-to-Director shutdown (LOST - force-kill instead), launcher-to-Director update
check (LOST - same path pair), Director-to-launcher restart / "install it now" (LOST - silent
user-facing failure). Unaffected: launcher shutdown and launcher restart raised by the setup engine or
scripts running at the shared root (proven working); everything on Windows.

---

## The four proofs, run as on Windows

Everything drove the production classes - `DirectorInstanceLocator`, `DirectorSupervisor`,
`DirectorUpdateOwner`, `LifecycleSignal`, `InstanceRegistration`, `DirectorCrashJournal` - against
throwaway storage roots; the harness (`phase4-mac-rig/` in this worktree, not committed to the
mission's shipped code) only selects a verb. The Director endpoints in the rigs were real processes
launched through the production start path (`/usr/bin/open` on an application bundle shaped exactly
like the shipped one: `launch` stub plus `cc-director` payload). No Gateway was configured in any rig
root. One honesty caveat is recorded at the end: the rig Directors were production-faithful stand-ins
that perform the real registration, roster, listener and clean-shutdown sequence, not the full Avalonia
application.

### Rig A - liveness resolves the RIGHT Director, and its absence, with two running

Two live Directors from ONE executable (instance `default` pid 28069, instance `work` pid 28070):

```
outcome=Running
  candidate: directorId=e48d6995-... pid=28069 version=1.9.7 ...
status: directorId=e48d6995-... pid=28069 version=1.9.7 sessions=0
```

One candidate, the supervised one, named by identifier. Then the default Director was stopped CLEANLY
(request written into its own instance home - which is also the proof the Unix listener works when the
path is right: `shutdown requested by lifecycle signal`, journal deleted, registration removed) while
`work` kept running:

```
outcome=NotRunning   isRunning=False     (work, pid 28070, still alive)
```

The old name scan would have matched the survivor. The start-time identity check and the
session-count-from-roster read (a real roster entry, `sessions=1` read back by the launcher with no
network) both behaved as the Windows report describes.

### Rig C - the update: held when busy, applied when idle, certified from the registration

**Held.** Staged 1.9.8, one session in the roster: `decision=HeldBecauseBusy`, and the installed
bundle's payload hash was byte-identical before and after. No session interrupted.

**Applied.** Roster drained to zero, same staged build: the launcher stopped the Director (by
force-kill after the futile 20-second wait - the defect's tax on every macOS update), swapped the
bundle (`ditto`, backup kept at `Director.app.prev`), started the new build through `/usr/bin/open`,
and certified `after: version=1.9.8 pid=29995` - read from the registration the NEW process wrote.

**Rolled back.** Staged a build that exits before registering. The executable on disk said the new
version for the whole 45-second health wait and the launcher never believed it - which is the
discriminating half of "version from the registration, not the disk" - then:
`decision=RolledBack, after: version=1.9.8 pid=33458`. The previous build was restored from the
backup and is running.

### Rig B - the launcher restarts a Director that crashed

Director force-killed (a crash, not a stop). Locator: `NotRunning`. The launcher-restart signal was
raised by a SAME-ROOT sender (the shape the setup engine and scripts use - the in-Director shape is
the broken direction recorded above): the listening launcher ran the production `RestartAsync`, and a
new Director came up - `pid=33724 version=1.9.8`, certified from its registration. The launcher then
quit cleanly on its own shutdown signal.

---

## The unit suites, on their first Unix execution ever

Clean `obj`/`bin` before every run. `CcDirector.Launcher.Tests`: 112 of 113 green.
`LifecycleSignal` tests: 3 to 4 failures depending on the run. Each failure diagnosed, not just
counted:

1. **`ARaisedSignal_ReachesItsListener` fails in the suite, passes alone (4 milliseconds).** Parallel
   tests in the same assembly mutate the process-wide `CC_DIRECTOR_ROOT`, so the listener's directory
   and the raiser's directory diverge inside one process - the SAME root-divergence as the production
   defect, reproduced by the test host. The suite cannot see the real defect (it never crosses a
   process boundary) but it trips over the same property by accident.
2. **`EachRaise_RunsTheHandlerExactlyOnce` fails alone, deterministically.** Two raises inside one
   500-millisecond poll window overwrite the same request file: Unix raises COALESCE. The class
   comment promises "invoked once per raise"; the Unix arm cannot honour that, only
   at-least-once-per-window. For verbs that are idempotent ("shut down") this is survivable, but it is
   an undocumented contract narrowing, and `AHandlerThatThrows_DoesNotStopTheListener` fails or passes
   on poll-phase luck for the same reason (fails in suite, passed alone at 508 milliseconds).
3. **`LifecycleSignalNamesTests.TheSameRoot_AlwaysGetsTheSameName` fails on macOS.** Test artifact:
   it feeds Windows backslash paths, and `RootKey`'s trailing-separator normalization is
   platform-correct, so `D:\rig\cc-director` and `D:\rig\cc-director\` hash differently on Unix.
4. **`DirectorInstanceLocatorTests.TwoClaimantsRunningDIFFERENTImages_ResolveToTheInstalledOne...`
   fails on macOS.** Two causes, one each: the test's foreign process is `/bin/sh -c "sleep 60"`,
   which on macOS EXECS into `/bin/sleep`, so the "installed" image the test pinned stops being what
   the process runs; and underneath it a product-level fact - `ps -o comm=` on macOS returns argv[0]
   as invoked (a full path only when launched by full path, a bare name when resolved via PATH), not
   a kernel-verified image path. The tie-break therefore fails CLOSED (`Ambiguous`) for any claimant
   not started by absolute path. The production start path (`open` via launchd) does launch by full
   path, so the shipped flow tie-breaks correctly; a Director started from a terminal by name would
   be refused. Availability cost, not a wrong-target risk.

I did not run the full comparative gate on macOS. The mission already records roughly sixteen
pre-existing macOS-only failures in `CcDirector.Core.Tests`, and a two-arm parent-versus-mission gate
on this platform is its own piece of work; the targeted suites above are what this phase's claims rest
on.

---

## Findings that are not the headline defect

- **Rig isolation is structurally harder on macOS, and future provers must know it.**
  `InstallLayout.PathFor(Director)` is `~/Applications/Director.app`, resolved from HOME and NOT from
  `CC_DIRECTOR_ROOT`. A rig that only redirects the storage root aims the production supervisor and
  update owner at the OWNER'S REAL INSTALL - on this machine, the Director hosting the session that
  wrote this report. Every rig process here therefore ran with both HOME and `CC_DIRECTOR_ROOT`
  redirected. Also: `Environment.SpecialFolder.LocalApplicationData` does NOT follow a changed HOME in
  .NET on macOS, so `CC_DIRECTOR_ROOT` must always be set explicitly for rig processes.
- **The bundle swap machinery judges presence by a hardcoded name.** `DirectorBuildSwapper.Inspect`
  counts a bundle present only if `Contents/MacOS/cc-director` exists inside it, and
  `PrepareBundleToRun` chmods that same hardcoded path (a failure there is tolerated with only a log
  line). Correct for the shipped bundle; brittle against any future rename, and it cost this rig a
  wrong first rollback verdict until the rig bundle was reshaped to match production. Recorded so the
  next person does not re-derive it.
- **A force-killed Director leaves its crash journal, so the defect manufactures phantom
  "interrupted" entries.** Not new information in itself - the force-kill branch says so in its own
  log line - but on macOS today that branch is not the fallback, it is EVERY launcher-initiated stop.

## What I did not prove, said plainly

- The rig Directors were production-faithful stand-ins (real registration, real roster, real
  listeners, real clean-shutdown sequence, launched through the real `open` path in real bundles),
  not the full Avalonia Director. The full application's listener wiring (`App.axaml.cs
  StartLifecycleSignals`) is the same production `LifecycleSignal.Listen` call the stand-in makes,
  and the path it computes is decided by the `CC_DIRECTOR_ROOT` redirect that `Program.cs`
  demonstrably performs - but a full-application end-to-end run on macOS has still never happened,
  and cannot honestly happen before the path defect is fixed, because its lifecycle would fail
  exactly as proven above.
- No real coding-agent session was created inside a rig Director; the roster entries were written by
  the same production `DirectorCrashJournal.Update` call the real session manager uses.
- The launcher process was the production `DirectorSupervisor`/`LauncherCore` listener wiring driven
  from the harness, not the shipped launcher application - which on this platform `InstallLayout`
  itself declares "never placed on mac", so there is no shipped macOS launcher to run; that
  contradiction between the layout's comment and this phase's macOS ambitions is worth one look from
  the Architect.

## Disposition, as first reported

Phase 4 on macOS was NOT shippable as found, and the reason was narrow: one path expression in
`LifecycleSignal.UnixRequestPath` disagreed with the root the signal names are already correctly
scoped by. Everything above it - locator, roster, hold, apply, certify, rollback, restart - was proven
working on this platform. The Architect then reassigned this seat from prover to fixer, on the
reasoning that a prover is not the independent inspector, so nothing is compromised by it building -
and this is the only seat that can verify the fix, because the defect is invisible on Windows by
construction. Everything below this line is the fix and its proof.

---

# The fix, and the detector that was missing

## Why Windows could never have caught this, stated as the Architect asked

The Windows arm addresses a kernel object by NAME alone: `EventWaitHandle` with a `Local\` prefix, one
namespace per logon session. The two processes never have to agree on a FILE PATH at all, because no
file exists. The Unix arm is the only place where agreeing on a path is load-bearing - the path IS the
namespace - which is exactly why a platform-shared test suite ran entirely green while one platform's
delivery mechanism was completely inert. No amount of Windows testing, at any thoroughness, could have
surfaced this defect. It could only ever be found by executing the Unix arm across a real process
boundary, and until this seat ran, nothing ever had.

## The one-expression fix

`LifecycleSignal.UnixRequestPath` now derives the request-file directory from
`InstanceContext.SharedRoot` - the machine-wide root captured BEFORE a Director redirects its data
tree - instead of from per-process `CcStorage`:

```
config/lifecycle-signals under InstanceContext.SharedRoot   (was: CcStorage.ToolConfig)
```

That is the same value `LifecycleSignalNames.RootKey` already derives the signal NAME from, for the
same documented reason; the path now agrees with the name. The per-root scoping property is unchanged:
a test rig with its own root and the real install still resolve different directories and can never
signal each other. The Windows arm is untouched by construction - the changed expression is reachable
only from `RaiseUnix` and the Unix listener, both of which sit behind `OperatingSystem.IsWindows()`.

## The missing cross-process test - what each piece covers, per platform

Three detectors were added, and each was proven RED with the old expression injected and GREEN with
the fix (all three failed under the fault - the two cross-process ones by delivery timeout, the
derivation pin in 2 milliseconds - and all three pass with the fix restored):

1. **`LifecycleSignalCrossProcessTests` - two real processes, both directions.** A probe executable
   (`CcDirector.Core.Tests.SignalProbe`, test-tree only, never shipped) runs the production
   `LifecycleSignal` under a controlled environment; the test spawns a listener child and a raiser
   child, one of them redirected exactly the way a real Director's `Program.Main` redirects, and
   asserts the raise is heard. One test per direction: launcher-to-Director (the stop that was
   becoming a force-kill) and Director-to-launcher (the "install it now" that was going nowhere). The
   parent test process mutates no environment of its own, so these are immune to the suite's
   parallel-test environment races.
   - On macOS and Linux: fails without the fix, passes with it - the real detector.
   - On Windows: cannot detect a path regression (no path exists); still proves genuine cross-process
     delivery through the kernel event, which no other test does. Stated in the class comment so a
     Windows pass is never mistaken for path coverage.
2. **`LifecycleSignalRequestPathTests` - the derivation pinned as a value.** Asserts that after the
   production redirect the request path still resolves under the SHARED root. This is the only
   detector that is meaningful on WINDOWS: the expression computes everywhere, so a future regression
   fails every platform's suite rather than only the platform that suffers it - closing the
   invisibility that let this ship.
3. The class comment on `UnixRequestPath` itself now carries the full reasoning, including the
   Windows-invisibility argument, so the next person changing it meets the failure story at the line.

**A test defect found while building the test, recorded because it cost an hour and will bite again:**
`Assert.True(condition, message)` evaluates its message argument EAGERLY, even when the condition is
true. The first version of the cross-process test interpolated `listener.StandardError.ReadToEnd()`
into that message - a call that blocks until the listener process exits - so a correctly-working
mechanism looked undelivered for exactly the listener's lifetime. The tests now build failure messages
only on failure, and carry a millisecond timeline of every step in every failure message so the next
diagnosis starts from evidence rather than guesses.

## The four proofs, re-run on the fixed build - same rigs, same verbs, no Gateway

| Proof | Before the fix | After the fix |
|---|---|---|
| Launcher stops the Director | 20,043 ms, force-kill, phantom crash journal left | **385 ms, clean exit, journal deleted** |
| "Install it now" reaches the launcher | Never arrives; user told it is installing | **Arrives; production `RestartAsync` runs** |
| Update applied when idle | Applied, but by force-kill, 22 s | **Applied in 2 s, stop inside is graceful, 1.9.8 certified from the new process's registration** |
| Update held when busy | HeldBecauseBusy (already worked) | HeldBecauseBusy, bundle byte-identical |
| Dead build rolls back | RolledBack (already worked, given a faithful bundle) | RolledBack, restored 1.9.8 running |
| Liveness among several | Right Director resolved (already worked) | Right Director resolved; NotRunning after a now-graceful stop while the other survives |
| Crash restart | Worked from a same-root sender only | Works; new Director certified from its registration; launcher quits cleanly on its own signal |

One rig-side consequence worth knowing: with the fix, a signal can no longer be delivered by writing
into a Director's instance home - listeners live at the shared root now. The old pre-fix rig trick of
raising "into the instance home" is dead, correctly.

## Suite status on macOS after the fix

`LifecycleSignal` filter: 13 of 16 green. My three additions all pass; additionally
`ARaisedSignal_ReachesItsListener`, which failed in-suite before, now passes - deriving the path from
the statically captured shared root also made it immune to the parallel tests that mutate the
process-wide root mid-run. The three remaining failures are the PRE-EXISTING findings recorded above,
deliberately not folded into this fix (the Architect's instruction against quiet widening): the two
Unix raise-coalescing tests and the backslash-path names test. The launcher suite is unchanged at 113
of 114, the one failure being the pre-existing macOS tie-break test, also recorded above.
`LauncherStopperTests` 10 of 10.

## Windows

By construction the fix cannot change Windows behaviour: the modified expression is unreachable there,
and the diff otherwise adds tests and comments. The new tests compile for both platforms and are
platform-honest as described. A Windows execution of `CcDirector.Core.Tests` (the LifecycleSignal
classes), `CcDirector.Launcher.Tests` and `LauncherStopperTests` is still the required confirmation,
and this seat runs on the Mac; that run is being arranged on a Windows seat and its result reported to
the Architect separately. Until it lands, "Windows unregressed" is an argument from construction, not
an observation - said plainly rather than assumed.
