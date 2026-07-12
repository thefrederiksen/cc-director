# Mission Brief: CC Launcher on the Mac (remote launch and mutual updates)

> **STATUS CORRECTION 2026-07-11 (late evening): the mission is ACTIVE again.** The earlier
> mission-wide park was a relay scope error - the owner's instruction ("we're not implementing
> this right now") referred only to the auto-update work stream, not the whole mission. The Mac
> port (pull request 1365) and installer (pull request 1364) streams are resumed. Only the
> mutual-update stream (pull request 1367) is parked, confirmed by the owner verbatim: "I didn't
> park all launcher missions! I parked the auto-update, that's all I parked! We need the launcher
> working, but no auto-update while sessions are running." (His condition - never update while
> sessions are running - is exactly what decision 8 and the DirectorUpdateGuardian on the parked
> branch already enforce.)
>
> The resume-gate list below remains the accurate map of what is left to prove:
> **Resume gates** - what remained unproven at stand-down, in merge order:
> 1. Pull request 1365 (Mac port): the Director-restart-over-the-stream proof leg needs the
>    screen unlocked or the never-lock posture applied; everything else was proven live.
> 2. Pull request 1364 (installer): merges after 1365 (shared launch agent label constant),
>    and the end-to-end clean-install proof needs a release that ships cc-launcher-mac-arm64.
> 3. Pull request 1367 (mutual updates): the live deliberate-bad-build rollback proof never
>    ran; that is the gate before it could be undrafted.
> 4. Fleet-wide: the production Gateway must be updated to a build containing the launcher
>    stream before remote commands work outside a local test Gateway.

Status: active mission (auto-update stream on hold - see the status correction above). Written 2026-07-11 by the Architect session
("CC Launcher Mission - Architect", session 7b78e474, machine Sorens-Mac-mini). The Architect
settles the design and coordinates; the worker sessions listed under "The work" own execution.

## The mission

The fleet must keep itself alive and current on unattended Macs. The Windows tray launcher
(cc-launcher) is the model: an always-on tray application that connects out to the Gateway, can
launch applications on the machine with clean process parentage - most importantly cc-director
itself - and pairs with the Director so each can update the other. The owner wants the same
experience on the Mac: install once, and out of the box the machine has a Director, a launcher, a
tunnel connection, and a two-way update scheme where the launcher updates cc-director and
cc-director updates the launcher, so a fleet of unattended machines stays current even when an
update goes wrong - each binary is the other's rescuer.

## Decisions already made - do not re-litigate

1. **One application, both platforms.** We do not build a separate Mac launcher. The existing
   `src/CcDirector.Launcher` (Avalonia tray application) is multi-targeted to run on macOS; the
   Windows tray behavior is the thing being emulated. Avalonia puts the tray icon in the Mac menu
   bar. Roughly 80 percent of the launcher (Gateway registration, command stream, token
   authentication, web interface, self-update logic) is already platform-neutral.
2. **The Python daemon in `tools/cc-launcher` is retired** once the .NET launcher runs on the Mac.
   The Windows tray launcher is the only launcher we care about.
3. **Never overwrite a running binary in place** - on macOS that is an instant code-signature
   kill. Every swap is rename-based: place the new file beside the target, rename the current
   target aside as `.old`, rename the new one in. Delete the `.old` backup only after the new
   build proves healthy.
4. **A machine never updates both binaries in the same pass.** Update one, confirm it healthy,
   then the other on a later cycle. At least one healthy supervisor exists at every moment.
   Underneath both sits launchd (`RunAtLoad` + `KeepAlive`) as the bottom safety layer.
5. **The persistent stream is the command path on the Mac.** The launcher's web interface stays
   loopback-only; remote commands arrive over the outbound SignalR stream to the Gateway, which
   works with the screen locked. (The Gateway's HTTP relay cannot reach a loopback-bound launcher
   on another machine - that is accepted, not a bug to fix.)
6. **Unattended posture** (owner-accepted, see memory "mac-unattended-posture"): fleet Macs run
   never-lock plus display-off plus automatic login. The launcher runs as a user launch agent in
   the logged-in graphical session, which is what lets it launch graphical applications remotely.
7. Plain English everywhere. No fallback programming. Enterprise logging on every public method.
8. **Restart policy for applying Director updates** (owner ruling, 2026-07-11): the launcher must
   NEVER restart a Director that has any actively working session. If ALL of a Director's
   sessions are idle or waiting, a restart to apply an update is allowed, but only inside a
   nightly maintenance window. Both are configurable settings - window start and end hours, and
   an enable/disable switch for automatic restarts entirely - because the owner expects to tune
   them. When an update is staged but blocked by activity, never force anything: surface a
   "new version waiting" notification to the owner instead. Implement the restart step as a
   policy seam, not hard-coded, because a version 2 is deferred but expected: the owner is torn
   between (a) saving every session as a handover document (the Director already has /handover
   endpoints), updating, then restarting sessions from the handovers, and (b) staying
   notify-only. That decision waits until version 1 shows how often updates actually get
   blocked. Do not build version 2 now.

## Core findings (verified 2026-07-11 against the working tree)

Full discovery and design detail: `docs/plans/cc-launcher-mac-tunnel-and-mutual-updates.md`.
The short version:

- The Gateway side is DONE and tested: launcher registration (`POST /launchers/register`,
  heartbeat, 90-second timeout), relay endpoints (`/machines/{machine}/director/restart` and
  friends), the `LauncherHub` persistent stream with verbs `director/start`, `director/stop`,
  `director/restart`, `launch`, and the protected-slot guard.
- The launcher's Gateway clients (`GatewayRegistrationClient.cs`, `LauncherStreamClient.cs`) are
  written and platform-neutral. The blockers are packaging, not architecture: the project targets
  `net10.0-windows` / `WinExe`, autostart is a registry Run key, `LaunchService` routes through
  `cmd.exe`, `DirectorSupervisor` matches processes via `MainModule`, and the release ships only
  `cc-launcher-win-x64.exe`.
- The Director already self-updates on macOS arm64 (GitHub release poll, sha-256 verification
  against `release-manifest.json`, staged bundle, swap at next startup via `SwapMac`). But
  `SwapMac` keeps no backup, rollback and half-swap recovery return early on non-Windows, and
  nothing applies a staged update on an unattended machine (it waits for "next time you open the
  app", which is never).
- The launcher's managed self-update (stop, swap, relaunch, health check, roll back to `.old`)
  exists and is the right shape - gated `OperatingSystem.IsWindows()`.
- The tunnel is the Tailscale mesh and already works from this Mac: outbound-only connections to
  the Gateway's tailnet address, verified live with the screen-locked constraint in mind.

## The work - three sessions, one mission

### Session 1 - "CC Launcher - Mac Port" (build the launcher for the Mac)

Port `src/CcDirector.Launcher` per the plan document, part 3.1:
- Multi-target `net10.0-windows;net10.0`; publish `osx-arm64` self-contained single-file.
- launchd autostart (`~/Library/LaunchAgents/com.devthrottle.cc-launcher.plist`, `RunAtLoad`,
  `KeepAlive`, `--managed`), the macOS twin of the registry Run key.
- `LaunchService` macOS branch: `/usr/bin/open` for application bundles, fresh process group for
  plain executables, no `cmd.exe`.
- `DirectorSupervisor` macOS branch: resolve the installed `CC Director.app`, start via
  `/usr/bin/open`, graceful stop through the Director Control API (already portable), process
  matching without `MainModule`.
- Prove it end to end on Sorens-Mac-mini: launcher registers with the Gateway, appears in
  `GET /launchers`, and a `director/restart` sent over the stream restarts a test-slot Director
  (slot 5 or higher only - never the owner's running Directors).

### Session 2 - "CC Launcher - Mac Installer" (install everything, out of the box)

The owner installs once on a fresh Mac and it just works: cc-director installed, cc-launcher
installed and autostarted, Gateway connection configured, launch agent loaded. Work:
- Discover the current Mac installer (`devthrottle-setup-mac-arm64.zip`, the setup engine,
  `MacAppPlacer`) and what it covers today.
- Add the launcher as an installed component on macOS: a macOS asset name in the setup-engine
  `Component` record, `InstallLayout` path, placement, launch-agent registration, first start.
- Release pipeline: build and publish `cc-launcher-mac-arm64` in `.github/workflows/release.yml`
  and add it to `release-manifest.json` (the completeness guard must know about it).
- Prove it: a clean install on this Mac ends with both binaries installed, the launcher
  registered with the Gateway, and the Director connected.

### Session 3 - "CC Launcher - Mutual Update" (research, then build, the two-way update)

Research first, then implement the scheme in the plan document, parts 3.2 to 3.4:
- Launcher updates the Director: version comparison from the release manifest, restart to apply
  the already-staged update when the machine is quiet, post-restart health check, rollback via a
  kept `.old` bundle (change `SwapMac` to rename aside instead of delete), bad-version pinning,
  rescue install via `MacAppPlacer` when no Director is runnable.
- Director updates the launcher: un-gate the tool-update loop and `LauncherSelfUpdate` from
  Windows, rename-based swap, start via `launchctl kickstart`, restore `.old` when the launcher's
  health endpoint stays dead.
- Startup recovery on macOS (the port of `RecoverHalfAppliedSwap`): target missing plus `.old`
  present means restore.
- Research items to settle with evidence before building: exactly how macOS treats a renamed
  running bundle across all our cases (running from `.old` after rename, Gatekeeper on the swapped
  bundle, quarantine on downloads), and whether the Director should also apply staged updates on a
  timer when it has zero sessions rather than only at startup.
- Prove it both directions on this Mac with test builds: a deliberate bad Director build rolls
  back; a deliberate bad launcher build rolls back; a mid-swap kill recovers at next start.

Sequencing: Session 1 leads. Session 3 researches in parallel and implements once the launcher
runs on the Mac. Session 2 discovers in parallel and wires the installer once the
`cc-launcher-mac-arm64` asset exists. The Architect session coordinates and holds owner
questions.

## Environment gaps found during execution (owner actions)

- **The production Gateway is too old for the launcher stream.** Verified 2026-07-11 by Session 1:
  the Gateway on SOREN_NORTH runs release 1.0.7 (commit af963b38), which predates the LauncherHub -
  `/launcher-stream` returns 404 there (and this Mac Director's `/director-stream` connection is
  rejected the same way). Launcher registration and heartbeat work against it; stream commands do
  not. Owner action: update the production Gateway to a build containing the launcher stream
  before the fleet-wide remote-launch proof.
- **The locked screen blocks the last proof leg.** The launcher itself survives a locked screen in
  headless degraded mode, but a restarted Director is a graphical application and dies on a locked
  screen. The Director-restart-over-the-stream proof needs the screen unlocked or the never-lock
  posture applied to this Mac mini.

## Open questions (withdrawal reversed with the resume; question 2 re-asked, 3 and 4 pending)

1. ANSWERED 2026-07-11 - restart policy. See decision 8 above.
2. VOID by owner ruling 2026-07-11 - the protected-builds question only exists for automatic
   restarts, and the auto-update stream is parked, so nothing restarts any Director
   automatically and there is nothing to protect. Do not re-ask. If the auto-update work ever
   resumes, this returns as a design question inside that work, not as an owner question.
3. Any Intel Macs in the fleet, now or planned? (Release pipeline is Apple-silicon only.)
4. Apple Developer signing identity, or stay ad-hoc signed with quarantine stripping?

## Definition of done for the mission

1. On a fresh Mac: one install produces a working Director, a working launcher in the menu bar,
   both connected to the Gateway, launcher visible in `GET /launchers`.
2. From another machine, with the Mac's screen locked and nobody at it: the Gateway stream can
   start, stop, and restart the Mac's Director, and launch an application, with clean parentage.
3. Two-way updates proven on a real Mac: the launcher applies a Director update and rolls back a
   deliberately broken one; the Director replaces a dead or outdated launcher and rolls back a
   deliberately broken one; a mid-swap interruption recovers on the next start. Never both in one
   pass; no running binary ever overwritten in place.
4. The Python daemon in `tools/cc-launcher` is deleted.
5. All work merged to origin/main with tests, enterprise logging, and a verification report with
   evidence from this Mac.
