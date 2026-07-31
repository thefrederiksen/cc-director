# macOS install failure: the orphan launcher loop

Investigation handover, 2026-07-29. Written for an agent already working on installer fixes.

Machine: Sorens-Mac-mini (Apple silicon). Release: v1.8.4. Setup log:
`/Users/soren/Library/Application Support/cc-director/logs/setup/setup-20260729-073819.log`.
Full raw evidence: `/Users/soren/devthrottle-install-evidence.txt` on that machine.

The wizard showed `cc-director` Done, `cc-launcher` Failed, after downloading all 137.2 MB.
Summary line: "Done - 1 installed, 1 skipped".

---

## The short version

The macOS uninstall can only stop a launcher that launchd owns. The installer's own
first-install branch starts launchers that launchd does NOT own. So the product
manufactures an orphan that its own uninstaller cannot remove, and every install after
that collides with the orphan and fails. That machine is now in a state where no version
of anything will install until the orphan is killed by hand.

Nothing was wrong with the download, the binary, or code signing. Those were ruled out
with hard evidence (see "Ruled out" below).

---

## Proven timeline

```
06:24:04  launchd gui/501: "service inactive: com.devthrottle.cc-launcher"
          then "removing service: com.devthrottle.cc-launcher"
          A prior setup run boots the launch agent out of launchd.

06:25:23  Launcher process 34084 starts DIRECTLY (not under launchd), command line
          "/Users/soren/Library/Application Support/cc-director/launcher/cc-launcher --managed".
          Its own attempt to re-register the launch agent FAILS - swallowed by
          LauncherCore.RegisterAutostartSafe into a log line.

~07:38    An uninstall wipes the whole cc-director tree and the launch agent property
          list. It cannot stop 34084: that process is not under launchd, and the macOS
          stop path only knows how to bootout a launchd service.

07:38:46.322  [LauncherMacInstaller] InstallAsync begin
07:38:46.347  launcher: started launcher process id 35158 (--managed)
07:38:46.347  launcher health endpoint on port 7900: OK
              (version 1.8.4+71f90bad0ea1e25cc006159f42c6474bd4263dee, process id 34084)
07:38:46.347  launcher: launch agent property list: NOT registered at
              /Users/soren/Library/LaunchAgents/com.devthrottle.cc-launcher.plist
07:38:46.347  FAILED: Launcher is healthy but did not register its launch agent
              property list; check the launcher log.

07:38:48.065  [Program] CC Launcher already running in this session; exiting second instance.
              This is process 35158 - logged 1.7 SECONDS AFTER the installer had
              already rendered its verdict.
```

Read the process numbers in the health line. The installer started **35158**. The health
check was answered by **34084**, an orphan that had been running for seventy-three minutes
from a path the installer had just overwritten. The whole launcher step took twenty-five
milliseconds because the answer came from a process that was already up.

## Corroborating facts, all first-hand

- `/fleet/machines` still shows `Sorens-Mac-mini pid 34084 startedAt 2026-07-29T10:25:25Z`
  hours after the install. The launcher registers the machine with the Gateway, so the
  fleet's view of that machine is being served by an orphan from a wiped installation.
- The placed binary is correct: 143,840,156 bytes, `sha256 b37a66f7788a5c48a9fb8734bc50c1d34780e032aceddc40203d0a7fed58b20f`,
  which matches `release-manifest.json` for `cc-launcher-mac-arm64` exactly. Mach-O arm64,
  ad-hoc signed, no quarantine attribute. Placement worked.
- The launch agent property list does not exist. `launchctl print gui/501/com.devthrottle.cc-launcher`
  returns "Could not find service".
- `logs/launcher/` never existed on disk. `LauncherLaunchdAutostart.EnsureRegistered`
  creates that directory BEFORE it writes the property list, so the 06:25 registration
  attempt died at or before the write.
- The launchd unified log shows ZERO bootstrap attempts for `com.devthrottle.cc-launcher`
  between 06:24 and 08:00. The attempt never reached launchd.
- Process 34084 is now running a deleted binary inode and writing to a deleted log file
  (lsof fd 40w, NLINK=0, still growing). Every log from before 07:38 is gone.

## Ruled out

- **Code signing / Gatekeeper.** The binary is ad-hoc signed with no quarantine attribute
  and its hash matches the manifest. It would have run fine.
- **Download or placement failure.** The size shown in the wizard (137.2 MB) is the exact
  asset size, and the on-disk hash matches.
- **A version-mismatch rejection.** The health line shows the probe accepted the answer.
  The failure was the launch agent check alone.

---

## The six issues

Ordered by what to fix first. Items 1-3 are one contained piece of work and together
close the loop.

### 1. The macOS uninstall leaves an orphan launcher that blocks every future install

**Symptom.** After an uninstall, a launcher process can still be running, holding port 7900
and the single-instance mutex. Every subsequent install then fails, permanently.

**Where.** `tools/cc-director-setup-engine/Uninstaller.cs` - the macOS branch calls
`LauncherLaunchdAutostart.Unregister()`, which runs `launchctl bootout`. That is a no-op
when the launcher was not started by launchd. The Windows branch does the right thing in
`StopLauncherTrayApp`: it finds processes whose image lives under the install-owned
launcher directory and stops those.

**Why it is a loop, not bad luck.** `LauncherMacInstaller.InstallAsync` has a first-install
branch that starts the launcher DIRECTLY when no property list exists. That is precisely
how a non-launchd launcher comes into being. The installer creates the orphan; the
uninstaller cannot remove it; the next installer collides with it.

**Fix shape.** Stop the launcher by process as well as by label on macOS: find the process
whose executable is the install-owned launcher binary, ask it to exit through its own
shutdown endpoint (it is token-gated - see `LauncherAuth.TokenFile` and the pattern in
`Program.ApplyUpdate`), and confirm port 7900 is free before the uninstall reports success.
Scope it to the install-owned path so a developer launcher running from a repository is
never touched, exactly as the Windows path already does.

**Done looks like.** Start a launcher directly (not under launchd), run the uninstall, and
port 7900 is free with no `cc-launcher` process left. Then a fresh install succeeds.

**Reproduction available now.** Sorens-Mac-mini, process 34084. No launchd service, deleted
binary inode, still serving port 7900. It has been left untouched on purpose.

### 2. The install health check cannot tell the new launcher from the old one

**Symptom.** The installer certifies its install against whatever process holds the port,
including one it failed to replace, and does not wait for the process it started.

**Where.** `tools/cc-director-setup-engine/LauncherHealthProbe.cs` (`WaitForHealthyAsync`)
and `tools/cc-director-setup-engine/LauncherMacInstaller.cs` (`InstallAsync`). The probe
polls a URL and compares a version string. It never receives or checks a process id.

**The version guard cannot fail on a reinstall.** `VersionUtil.TryParse` strips build
metadata, so the orphan's `1.8.4+71f90bad...` and the freshly placed `1.8.4` compare equal.
The identity work behind issue #2042 only catches a version CHANGE, which is the case that
matters least. On a same-version reinstall the guard is blind.

**Timing.** The verdict was rendered at `07:38:46.347`; the started process logged its first
line at `07:38:48.065`. The installer never waited for its own child.

**Fix shape.** Pass the process id the installer launched into the probe and require the
health answer to come from it. Wait for the child (bounded) before ruling. Keep the version
comparison as a second signal, not the only one.

**Done looks like.** A test that installs over a RUNNING same-version launcher and expects
failure. That test passes today when it should fail - which is the point.

### 3. The wizard computes the failure reason and never displays it

**Symptom.** The user sees the word "Failed" and no reason. The sentence
"Launcher is healthy but did not register its launch agent property list" exists in memory
and is discarded.

**Where.** `tools/cc-director-setup-avalonia/Services/EngineInstallRunner.cs` sets
`item.StatusDetail` on every failure path. `tools/cc-director-setup-avalonia/Steps/InstallStep.axaml.cs`
(`BindItem`) binds only `Status`, `Progress` and `SizeText`. `Steps/CompleteStep.axaml.cs`
names WHICH component failed but never WHY.

**Fix shape.** Render `StatusDetail` under the failed row, and include it in the Complete
screen's failure panel and in the generated issue body.

**Done looks like.** A failed launcher install shows the engine's own sentence on screen
without opening a log file. This is the smallest change of the six and the one that saves
the most time on every future report.

### 4. Launch agent registration fails silently and leaves the machine un-installable

**Symptom.** The launcher tries to register its launch agent, fails, catches the exception,
writes a log line, and carries on looking perfectly healthy. The machine is now in a state
that breaks every future install, and nothing says so.

**Where.** `src/CcDirector.Launcher/LauncherCore.cs` - `RegisterAutostartSafe` (line 117),
whose catch block writes `[LauncherCore] Autostart registration FAILED: {ex.Message}` and
returns. Called from `LauncherTrayController.Start` on the normal tray path and from
`Program.RunHeadless` on the degraded path.

**Evidence it happened here.** The 06:25 launcher ran plain `--managed` (so
`RegisterAutostart` was true and the skip path was not taken), yet no property list and no
`logs/launcher` directory exist, and launchd recorded no bootstrap attempt. The exact
exception message is in the log the uninstall deleted - see issue 5 - so the CAUSE of the
registration failure is still unknown.

**Fix shape.** A launcher that cannot register its autostart should surface that state:
report it on `/healthz` and `/status` so the Gateway can see it, and make it visible in the
tray. Do not fail the process - it is still useful - but stop it looking healthy.

**Done looks like.** A launcher whose registration failed reports that fact over its own
API, and the fleet can tell a registered launcher from an unregistered one.

### 5. The uninstall destroys the diagnostics for the failure that follows it

**Symptom.** All logs from before the uninstall are gone, including the one that explains
why the machine got into its broken state.

**Where.** `tools/cc-director-setup-engine/Uninstaller.cs:216` -
`Directory.Delete(root, recursive: true)` over the whole per-user root, which contains
`logs/`.

**Why it bit.** The 06:25 registration failure line is in `director-2026-07-29-34084.log`,
which was deleted at 07:38 while process 34084 still held it open. On macOS a deleted-but-open
file cannot be read from another process without root, so that line is unrecoverable. The
investigation could reconstruct the timeline but not read the original cause.

**Fix shape.** Preserve `logs/` across an uninstall, or copy it aside to a retained location
before the delete. The uninstall already distinguishes install artifacts from user data;
logs belong on the preserved side.

**Done looks like.** After an uninstall, the previous installation's logs are still readable.

### 6. The macOS installer replaces a running Director's application bundle with no gate

**Symptom.** `MacAppPlacer.PlaceAsync` runs `/bin/rm -rf "<target>"` and re-extracts
`Director.app` while that exact process is running. There is no check anywhere in the
macOS wizard for a running Director.

**Where.** `tools/cc-director-setup-engine/MacAppPlacer.cs`. Compare
`tools/cc-director-setup/Services/EngineInstallRunner.cs` (`HandleDirectorRunningAsync`,
`IsDirectorRunning`, `OnProcessBlocking`), which is the Windows gate. I grepped every file
in `tools/cc-director-setup-avalonia` for `Process.GetProcessesByName`, `launchctl`, `Kill`
and quit prompts: there are none.

**Observed, not proven.** After this install the Director on that Mac (process 34111,
started 06:26, still running the pre-install image) stopped answering fleet relays for about
twenty minutes. A session it created produced 13 bytes and its agent never started. It later
recovered. I did not prove the install caused this.

**Fix shape.** Reproduce deliberately first - install over a running Director on macOS and
watch the Director. Then decide between warning the user to quit (the Windows behaviour) and
restarting the application afterwards. Do not implement blind.

---

## Also worth knowing

- The same launcher start path exists in the command line installer,
  `tools/cc-director-setup-cli/Commands.cs`, and there `if (!launcherStart.Success) return Error`
  fails the whole install. Its comment states the assumption that is wrong here:
  "Idempotent: an already-running launcher just keeps serving."
- `cc-devthrottle` is not on the PATH on that Mac (`~/.local/bin` contains only `claude`).
  The installer wrote the PATH line into `.zshrc` at `07:38:46.348`, but the tools bundle is
  provisioned on first app launch, and the app that would do it is the pre-install Director
  still running from memory.
- The launcher writes its own log into `logs/director/`, not `logs/launcher/`. The property
  list template points `StandardOutPath` at `logs/launcher/`, a directory that only
  `EnsureRegistered` creates. Worth tidying while in the area.
- File search on that machine abandoned the whole root after 621 directories in 9 seconds,
  which is issue #2241 reproducing in the wild.

## What was NOT verified

- The cause of the 06:25 launch agent registration failure. The exception message is in the
  deleted log. Everything else about that failure is corroborated two independent ways, but
  the reason itself is unknown.
- That the uninstall at ~07:38 was user-initiated. It is inferred from the evidence: only
  `Uninstaller.cs` deletes the whole root, the setup log recorded an empty installed version
  (so the wizard ran in fresh-install mode), the LaunchAgents directory has a 07:38
  modification time, and every earlier log is gone. Nobody was asked to confirm it.
- Any causal link between replacing `Director.app` and the Director going unresponsive.
  Correlated in time only.
- Nothing on the Mac mini was changed. The investigation was read-only throughout, and the
  orphan process 34084 was deliberately left running as a reproduction.
