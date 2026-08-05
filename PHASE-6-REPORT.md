# Phase 6 report - delete the launcher's listener (with phase 7's guard folded in)

Manager session f5c7ebf9, branch `mission/remove-network-port-p6`, worktree
`D:\ReposFred\devthrottle-noport-p6`. Commits: `f2c022e06` (the deletion and the guard),
`f5d8c7791` (a test-race fix found by the gate), plus this report. Written 2026-08-04.

The claim this phase proves: **the launcher listens on nothing, and everything that used to reach it
over its port still works - commands down the stream it opens to the Gateway, lifecycle by named
signal, and health by the registration file the running process writes.**

---

## 1. Scope: what the brief said, and the scope the Architect's sizing missed

The brief was right that six of nine routes already had a working non-HTTP path: the launcher already
dispatched `director/start`, `director/stop`, `director/restart`, `launch`, `apps` and `files` from
its stream command handler. Deleting the web host and confirming the Gateway dispatches over the hub
was, as briefed, the visible work.

**What the sizing missed - recorded in the Architect's own words, because the lesson generalises:**
the Architect checked which routes had moved to the hub and never checked who OUTSIDE the product
still called the ones that had not. Knowing a route is unused inside the product says nothing about
who calls it from outside it. Three external callers certified their work over the launcher's
`/healthz`:

- **The installer's readiness wait** (`LauncherTrayInstaller`, `LauncherMacInstaller` via
  `LauncherHealthProbe`) - the identity-verified certification from issues #2042 and #1152.
- **The self-update helper** (`Program.ApplyUpdate`) - its healthy/rollback decision was a 200 from
  the port.
- **The uninstaller's stopper** (`LauncherStopper`) - its verdict included "is the port free" and
  "who answers on it".

None of these could survive the deletion unchanged, and all three are certification paths - the kind
that fail loud on a user's machine, not in a test run. That is a mistake anyone doing a deletion will
make again: enumerate the callers of a route from OUTSIDE the product before calling it unused.

## 2. What changed

**The Gateway's second door is deleted, not switched off.** `LauncherLifecycleRelay` had two dispatch
arms - the stream, then an HTTP relay that dialed the launcher's stored address, port and bearer
token whenever the stream was absent. The REST arm, `BuildLauncherClient`, and the
`NoToken`/`Unreachable` outcomes are gone. No stream now means a typed refusal: 404 when the calling
tenant never registered that machine (unchanged, and still deliberately indistinguishable from
"another tenant's machine", so nobody can enumerate), and a new 502 that says the launcher is
registered but not connected and that the connection it opens is the only path.

**The launcher's web host is deleted.** `LauncherHost` (Kestrel on 127.0.0.1:7900) and `LauncherAuth`
(the bearer token and its file) no longer exist. The `--port` option, the port-keyed single-instance
mutex (now keyed to the storage root, the same scoping as the lifecycle signals), and the port in the
tray display are gone with them. The stream client's `StreamMode` gate is removed - `IsEnabled` is
now "a Gateway is configured", because an off switch on the only command path would be an off switch
for cross-machine lifecycle itself. This mirrors what phase 5 did to the Director's stream client.

**Nothing carries a dial-back any more.** `LauncherRegistrationRequest`, `LauncherDto`,
`LauncherStreamHello` and `LauncherRegistry` all lose port, token and network address. This is the
same argument the Architect endorsed for `CC_DIRECTOR_API` in phase 5: a stored address and
credential for a surface that no longer exists is a live-looking dead door, and removing it is the
only way to know nothing still depends on it. A shape test pins the listing JSON against the fields
returning.

**`launcher.json` changed meaning: discovery file to registration file.** It was `{port, token, pid}`
so callers could dial in. It is now the launcher twin of the Director's phase-4 instance
registration - `{pid, version, startedAtUtc, userInterface, autostartOk, autostartRegistered,
autostartFailure}` - written on startup, rewritten when the autostart state changes (the
managed-versus-orphan visibility that used to live on `/healthz`, preserved), deleted on clean stop.
Liveness is the pid in it being alive, never the file alone, so a crash leftover cannot pass for a
running launcher - which is stricter than the old file, whose stale port lingered after a crash.

**The three external callers moved to the registration file, keeping their identity rules:**

- The installer polls the file and certifies only when it names THE PROCESS THE INSTALLER STARTED,
  alive, version as a second signal - the same rules that caught the Mac orphan, because a
  pre-existing launcher's registration file is exactly as valid-looking as its health answer was. A
  registration naming a DEAD pid is refused even when it is the right pid, a case the port could
  never produce.
- The self-update helper's health check pins the pid it itself most recently relaunched. That is
  stronger than the old check (any 200 passed), and pinning the pid rather than the version keeps the
  ROLLBACK wait honest: after a rollback the old build is the one relaunched, and a version pin would
  call a healthy rollback dead.
- The stopper's verdict is the process scan alone. One deliberate strictness change: an unreadable
  process list is now "cannot confirm - NOT stopped" in every case, where the old code accepted a
  free port as corroboration. There is no port left to corroborate with, and unknowable must not read
  as done - the caller uses this verdict to decide whether deleting the launcher's files is safe.

**Readers of the launcher fact updated:** `UpdateStatusFold` gates "install it now" on a RUNNING
launcher (pid alive) instead of a port in the file; `LauncherFactDto` in the facts document carries
`running`/`version` instead of `port`.

**Content:** the move-session skill (both copies - the Gateway-served body and `.claude/skills`) no
longer tells agents to POST to port 7900 with the launcher token; it names the Gateway machine route
and says why there is no port.

## 3. The listener guard (phase 7, folded in)

`NoListenerDependencyGuardTests` in `CcDirector.Gateway.UnitTests` (the unlocked project, so it runs
in the default gate). A DEPENDENCY assertion, never a text scan, at two levels for each of
`CcDirector.ControlApi` and the launcher:

1. **Project level** - the `.csproj` carries no `Microsoft.AspNetCore.App` framework reference.
2. **Assembly level** - the built assembly and its whole `CcDirector.*` reference closure (walked
   with Cecil off the DLL metadata, nothing loaded) name no hosting assembly: exact
   `Microsoft.AspNetCore`, `Microsoft.AspNetCore.Hosting*`, `Microsoft.AspNetCore.Server.*`.

The line is LISTEN versus CONNECT, and it is stated in the guard's own comment: the SignalR CLIENT
assemblies pass because dialing out is the entire architecture the mission moved these components to;
the hosting and Kestrel assemblies fail because they are the capability to listen. The guard also
carries a standing detector validation - it asserts the SignalR client IS in the closure, so a walk
that silently saw nothing cannot pass vacuously.

**Validated the hard way before being trusted.** A listener was reintroduced INDIRECTLY - the
framework reference restored, and a Kestrel `WebApplication` built inside an innocuously named helper
in its own file, referenced by nothing, with no listener text at any call site. That is the shape
that defeated the previous source-text guard. Both levels went red for the launcher
(`The_project_file_carries_no_hosting_framework_reference` and
`The_built_assembly_and_its_whole_closure_reference_no_listen_surface`), while the ControlApi rows
stayed green - the guard localises the offender. Reverted; 4 of 4 green.

**The guard caught a real leftover on its first run:** `CcDirector.ControlApi.csproj` still carried
the `Microsoft.AspNetCore.App` framework reference. Phase 5's deletion was complete in code - the
project builds clean without it, and the assembly-level walk was already clean - but the switch that
makes the hosting surface compilable had been left on. Removed in this phase.

## 4. Runtime proof - the rig

All from an isolated root (`CC_DIRECTOR_ROOT` redirected), a rig Gateway on port 7899 with
`CC_GATEWAY_NO_TAILSCALE=1`, and binaries from a from-scratch build of this branch (all 66 `obj`/`bin`
directories deleted first - the stale-assembly hazard). Identity was verified before anything was
trusted: the rig Gateway's `/healthz` reported version `1.9.7+f2c022e06...` and the launcher's
registration file the same - the phase commit, by SHA.

**Proof A - the connection scan, owning process resolved (the proof the QA requirements demand,
because a port-number check proves nothing).** The launcher's pid was read from the registration the
running process wrote (90060, path resolved to this branch's `cc-launcher.exe`), then every TCP row
on the machine was filtered to that owning process:

```
--- LISTEN-state TCP rows owned by the launcher pid ---
NONE - the launcher owns no listening socket

--- ALL TCP rows owned by the launcher pid (any state) ---
      State LocalAddress LocalPort RemoteAddress RemotePort
      ----- ------------ --------- ------------- ----------
      Bound 0.0.0.0          59311 0.0.0.0                0
      Bound 0.0.0.0          49357 0.0.0.0                0
Established 127.0.0.1        59311 127.0.0.1           7899
Established 127.0.0.1        49357 127.0.0.1           7899

--- who owns the rig gateway's 7899 listener (context) ---
0.0.0.0:7899 owned by pid 41352 (devthrottle-gateway)
```

The launcher was demonstrably RUNNING and FUNCTIONAL while owning zero listeners - its only sockets
are the two outbound connections it opened to the Gateway (the command stream and the registration
client; `Bound` rows are those sockets' local ephemeral ports, not listeners). This is the
distinction the requirements document draws: not "the port is free" but "our live process would not
have listened".

**Proof B - starting, stopping and restarting a Director through the Gateway, over the hub.** The
deleted routes' whole purpose, exercised end to end with observed process effects, never trusting a
status code alone:

- `POST /machines/SOREN_NORTH/director/start` -> 200, payload `{"ok":true,"via":"stream"}` - the
  response itself names the transport. The Director's instance registration appeared in the rig root
  naming pid 71428, process alive, image the rig's own `app\cc-director.exe`.
- `POST .../director/stop` -> 200 via stream; pid 71428 exited (gracefully - the named-signal path,
  no force-kill logged).
- `POST .../director/restart` -> 200 via stream; a NEW pid (4156) registered and alive; stopped
  cleanly afterwards.

On "from another machine": the rig's caller and launcher shared one machine. What makes the result
carry across machines is stated in section 6 as an argument, not silently assumed.

**Proof C - the refusal is loud, and nothing dials.** Two states, both exercised live:

- After the launcher was asked to quit by its named lifecycle signal
  (`Local\cc-director-launcher-shutdown-<rootkey>` raised from outside the process), it exited,
  deleted its registration file, and unregistered from the Gateway. `director/start` then answered
  404 `no launcher registered for machine 'SOREN_NORTH'` - the honest answer for that state.
- A registry row was then re-created WITHOUT a stream behind it (the crash shape - a launcher that
  heartbeated and died). `director/start` answered **502**: "the launcher on 'SOREN_NORTH' is
  registered but not connected to this Gateway ... Commands reach a launcher only over the connection
  it opens to the Gateway". No dial was attempted; there is no address in the system to dial.

Teardown: Director and launcher already stopped gracefully above; rig Gateway stopped via its own
`/shutdown`; both scheduled tasks unregistered; zero surviving processes verified by image path; rig
directory removed.

## 5. The gate, judged comparatively

Parent (`4a2e6665e`, phase 5 complete) run twice in its own clean control worktree; mission arm run
twice from a fully cleaned build. Default run, nine projects:

| Run | Result | Failures |
|---|---|---|
| Mission 1 (`f2c022e06`) | 2 failed / ~4,195 | `TenantScopedSweepTests.Hosted_EmptyCensus_RunsTheBodyZeroTimes` (FileLog race signature); `DirectorInstanceLocatorTests.AClaimantWhoseImageCannotBeRead_ForcesARefusal` (see below) |
| Parent 1 | green | - |
| Parent 2 | 2 failed | `TenantSettingsResolverTests.DailyReportCadence_...` and `WingmanVoiceServiceTests.StoreSpokenAsync_...` - both the FileLog race signature |
| Mission 2 (`f5d8c7791`) | 1 failed | `SkillPlacementStoreTests.A_fixed_machine_stops_being_reported_as_broken` - the FileLog race signature |

Every failure on either arm except one carries the single documented signature -
`System.InvalidOperationException: The collection has been marked as complete with regards to
additions` (the `FileLogWriter` teardown race the mission has already filed) - with five DISTINCT
victims and zero repeats, on both arms. Under the mission's comparative criterion, none is this
phase's.

**The one exception was real and is fixed** (`f5d8c7791`): the locator test's `ForeignProcess`
constructor waited only for a NON-EMPTY main-module answer before asserting it equalled the started
executable, and Windows transiently reports `ntdll.dll` for a process still initializing - non-empty
and wrong. The observed failure message ("Expected cmd.exe / Actual ntdll.dll") IS the mechanism, so
this was fixed on evidence rather than parent-attributed: the wait now waits for the answer to BE the
expected image, and compares case-insensitively (the same run showed `system32` versus `SYSTEM32`).
The failure was in phase 4's test, pre-existing; my arm was simply the one that caught it.

**Parked suites on the mission commit:**

- `CcDirector.Gateway.Tests` (full, under its machine-wide lock): 2,203 passed, 47 skipped, **2
  failed - and both are DETERMINISTIC and INHERITED, not flaky and not this phase's.** Each failed 3
  of 3 isolated reruns on the mission commit AND failed identically on the parent:
  - `GatewayDirectoryRegistrationTests.Register_rejects_missing_tailnet_endpoint` - expects 400 for
    a Director registration with no tailnet endpoint, gets **201**. The Gateway now accepts an
    endpointless registration, which is plausibly phase 5's own intent (a portless Director HAS no
    endpoint to register) with the test not updated - but that is a hypothesis for the Architect to
    settle in phase 5's context, not this phase's to guess at.
  - `WingmanAskForwardingTests.Ask_no_claude_returns_no_claude_status_with_context_digest` - the
    route the test drives answers **HTTP 404** (WingmanAskForwardingTests.cs line 92).
  Handed to the Architect by name. This phase changes nothing they touch.
- `CcDirector.Core.Tests` (full, 4,215 tests, ~20 minutes per run): run once on the phase commit -
  4,205 passed, 2 failed, both in `NoCrossMachineLoopbackGuardTests`, and both were the guard doing
  its job: four allowlist entries had gone stale because THIS PHASE removed the loopback they
  documented (`LauncherHost.cs` deleted; `LauncherLifecycleRelay.cs`, `MachineEndpoints.cs` and the
  launcher's `Program.cs` no longer carry any loopback literal). Allowlist updated with the phase 6
  note - the list shrinking is the mission's progress made visible in that guard. Run again in full
  after that fix - 4,206 passed, 1 failed: `PrintBanAuditTests.No_in_scope_side_call_emits_print_or_p`,
  flagging `DirectorInstanceLocator.cs`. That is an INHERITED false positive, not this phase's and
  not a real ban violation: the flagged `-p` is `/bin/ps -o comm= -p <pid>` in the locator's Unix
  arm (phase 4/5 work, file untouched by this phase) - ps's pid selector, not a claude one-shot.
  Fixed by the audit's own prescribed protocol: an `ExemptFiles` entry naming the mechanism.
  Both audit classes rerun green after their fixes. The full suite was not run a third time: both
  failures were deterministic audits over static source, their causes are visible in the flagged
  lines themselves, and the fixes are data entries in the two audit files - nothing the other 4,206
  passing tests consume.

The launcher-facing suites specifically: `CcDirector.Launcher.Tests` 106/106 on both target
frameworks (down from 114: the eight deleted tests are `LauncherAuthTests`, which tested the bearer
gate on routes that no longer exist); setup engine 454/454; the rewritten Gateway launcher/machine
classes 136/136.

## 6. What is NOT proven, stated plainly

- **No run on macOS or Linux.** The registration-file probe, the root-keyed mutex, and the stream
  path all compiled and unit-tested for both target frameworks, but nothing executed on a Mac. Phase
  4 proved on this exact surface that a green shared suite can hide a completely inert platform
  mechanism. The pieces this phase touched are less platform-split than phase 4's (no kernel-object
  arm - files and sockets on both platforms), but that is an argument, and phase 4 also proved
  arguments are not runs.
- **"From another machine" was proven as a code path, not as two physical machines.** The claim that
  carries it across machines: the launcher DIALS OUT to the Gateway and the command rides back down
  that connection, so the caller's location changes only which machine dials the Gateway's own HTTP
  interface; and the deleted code was the only place a machine-local shortcut (loopback dial-back)
  existed. The hosted tenant tests drive the same routes through real device keys and real stream
  connections. A two-machine run remains the stronger proof and was not performed.
- **Mixed-version fleets degrade until the launcher updates.** An OLD launcher against the NEW
  Gateway registers fine (its extra port/token fields are ignored) but is only commandable while its
  stream is up - and old launchers only open the stream when `streamMode` was on. A NEW launcher
  against an OLD Gateway has its registration REFUSED (the old validation requires port and token) -
  it retries harmlessly and its stream still connects, but the listing will not show it until the
  Gateway updates. The mission has shipped in lockstep on this branch throughout; stating the window
  is honest, testing it was not done.
- **The generated web API schema is stale.** `packages/client-core/src/api/schema.ts` (generated by
  `openapi-typescript` from a running Gateway) still describes the old `/launchers` shapes. No web
  client consumes those routes - verified by search - so nothing breaks; regenerating requires a
  running Gateway on port 7878 (`npm run gen:api`) and is left for the Architect to fold into the
  mission's landing.
- **The clean-machine first-launch wizard proof belongs to phase 5** (the Director's port-probe code
  was what raised the Windows popup) and was not re-run here. The launcher only ever bound loopback,
  which does not raise that dialog.

## 7. Incidents, reported against myself

- **The rig launcher overwrote the owner's autostart Run key.** I launched the rig launcher without
  `--no-autostart`, and the launcher does what it is designed to do on startup: it registered the
  `CcDirectorLauncher` Run key - pointing at my development build in this worktree. Caught within
  minutes by reading the registry, restored to the exact command line the product writes for the
  installed launcher (`"...\cc-director\launcher\cc-launcher.exe" --managed`, per
  `LauncherAutostart.CommandLine`), and the installed launcher re-asserts this value on every start,
  so the repair is also self-healing. The lesson is mechanical: **a launcher rig must pass
  `--no-autostart`; the flag exists for exactly this.** Had it gone unnoticed, the owner's next login
  would have started a development build instead of his installed launcher.
- **A behavioural strictness change is buried in a caller that trusted the port**, restated here so
  it is reviewed rather than discovered: the uninstaller's stopper now refuses to claim success when
  the process list is unreadable, in every case (see section 2). The old code said "stopped" if the
  port happened to be free. Fail-closed is correct for a verdict that gates file deletion, but it is
  a change in what an uninstall reports on a machine where `ps` fails.

## 8. For the mission QA report

- The launcher's port 7900 is gone the same way the Director's went: nothing listens, nothing can
  listen (the dependency guard), and the one path in is a connection the launcher itself opens.
- The FileLog teardown race collected four more occurrences across these runs (four new distinct
  victims, both arms) - further confirmation for the already-filed fleet finding that every merge
  currently rides on a signal this race corrupts.
- Two deterministic inherited reds in the parked Gateway suite are named in section 5 and are open.
- The guard's first run catching ControlApi's leftover framework reference is the guard's value
  demonstrated before it ever guarded anything: the capability switch survives the code that used it.
