# Mutual update on macOS: research findings with evidence from this Mac

> Research report for the CC Launcher mission, part of the two-way update work (mission brief:
> `docs/architecture/cc-launcher-mission-2026-07-11.md`, design draft:
> `docs/plans/cc-launcher-mac-tunnel-and-mutual-updates.md`). Written by session
> "CC Launcher - Mutual Update" (79f0d94f) on Sorens-Mac-mini, 2026-07-11. Every claim in part 1
> was verified by a live experiment on this machine (macOS 26.5, Apple silicon), using throwaway
> test bundles in the session scratchpad and a throwaway launchd agent - never the owner's
> running Directors. Part 2 is a map of the code as it exists in the working tree today.

## Part 1 - Operating system behavior, verified by experiment

The test fixture was a small self-contained single-file .NET application (the same shape as
cc-director and cc-launcher), placed inside an ad-hoc-signed `.app` bundle, serving a health
endpoint over a plain socket and able to spawn a child process on request.

### 1.1 Renaming a running application bundle is safe

With the test application running from `live/SwapLab.app`:

1. `mv live/SwapLab.app live/SwapLab.app.old` - the process **kept running, kept serving
   requests, and kept spawning child processes**. A running process holds its executable by
   file node, not by path, and a rename keeps the file node.
2. `mv staging/SwapLab-B.app live/SwapLab.app` (the new build renamed in) - the old process
   was still unaffected.
3. The new build was launched from the swapped-in path while the old one still ran from
   `.old` - **both versions ran side by side**, each healthy.
4. After stopping the old process and deleting the `.old` backup, the new build kept running,
   its code signature verified (`codesign --verify`: "valid on disk, satisfies its Designated
   Requirement"), and it launched fine both by direct execution and by `/usr/bin/open` - from
   its real path, with **no App Translocation**.

**One hazard found.** The running process's idea of its own directory is a stale string:

- During the window between rename-aside and rename-in, any file the old process tries to read
  beside its own executable **fails** (directory not found).
- After the new bundle is renamed in, the old process reading "its own" files by path actually
  reads the **new** bundle's files - a silent cross-version mix.

Consequences for the design: keep the swap window to two back-to-back renames (never a copy in
between), and stop or restart the old process promptly after the swap rather than letting it
run indefinitely against the new bundle's files.

### 1.2 Gatekeeper: execution is gated by quarantine, not by the ad-hoc signature

- `spctl --assess` **rejects** our ad-hoc-signed bundles. That verdict does not matter for us:
  with **no quarantine attribute present**, the bundle executes fine both by direct execution
  and via `/usr/bin/open`, from its real path, no translocation, no prompt.
- With a quarantine attribute present, the bundle is unusable: `open` ran it under **App
  Translocation** (a randomized read-only mount - every path assumption broken) where it
  stalled, and direct execution **hung indefinitely** (blocked on the Gatekeeper assessment,
  which may also put a dialog on the machine's screen).

### 1.3 New and critical: quarantine can only be stripped BEFORE the first launch attempt

Verified twice, cleanly isolated:

- Quarantine set, **never launched**, then `xattr -dr com.apple.quarantine` → strip works,
  application runs instantly. (This is the updater's current order, and it must stay that way.)
- Quarantine set, **launched once** (and therefore assessed by Gatekeeper), then strip →
  **"Operation not permitted" on every file, permanently.** macOS stamps a
  `com.apple.provenance` attribute at first launch, and from then on the ordinary user cannot
  remove the quarantine attribute at all. The bundle can still be deleted - the only recovery
  is delete and re-download.

Rule for all our code paths, including every rescue path: **strip quarantine immediately after
extraction and never attempt to launch first.** If a quarantined bundle was ever launched, do
not try to repair it; delete it and download again.

### 1.4 Our real download-and-extract flow produces no quarantine at all

Tested against the genuine `cc-director-mac-arm64.zip` from release v1.0.7:

- Downloading with .NET `HttpClient` (exactly what `UpdateService.DownloadFileAsync` does)
  attaches **no quarantine attribute** - quarantine is added by applications that opt into it
  (browsers), not by plain socket writes.
- Extracting with `ZipFile.ExtractToDirectory` (exactly what `UpdateService.ExtractMacApp`
  does) preserved the executable bits on both `cc-director` and the `launch` script, attached
  no quarantine, and the extracted bundle **passed `codesign --verify`**.

So the existing `StripQuarantine` call is belt-and-braces rather than load-bearing today. Keep
it (it is cheap and protects against a future download path that does set quarantine), and keep
it positioned before anything could launch the bundle, per finding 1.3.

### 1.5 launchd behavior (user agent with RunAtLoad and KeepAlive)

Verified with a throwaway agent `com.devthrottle.swaplab-test` (bare single-file binary, the
launcher's shape), then torn down:

- **KeepAlive resurrects a killed process** within a few seconds, no action needed.
- **`launchctl kickstart -k gui/501/<label>` kills and restarts the agent**; the replacement
  process picks up whatever binary now sits at the plist's path. So the update flow "rename the
  new binary in, then kickstart" atomically moves the agent to the new build.
- **Renaming the binary under a live agent is safe** - the running process is unaffected until
  the kickstart.
- **Missing binary** (the simulated mid-swap crash): launchd logs exit code 78 (configuration
  error), keeps rescheduling the spawn, and the moment the `.old` backup was renamed back to
  the target name, **launchd recovered the service by itself** - no `launchctl` command needed.
  The bottom safety layer works exactly as the design hoped.

**Design consequence discovered.** On macOS there are no file locks, so the Windows-style
sequence (stop the running launcher, wait for its executable to unlock, swap, start) is both
unnecessary and racy: with plain `KeepAlive true`, launchd restarts the launcher immediately
after a graceful shutdown - potentially before the swap has happened, restarting the old build.
The natural macOS sequence is the reverse of Windows:

1. Rename-swap **while the old launcher is still running** (safe, per 1.1).
2. `launchctl kickstart -k` to atomically replace the process with the new build.
3. Health-check; on failure rename the `.old` back and kickstart again.

No stop, no unlock wait, no window where no supervisor is configured.

### 1.6 Overwriting a running binary in place: nondeterministic, still forbidden

Overwriting a small, fully-resident test binary in place did not always kill it (the kernel
kills on the next page-in of an invalidated executable page; a small hot binary may never
fault). One trial stopped serving, another survived untouched. The owner's own observation
(recorded in project memory) is that the real 64-megabyte Director dies instantly when copied
over. The behavior ranges from "instant kill" to "silent time bomb", which is worse than a
deterministic failure. The rule stands absolutely: **every swap is rename-based; never write
into a live file.**

### 1.7 A quirk worth recording (cost an hour; not a product issue)

A .NET `HttpListener` **hangs in its constructor** when the process is launched through
LaunchServices (`open`) on this macOS version, while the same code works when launched from a
shell. A plain socket listener (`TcpListener`) binds instantly either way. The Director and the
launcher both use Kestrel (socket-based), so this does not affect the product - but no test
harness for this mission should use `HttpListener`.

## Part 2 - The code as it stands (map for the implementation)

### Director self-update (`src/CcDirector.Core/Update/`)

- **`UpdateInstaller.SwapMac` (UpdateInstaller.cs:443)** builds the replacement beside the
  target (`ditto` to `<target>.new`, strip quarantine, `chmod +x`), then `rm -rf <target>` and
  `mv <target>.new <target>`. **The `rm -rf` is the whole macOS rollback gap** - change it to
  `mv <target> <target>.old` and the backup exists. One line of behavior.
- **`RecoverHalfAppliedSwap` (UpdateInstaller.cs:265)** returns early on non-Windows (its
  comment predates keeping a macOS backup). The macOS port is: target bundle missing while a
  non-empty `.old` bundle exists → rename `.old` back. Directory operations instead of file
  operations; the decision helper `NeedsHalfSwapRecovery` is already pure and reusable.
- **`TryRollBackFailedUpdate` (UpdateInstaller.cs:314)** returns early on non-Windows. The
  Windows body is file-copy based; the macOS port is two renames. The health-marker and
  bad-version-pinning state (`UpdaterState.PendingHealthCheckVersion`,
  `UpdaterState.PinnedBadVersion`, `updater-state.json`) is platform-neutral and needs no
  change.
- **Found a flaw that also affects Windows:** `CleanupAfterUpdate` (called at
  CcDirector.Avalonia/Program.cs:93) deletes the `.old` backup on the **first** boot of a
  freshly swapped build, while `PendingHealthCheckVersion` is still armed -
  `MarkCurrentBuildHealthy` only runs later, when the main window is shown
  (App.axaml.cs:101). A new build that crashes after early startup but before the window
  leaves **no backup to roll back to**. The fix for macOS (and recommended for Windows too):
  only delete the `.old` backup when no post-update health check is pending - in effect, the
  backup dies when the build proves healthy, exactly as the mission brief states.
- **Nothing applies a staged update without a process start.** `TryApplyStagedUpdateAtStartup`
  runs only at startup; there is no timer. On an unattended machine the launcher's
  `director/restart` is what closes this loop (by design). See recommendation 3 below on
  whether the Director should also restart itself.
- The relauncher flow (`--apply-update <target> <parentPid>`, Program.cs:37) and `Relaunch` via
  `/usr/bin/open` both already work on macOS and match the verified-safe behavior from part 1.

### Launcher self-update (`src/CcDirector.Launcher/`, `tools/cc-director-setup-engine/`)

- **`LauncherSelfUpdate` and `InstallSwapper` are already rename-based and platform-neutral.**
  `InstallSwapper.Place` copies the staged file to `<target>.new` (a fresh file node) and
  renames; `Rollback` renames `.old` back. No changes needed for safety; verified against the
  experiments.
- **The gates to remove or rework:**
  - `LauncherTrayController.cs:426`: the update loop runs only `if (cfg.Enabled &&
    OperatingSystem.IsWindows())`.
  - `LauncherUpdater.LaunchDetachedUpdater` and `CheckStageAndLaunchAsync` carry
    `[SupportedOSPlatform("windows")]`.
  - `LauncherUpdater` reads `ComponentRegistry.Launcher.WindowsAsset`
    ("cc-launcher-win-x64.exe") and stages to a hard-coded `staged/cc-launcher.exe`. The
    `Component` record (Component.cs:15) has no macOS asset name field yet - **Session 2 owns
    adding it** along with the `cc-launcher-mac-arm64` release asset; the field and asset name
    must be agreed between us.
- **`LauncherSelfUpdate.WaitUntilWritable` is meaningless on macOS** (no file locks - it
  returns immediately even while the old launcher runs). Per finding 1.5 the macOS helper
  should not stop-and-wait at all: swap first, then `launchctl kickstart -k`, then
  health-check, renaming back plus kickstart on failure. The existing delegate-injected shape
  of `ApplyAsync` makes this a macOS branch in the injected `stopLauncher`/`startLauncher`
  delegates plus a skip of the writability wait, not a rewrite.
- The launcher's health endpoint (`GET /healthz`, no authentication, LauncherHost.cs:119) and
  version pinning (`PinStore`) already exist and are platform-neutral.

### Director as the launcher's rescuer

- **The Director does not reference the launcher anywhere today** - no rescue exists on any
  platform. The natural home is the Director's periodic auto-update loop
  (App.axaml.cs:324-351), which already runs `UpdateService.CheckAndStageAsync` plus
  `ToolUpdater.RefreshAsync` on a configured cadence. The new step: probe the launcher's
  `/healthz`; if dead longer than a threshold or the binary is missing, place a fresh binary
  through `InstallSwapper` and start it with `launchctl kickstart -k
  gui/<uid>/com.devthrottle.cc-launcher`; if the fresh binary also fails, restore the `.old`.
- `InstallLayout.PathFor` already maps the launcher on macOS to `<LauncherDir>/cc-launcher`
  (InstallLayout.cs:121), and Session 1 has `LauncherLaunchdAutostart` in flight in the working
  tree, so the plist label and paths will come from there.

### Launcher as the Director's updater (what the launcher needs, all verified available)

- The Director's Control API `GET /healthz` returns `Version` **and** `Sessions` (a live
  session count) - ControlEndpoints.cs:86 - which is exactly what the launcher needs both for
  the version comparison and for the "only restart when quiet" gate.
- `DirectorSupervisor` already discovers running Directors (process id and port) from the
  instance registration files `config/director/instances/{id}.json`.
- The release manifest side (`ReleaseSource`, sha-256 verification) is shared setup-engine code
  the launcher already links.
- **`MacAppPlacer` (MacAppPlacer.cs:22)** is the rescue installer: download, verify, `ditto`
  extract, place into `~/Applications`, strip quarantine, record the installed version. It is
  correctly macOS-gated and its quarantine order matches finding 1.3. Note it deletes any
  existing bundle without a backup - correct for "no runnable Director exists", and it must
  only ever be used in that state.

## Part 3 - Recommendations for the Architect (decisions to settle before implementation)

1. **macOS helper order differs from Windows on purpose.** Windows: stop, wait for unlock,
   swap, start. macOS: swap while running, then restart (`launchctl kickstart -k` for the
   launcher; Control API shutdown plus `open` for the Director). Recommend encoding this as two
   named strategies rather than sprinkling platform checks.
2. **KeepAlive semantics need one decision.** With plain `KeepAlive true`, any graceful
   shutdown of the launcher is undone by launchd within seconds, which breaks stop-based flows
   and any human "quit the launcher" affordance. Options: keep `KeepAlive true` and make every
   programmatic restart go through `kickstart -k` (never stop-start), or use the
   `SuccessfulExit false` form so a clean exit stays down. Recommend the first (an unattended
   machine should never have the launcher down), coordinated with Session 1 who owns the plist.
3. **The `.old` backup must survive until the build proves healthy** - move the deletion out of
   unconditional startup cleanup and gate it on the pending-health-check marker being clear.
   Recommend fixing this on Windows in the same change, since the flaw is live there today.
4. **Quarantine handling in every path, including rescues:** strip after extraction, before any
   possible launch; treat a bundle that was ever launched while quarantined as unrecoverable
   (delete and re-download). This is now a verified hard rule, not a style preference.
5. **Should the Director also apply staged updates on a timer at zero sessions?** Recommend
   no - one restart mechanism (the launcher's, health-checked and rollback-capable) is easier
   to reason about than two racing ones, and the launcher is itself rescued by the Director
   and launchd. If the fleet later runs Directors on machines with no launcher, revisit.

## Part 3a - Owner ruling on the restart policy (received 2026-07-11, decision 8 in the mission brief)

Version 1, the one to build now:

- The launcher must **never** restart a Director that has any actively working session.
- An automatic restart is allowed only when **all** of the Director's sessions are idle or
  waiting **and** the current time is inside a nightly maintenance window.
- Both are configurable: the window's start and end hours, and a switch that disables
  automatic restarts entirely.
- When an update is staged but the policy blocks the restart, force nothing - surface a
  "new version waiting" notification to the owner instead.

Build the restart step as a **policy seam** (an injected decision, not logic hard-coded into
the update loop), because version 2 is explicitly deferred and undecided: it may save the
running sessions as handover documents through the existing /handover endpoints, apply the
update, and restore them - or stay notify-only. That choice will be made later, with
version 1 evidence in hand. Do not build any part of version 2 now.

Implementation note for the policy check: the Director's health endpoint reports only a
session **count**; "all sessions idle or waiting" needs the per-session activity state from
the Director's session-list endpoint, which sits behind the Control API's authentication.
The launcher's supervisor already talks to the Control API for graceful stop, so the policy
check rides the same authenticated channel.

## Part 4 - Coordination notes and blockers

- **Implementation is sequenced behind Session 1** ("CC Launcher - Mac Port", 5ad3c44f), per
  the mission brief. Session 1 currently has uncommitted changes to the same launcher files
  the un-gating touches (`LauncherTrayController.cs`, `Program.cs`,
  `LauncherLaunchdAutostart.cs`), and all three worker sessions share one working tree - the
  Director-side changes (`UpdateInstaller`, `UpdaterState` callers) do not overlap Session 1's
  files, but commit coordination needs a plan from the Architect.
- **Session 2 dependency:** the `Component` record needs a macOS asset name field and the
  release needs a `cc-launcher-mac-arm64` asset before `LauncherUpdater` can stage launcher
  updates on the Mac. Proposed asset name: `cc-launcher-mac-arm64` (matching the existing
  `cc-director-mac-arm64.zip` convention, no extension since it is a bare binary).
- All experiments were cleaned up: test bundles and processes removed, the throwaway launchd
  agent booted out and its plist deleted. Nothing was installed and no running Director was
  touched.
