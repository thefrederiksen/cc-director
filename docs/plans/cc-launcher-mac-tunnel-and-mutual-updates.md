# CC Launcher on macOS: tunnel connectivity, remote launch, and mutual updates

> **PARKED - DESIGN ONLY, NOT SCHEDULED** (owner decision, 2026-07-11). See the mission brief
> docs/architecture/cc-launcher-mission-2026-07-11.md for recorded decisions and where the
> pre-stand-down work is preserved.

> Discovery, gap analysis, and design draft. Nothing here is implemented yet. Written by session
> "CC Launcher - Tunnel + Updates" (7b78e474) on Sorens-Mac-mini, 2026-07-11. Open questions at the
> end are held for the owner, relayed one at a time by the coordinating session (04a37e53).

## Part 1 - What exists today (discovery findings)

There are two unrelated things called "cc-launcher" in this repository:

1. **The product launcher, `src/CcDirector.Launcher`** (.NET 10, Avalonia tray application,
   issue #250). This is the real one, and it already does most of what this mission needs -
   on Windows only:
   - A loopback-only web interface on port 7900, gated by a random bearer token
     (`LauncherHost.cs`, `LauncherAuth.cs`): health, status, launch an arbitrary application,
     start / stop / restart the Director, shut down the launcher.
   - Clean process parentage launching (`LaunchService.cs`) - the whole reason the launcher
     exists (the "rule 0b" nested pseudo-console problem).
   - Director supervision (`DirectorSupervisor.cs`): resolves the installed Director, stops it
     gracefully through its Control API, restarts it.
   - **Outbound Gateway connectivity, already written and mostly platform-neutral:**
     - `GatewayRegistrationClient.cs` registers at `POST {gateway.url}/launchers/register`,
       heartbeats every 30 seconds, re-registers on 410, unregisters on shutdown.
     - `LauncherStreamClient.cs` holds a persistent SignalR connection to
       `{gateway.url}/launcher-stream`, authenticated with `gateway.token`, auto-reconnecting,
       and executes the commands `director/start`, `director/stop`, `director/restart`, and
       `launch` through the same supervisor and launch service the local web interface uses.
   - Managed self-update on Windows (`--managed` flag): a polling loop downloads a new launcher,
     then a detached helper (`Program.cs --apply-update`) stops the old one, swaps the file,
     relaunches, health-checks, and rolls back to the `.old` copy on failure.

2. **The Python daemon in `tools/cc-launcher`** - an older macOS-only development helper
   (launchd, port 8765, no authentication, no Gateway connection, window and screenshot
   control through the Accessibility interface). It is not installed on this Mac mini, its
   `main.py` entry point is broken, and it shares no code with the product launcher.

**Gateway side is fully built and tested.** The Gateway already has the complete counterpart:
launcher registration and discovery endpoints, heartbeat with a 90-second timeout, relay
endpoints (`POST /machines/{machine}/director/restart`, `/start`, `/stop`, `/launch`, and
remote session spawning), the `LauncherHub` persistent stream, a per-connection registry, and a
protected-slot guard (the main build and slots 1-4 refuse restart or stop without an explicit
`confirmProtected` flag). Integration tests exercise the stream end to end. What is missing is
only a user interface that drives these endpoints - and a launcher that runs on a Mac.

**The tunnel is the Tailscale mesh, and it already works from this Mac.** Verified live on
Sorens-Mac-mini: Tailscale runs as an application plus a root system network extension, the
Gateway at `http://soren-north.taildb08ed.ts.net:7878` answers, and this machine's Director is
connected with `streamMode: true` in its configuration file. Both the Director stream and the
launcher stream are outbound connections - nothing ever dials into the Mac - so they keep
working when the screen is locked. The Tailscale Serve provisioners (Gateway machine plus each
Director's own front door) exist for inbound web access to Director ports and are not needed
for the launcher's outbound stream.

**The Director's own update path already works on macOS arm64, with gaps.**
`UpdateService` polls the latest GitHub release, picks `cc-director-mac-arm64.zip`, verifies
the download against the sha-256 in `release-manifest.json`, extracts the application bundle,
strips the quarantine attribute, and stages it. At the next startup the staged build re-invokes
itself as a helper (`--apply-update`), waits for the old process to exit, and swaps the bundle
(`SwapMac`). The gaps on macOS:
- `SwapMac` deletes the old bundle outright (`rm -rf` then `mv`) - **no backup is kept**, so
  there is nothing to roll back to.
- `RecoverHalfAppliedSwap` and `TryRollBackFailedUpdate` both return early on non-Windows -
  the issue-242 self-healing does not run on a Mac.
- Startup notices are Windows-only message boxes; on a Mac a failed update is silent.
- Only Apple-silicon (arm64) assets exist; there is no Intel build.
- The bundles are ad-hoc signed and not notarized; the updater relies on stripping quarantine.

## Part 2 - Gap analysis (honest and short)

| Capability | State | Gap |
|---|---|---|
| Tunnel connectivity for the launcher | Registration and stream clients fully written, platform-neutral, tested against a real Gateway | The launcher does not build on macOS at all (`net10.0-windows`, `WinExe`), so none of it runs here |
| Remote launch | Gateway protocol, relay, stream, and slot guard complete and tested | macOS process code: no `cmd.exe`, application bundles need `/usr/bin/open`, process matching uses `MainModule` which is unreliable on macOS; registry autostart must become a launchd agent; no macOS release asset or packaging |
| Launcher updates the Director | Director self-stages updates on macOS already; the launcher only needs to trigger a restart and the staged build applies itself | No rollback on macOS (no backup bundle, recovery code Windows-only); nobody health-checks the Director after an update; if the Director is dead or absent, nothing can install it |
| Director updates the launcher | `LauncherSelfUpdate`, `InstallSwapper`, and the tool-update loop exist | All gated `OperatingSystem.IsWindows()`; no macOS launcher asset in the release; no way to start the launcher on a Mac after placing it (needs `launchctl kickstart`) |
| Survives an update gone wrong | Windows: `.old` backup, health check, bad-version pinning, bounded attempts | On macOS, none of that safety net exists today, for either binary |

The loopback web interface deserves one honest note: the Gateway's fallback relay dials the
launcher at `{networkAddress}:7900`, but the launcher binds loopback only, so the relay can
never reach a launcher on a different machine. On a remote Mac the persistent stream is the
only command path. That is acceptable - connecting a Director now turns stream mode on - but
it means the stream is load-bearing, not an optimization.

## Part 3 - Design: the Mac launcher and the mutual-update scheme

### Principle

Each binary is the other's rescuer, and **no running binary is ever overwritten in place**
(on macOS that is an instant code-signature kill). Every swap is: write the new file beside
the target, rename the current target aside as a backup, rename the new file in. A running
process keeps its old file node across a rename, so renames are always safe; only writing
into the live file is fatal.

Underneath both binaries sits launchd: the launcher runs as a user launch agent with
`RunAtLoad` and `KeepAlive`, so the operating system itself resurrects it after crashes and
at login. That is the bottom safety layer that needs no code.

### 3.1 Getting the launcher onto macOS (port, not rewrite)

Reuse `src/CcDirector.Launcher` - the Gateway clients, the token authentication, the web
interface, and the tray flyout are already platform-neutral. The work is:

1. Retarget the project (and its test project) from `net10.0-windows` to multi-target
   `net10.0-windows;net10.0`, publish `osx-arm64` self-contained single-file as
   `cc-launcher-mac-arm64` in the release workflow, add it to `release-manifest.json`, and
   give the setup-engine `Component` record a macOS asset name beside `WindowsAsset`.
2. Autostart: a new `LauncherLaunchdAutostart` that writes
   `~/Library/LaunchAgents/com.devthrottle.cc-launcher.plist` (`RunAtLoad`, `KeepAlive`,
   the `--managed` flag) and loads it with `launchctl bootstrap` - the macOS twin of the
   registry Run key, modeled on the plist in `tools/cc-launcher`.
3. `LaunchService` macOS branch: no `cmd.exe` routing; launch `.app` bundles with
   `/usr/bin/open`; plain executables with `UseShellExecute = false` and
   `start_new_session` semantics (a fresh process group), which is what the Python daemon
   already proved gives clean parentage on macOS.
4. `DirectorSupervisor` macOS branch: resolve the installed `CC Director.app` through
   `InstallLayout`, start it with `/usr/bin/open`, keep the graceful stop through the
   Director's Control API exactly as on Windows (that part is portable), and match processes
   by name plus the instance registration files instead of `MainModule`.
5. Retire `tools/cc-launcher` (the Python daemon) once the .NET launcher runs here, so there
   is exactly one launcher. Its window-control tricks are not part of this scope.

Because the launch agent lives in the logged-in user's graphical session, it can launch
graphical applications even while the screen is locked - which is exactly the capability we
confirmed is impossible from a remote shell. The requirement it inherits: the user must be
logged in, which for an unattended fleet machine means automatic login at boot (see open
question 5).

### 3.2 Launcher updates the Director

> **PARTLY SUPERSEDED by internal issue #1033 (owner decision, 2026-07-29) - built and shipped.**
> Everything below about the backup, the health check, the rollback and the version pin is what was
> built: the bundle swap keeps a backup instead of deleting the old bundle, the backup is released when
> the build marks itself healthy, the launcher polls the Director's health endpoint with the port from
> the instance registration files, and a build that never answers is rolled back and pinned.
>
> ONE POINT IS NOW WRONG. This section has the launcher call `director/restart` and let the Director's
> own `UpdateInstaller` apply the staged build during that restart - "the launcher never fights
> UpdateInstaller". The launcher now does the swap ITSELF: stop, swap, start, and confirm the new
> version answers. Handing the swap back to the Director is the exact thing #1033 removes, because a
> Director that replaces its own binary has nothing left to witness whether the relaunch came up - the
> only process that could check is the one that just exited. The Director no longer applies its own
> update automatically at all. See `DirectorUpdateApply`, `DirectorUpdateOwner` and
> `DirectorBuildSwapper`.
>
> Still unbuilt from this section: the rescue install (a launcher that downloads and places a Director
> when none is installed or the bundle is damaged).

The Director keeps its existing self-update (poll, verify, stage). The launcher adds the
missing safety around it:

- **Trigger**: the Director stages silently today and applies only "next time you open the
  app" - on an unattended machine, never. The launcher closes that loop: its poll notices
  the Director is running an older version than the latest release manifest (both version
  numbers are already exposed: the manifest by `ReleaseSource`, the Director's version by
  its health endpoint), waits until the machine is quiet enough (open question 3), then
  performs `director/restart`. The staged build applies itself during that restart - the
  launcher never fights `UpdateInstaller`.
- **Backup**: change `SwapMac` to rename the old bundle to `CC Director.app.old` instead of
  deleting it, and delete the backup only when the new build marks itself healthy (the
  `MarkCurrentBuildHealthy` call that already exists). This is one small change in
  `UpdateInstaller` and gives macOS the same `.old` safety Windows has.
- **Health check and rollback**: after any restart the launcher initiated, it polls the
  Director's Control API health endpoint (port discovered from the instance registration
  files). If the Director is not healthy within a deadline, the launcher renames the `.old`
  bundle back, records the bad version in the updater state so it is not retried (the
  pinning mechanism already exists in `UpdaterState`), relaunches, and reports the failure
  through its Gateway stream.
- **Rescue install**: if no Director is installed or the bundle is damaged, the launcher can
  download the verified release itself and place it - the placing code already exists as
  `MacAppPlacer` in the setup engine; the launcher just needs to call it.

### 3.3 Director updates the launcher

The launcher's managed self-update already implements the right shape on Windows
(poll, stage, detached helper: stop, swap, relaunch, health check, roll back to `.old`).
The macOS work is to un-gate it and make the swap rename-based:

- Remove the `OperatingSystem.IsWindows()` gate on the update loop and on
  `LauncherSelfUpdate`, and make the helper's swap on macOS a rename-aside plus rename-in
  (never a copy over the live file).
- The Director's existing periodic tool-update loop becomes the rescuer: if the launcher's
  health endpoint has been dead longer than a threshold, or its binary is missing, the
  Director places a fresh launcher binary (rename-based, through `InstallSwapper`) and
  starts it with `launchctl kickstart -k gui/<uid>/com.devthrottle.cc-launcher`. If the
  new launcher binary itself is broken, launchd keeps failing to start it - and the
  Director's loop restores the `.old` copy.

### 3.4 Ordering rule and the failure matrix

**A machine never updates both binaries in the same pass.** Update one, wait for it to be
confirmed healthy, and only then (next cycle) update the other. This guarantees at least one
healthy supervisor exists at every moment.

| Failure | What happens |
|---|---|
| Download corrupt or truncated | Sha-256 mismatch, file deleted, retried next poll. The live binary was never touched. |
| New Director will not start | Launcher's health check fails, launcher restores `CC Director.app.old`, pins the bad version, relaunches, reports over the stream. |
| New launcher will not start | The helper's own health check rolls back to `.old`. If the helper died too, launchd keeps retrying while the Director's loop notices the dead health endpoint and restores `.old`. |
| Machine reboots mid-swap | The rename-based swap has a tiny window where the target name is absent. A startup recovery check (the macOS port of `RecoverHalfAppliedSwap`): if the target is missing and a `.old` exists, restore it. |
| Both binaries broken at once | Prevented by the ordering rule - the second update never starts before the first is confirmed healthy. |
| Launcher crashes for any other reason | launchd `KeepAlive` restarts it; at worst it re-registers with the Gateway thirty seconds later. |

### Out of scope for this design

Fleet-wide update orchestration in the Gateway user interface (a "update all machines"
button), Intel Macs, notarization, and Linux. All can layer on later without changing this
scheme.

## Part 4 - Open questions for the owner (held for relay)

1. **One launcher or two?** The plan is to port the .NET launcher to macOS and retire the
   Python daemon in `tools/cc-launcher` entirely. Confirm the Python daemon has nothing you
   still rely on (its window minimize/focus and screenshot endpoints would go away).
2. **Menu-bar icon on the Mac, or headless?** The Windows launcher shows a tray icon. On
   macOS a menu-bar icon means packaging the launcher as an application bundle; a headless
   launch agent (log files and the web interface only) is a plainer first version. Which do
   you want first?
3. **When may the launcher restart a Director to apply an update?** An unattended machine can
   restart any time it is idle, but a restart kills the running sessions. Proposal: only
   restart when the Director reports zero active sessions, plus a nightly window as a
   fallback. Acceptable?
4. **Protected builds on the Mac.** On Windows the main build and slots 1 through 4 are
   protected from remote restart. What is the equivalent on your Macs - protect the installed
   `CC Director.app` plus mac slots 1 through 4, reserving slot 5 and up for agents, same as
   Windows?
5. **Automatic login on fleet Macs.** A launch agent only runs while the user is logged in.
   For unattended recovery after a reboot or power loss, the fleet Macs need automatic login
   at boot - and FileVault disk encryption blocks automatic login, so this is a real
   trade-off. Are the fleet Macs (this Mac mini included) set to log in automatically, and is
   turning FileVault off (or leaving it off) acceptable on them?
6. **Apple-silicon only?** The release pipeline builds arm64 only. Are there any Intel Macs
   in the fleet, now or planned?
7. **Signing.** Everything on macOS today is ad-hoc signed and not notarized; the updater
   strips the quarantine flag itself, which works but is fragile against future macOS
   tightening. Is acquiring an Apple Developer identity for real signing worth doing now, or
   defer?
