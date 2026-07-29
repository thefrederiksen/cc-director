# Installer mission: one product, two platforms

> **Status, 29 July 2026.** Phases 1 and 2 are implemented on branch `installer/orphan-loop`,
> phase 3 is implemented EXCEPT item 3.1 - the macOS gate before replacing a running Director, which
> is deliberately deferred because the handover says reproduce it first and nobody has - and phase 4's
> documentation is done. What each item became is recorded
> against it below. Two items resolved differently than written, and both are corrected in place:
> item 3.3's `FrameworkDetector` was never a .NET check - it was a second implementation of "is
> there a coding agent here?" - and item 4.5 was not a bug at all.

Written 2026-07-29 for an agent to run end to end. Sources: the Codex review of pull request
2263, the macOS install investigation in
`docs/HANDOVER-mac-installer-launcher-orphan-2026-07-29.md`, and the screen-parity work already
in flight on branch `installer/three-step-parity`.

## Why this exists

Two wizards were maintained by hand and drifted into two different products for one account.
Fixing the screens exposed the deeper version of the same problem: the *engine* underneath them
has bugs that were found on one platform and assumed to be that platform's fault, when the code
is shared and both platforms have them.

**The rule for this mission: a finding on one platform is not closed until it has been checked on
the other.** Every item below carries an explicit platform scope, and the scope was established by
reading the code, not by assuming the platform where the symptom appeared.

Three findings changed scope when checked that way. All three were reported as macOS problems and
are shared-engine problems:

| Reported as | Actually |
|---|---|
| macOS health probe cannot tell the new launcher from the old | **Both.** `LauncherTrayInstaller.cs:81` (Windows) and `LauncherMacInstaller.cs:106` call the same `LauncherHealthProbe.WaitForHealthyAsync` with no process id. |
| macOS computes the failure reason and never displays it | **Both.** Both `EngineInstallRunner`s set `StatusDetail` on every failure path; neither wizard's markup binds it. |
| macOS launcher swallows an autostart registration failure | **Both.** `LauncherCore.RegisterAutostartSafe` is shared; on Windows it is the Run key instead of a launch agent. |

One factual refinement to the handover: the uninstall that destroyed the logs was **not** a plain
uninstall. The whole-root delete only runs when the user ticks "Also delete my data"
(`Uninstaller.Apply`, `deleteData`, default false, `Uninstaller.cs:186`). The fix does not change,
but the reproduction does - you have to ask for the wipe to lose the logs.

---

## Priority

Phase 1 is the only phase that can brick a machine. It is one contained piece of work and it
closes a loop: the installer manufactures an orphan, the uninstaller cannot remove it, and the next
install collides with it forever. Do it first, on both platforms, before anything cosmetic.

Phase 2 is every place a screen tells the user something untrue.
Phase 3 is the destructive and ungated paths.
Phase 4 is repository hygiene the earlier phases break or have already broken.

---

# Phase 1 - the orphan launcher loop

## 1.1 The uninstall cannot stop a launcher that launchd does not own  — DONE

**Scope: macOS.** Windows is already correct and is the model to copy.

**Symptom.** After an uninstall a launcher process can still be running, holding port 7900 and the
single-instance mutex. Every later install then fails, permanently. There is a live reproduction:
Sorens-Mac-mini, process 34084, no launchd service, deleted binary inode, still serving port 7900,
deliberately left running.

**Where.** `tools/cc-director-setup-engine/Uninstaller.cs` macOS branch calls
`LauncherLaunchdAutostart.Unregister()`, which runs `launchctl bootout` - a no-op for a process
launchd never started. The Windows branch `StopLauncherTrayApp` (`Uninstaller.cs:343`) does the
right thing: it finds processes whose image lives under the install-owned launcher directory and
stops those.

**Fix.** Stop the launcher by process as well as by label on macOS. Find the process whose
executable is the install-owned launcher binary, ask it to exit through its own token-gated
shutdown endpoint (`LauncherAuth.TokenFile`; the pattern is in `Program.ApplyUpdate`), and confirm
port 7900 is free before the uninstall reports success. Scope it to the install-owned path so a
launcher running from a developer's repository is never touched - exactly as Windows already does.

**Done looks like.** Start a launcher directly, not under launchd. Run the uninstall. Port 7900 is
free, no `cc-launcher` process remains, and a fresh install then succeeds. Add the test.

## 1.2 The health probe certifies against whatever holds the port  — DONE (shipped in 1.8.5)

**Scope: both.** This is the finding that changed platform when checked.

**Symptom.** The installer rules its own launcher install a success based on an answer from a
process it did not start - including the one it just failed to replace. On the Mac mini the
installer started process 35158, the health answer came from orphan 34084 which had been up for
seventy-three minutes, and the whole step took twenty-five milliseconds. The started process
logged its first line 1.7 seconds *after* the verdict was rendered.

**Where.** `tools/cc-director-setup-engine/LauncherHealthProbe.cs` (`WaitForHealthyAsync`,
`Certifies`), called identically from `LauncherTrayInstaller.cs:81` (Windows) and
`LauncherMacInstaller.cs:106` (macOS). The probe polls a URL and compares a version string. It
never receives or checks a process id.

**Why the version guard cannot save it.** `VersionUtil.TryParse` strips build metadata, so an
orphan reporting `1.8.4+71f90ba...` and a freshly placed `1.8.4` compare equal. The identity work
behind issue #2042 catches a version *change*, which is the case that matters least. On a
same-version reinstall - the common case - the guard is blind on both platforms.

**Fix.** Pass the process id the installer launched into the probe and require the health answer to
come from it. Wait for that child, bounded, before ruling. Keep the version comparison as a second
signal, never the only one.

**Done looks like.** A test that installs over a RUNNING same-version launcher and expects
failure. Write it first and watch it pass against today's code - that passing test is the bug.

## 1.3 The first-install branch manufactures the orphan  — DONE

**Scope: macOS.** This is the other half of 1.1; fixing only the uninstall leaves the factory running.

**Where.** `LauncherMacInstaller.InstallAsync` starts the launcher DIRECTLY when no property list
exists. That is precisely how a launcher outside launchd comes into being, and 1.1 is why nothing
can then remove it.

**Fix.** Register the launch agent first and let launchd start it, or - if a direct start is
genuinely needed for the first run - record that the process is not launchd-owned so the uninstall
in 1.1 knows to stop it by process. Do not leave a path that creates an unmanaged launcher silently.

**Done looks like.** After a first install on a clean Mac, the running launcher is a launchd
service (`launchctl print gui/501/com.devthrottle.cc-launcher` finds it).

## 1.4 A launcher that cannot register autostart still reports healthy  — DONE, and tray mode now answers SIGTERM

**Scope: both.** `LauncherCore` is shared; only the mechanism differs (launch agent on macOS, Run
key on Windows).

**Where.** `src/CcDirector.Launcher/LauncherCore.cs`, `RegisterAutostartSafe` (line 117). The catch
block writes `[LauncherCore] Autostart registration FAILED: {ex.Message}` and returns. Called from
`LauncherTrayController.Start` and from `Program.RunHeadless`.

**Why it matters.** On the Mac mini this is the original sin: the 06:25 launcher ran `--managed`,
its registration failed, it carried on looking perfectly healthy, and the machine was left in a
state that broke every future install with nothing saying so. The exception message itself is gone
(see 3.2), so the *cause* of that registration failure is still unknown.

**Fix.** A launcher that could not register its autostart must surface that state: report it on
`/healthz` and `/status` so the Gateway can see it, and show it in the tray. Do not fail the
process - it is still useful - but stop it looking healthy.

**Done looks like.** A launcher whose registration failed says so over its own API, and the fleet
can tell a registered launcher from an unregistered one.

---

# Phase 2 - screens that say untrue things

## 2.1 The failure reason is computed and thrown away  — DONE

**Scope: both.** Reported as macOS; neither wizard displays it.

**Symptom.** The user sees the word "Failed" and no reason. The sentence "Launcher is healthy but
did not register its launch agent property list" existed in memory and was discarded, so a
five-second diagnosis became a log-file investigation.

**Where.** Both `EngineInstallRunner`s set `item.StatusDetail` on every failure path
(`cc-director-setup/Services/EngineInstallRunner.cs:307`,
`cc-director-setup-avalonia/Services/EngineInstallRunner.cs:124`, `:181`, `:235`, `:249`). Neither
`InstallStep` binds it - macOS `BindItem` binds only `Status`, `Progress` and `SizeText`, and the
Windows step does the same. Both Complete screens name *which* component failed but never *why*.

**Fix.** Render `StatusDetail` under the failed row on both platforms, and include it in the
Complete screen's failure panel and in the generated issue body.

**Done looks like.** A failed launcher install shows the engine's own sentence on screen without
anyone opening a log. This is the smallest change in this document and the one that saves the most
time on every future report.

## 2.2 "Up to date" is decided by the Director version alone  — DONE

**Scope: both.**

**Symptom.** `prep.IsUpToDate` reflects the Director only, and the whole apply phase is then
skipped. If the Director is current but the launcher is old or missing, macOS paints the launcher
green as "Up to date" without installing or starting it, and Windows starts whatever launcher is
already there - which an old binary can survive, because of 1.2.

**Where.** `cc-director-setup/Services/EngineInstallRunner.cs:92`,
`cc-director-setup-avalonia/Services/EngineInstallRunner.cs:79`, and the up-to-date branches in
both `MainWindow`s. Note the macOS green line is new on this branch: it was added to stop the card
sitting at "Pending" and replaced one false state with a worse one.

**Fix.** Decide up-to-date per component, not from the Director alone. A card may only claim "Up to
date" for a component whose installed version was actually compared.

**Done looks like.** With a current Director and a deleted launcher, the update path installs the
launcher and no card claims otherwise.

## 2.3 The Windows launcher card is not bound to its download item  — DONE

**Scope: Windows.** macOS already binds it.

**Symptom.** If the launcher download, checksum or swap fails during an update while the previous
binary remains, the engine counts the item as failed - and then `StartLauncherAsync` starts the old
binary and paints the card green "Running". The Complete screen reports a skipped component while
the install screen says the launcher work succeeded.

**Where.** `cc-director-setup/MainWindow.xaml.cs` (`StartLauncherAsync`) and
`Steps/InstallStep.xaml.cs` - the card is driven only by the start call, never by the item.

**Fix.** Bind the Windows launcher card to the launcher download item as macOS does, and let a
failed item win: a successful start of a stale binary may not overwrite a failed install.

## 2.4 A release-fetch failure leaves every card at "Pending"  — DONE, both platforms say "Not started"

**Scope: both.** Pre-existing, same class as 2.2.

**Where.** The rate-limit and general catch blocks in both `RunInstallAsync` paths change only the
heading line and enable Retry. `SetItems` was never reached, so every card stays "Pending" forever.

**Fix.** On a fetch failure, put the cards in a state that matches reality - not started - and say
so once in the heading line.

## 2.5 Windows says "1 component(s)", macOS names what failed  — DONE

**Scope: Windows adopts macOS.** This is the last content difference between the two Complete
screens, and here macOS is better.

**Where.** `cc-director-setup-avalonia/Steps/CompleteStep.axaml.cs` names the skipped components;
`cc-director-setup/Steps/CompleteStep.xaml.cs` prints `{skipped} component(s)`.

**Fix.** Plumb the skipped names through on Windows and use the macOS wording on both.

## 2.6 The no-agent notice can be false  — DONE

**Scope: both.**

**Symptom.** The Complete screen says no coding agent is set up, and the Director then finds one
immediately when it opens.

**Where.** `tools/cc-director-setup-engine/AgentPresence.cs` searches only the wizard process's
inherited `PATH`. The Director deliberately probes more (see `ClaudeAgentPlugin` and the npm-global
and `~/.local/bin` locations) precisely because PATH goes stale. On the Mac mini, `~/.local/bin`
holds `claude` and is not on the installer's PATH.

**Fix.** Probe the same places the Director probes. Two components must not disagree about whether
the machine has an agent on it.

**Done looks like.** With Claude installed only at `~/.local/bin/claude` and that directory off
PATH, the Complete screen does not claim there is no agent.

---

# Phase 3 - destructive and ungated paths

## 3.1 macOS replaces a running Director's application bundle with no gate

**Scope: macOS.** Windows has the gate and is the model.

**Where.** `tools/cc-director-setup-engine/MacAppPlacer.cs` (`PlaceAsync`) runs
`/bin/rm -rf "<target>"` and re-extracts `Director.app` while that exact process may be running.
There is no running-Director check anywhere in the macOS wizard: the handover grepped
`tools/cc-director-setup-avalonia` for `Process.GetProcessesByName`, `launchctl`, `Kill` and quit
prompts and found none. Windows gates this in
`cc-director-setup/Services/EngineInstallRunner.cs` (`HandleDirectorRunningAsync`,
`IsDirectorRunning`, `OnProcessBlocking`).

**Observed, not proven.** After that install the Mac's Director stopped answering fleet relays for
about twenty minutes and a session it created produced 13 bytes with no agent. It recovered. The
causal link was not established.

**Fix.** **Reproduce deliberately first** - install over a running Director on macOS and watch the
Director - then choose between warning the user to quit (the Windows behaviour) and restarting the
application afterwards. Do not implement blind.

## 3.2 The opt-in wipe destroys the diagnostics for the failure that follows it  — DONE, logs are copied out first

**Scope: both.**

**Where.** `tools/cc-director-setup-engine/Uninstaller.cs:186` onward - the opt-in
`deleteData` branch deletes the whole per-user root, and `logs/` lives inside it.

**Why it bit.** The 06:25 registration failure line was in `director-2026-07-29-34084.log`, deleted
at 07:38 while process 34084 still held it open. On macOS a deleted-but-open file cannot be read
from another process without root, so the original cause of 1.4 is unrecoverable.

**Fix.** Copy `logs/` aside to a retained location before the delete, or exclude it. The uninstall
already separates install artifacts from user data; logs belong on the preserved side even in a
full wipe.

**Done looks like.** After a wipe uninstall, the previous installation's logs are still readable.

## 3.3 The gateway is left needing a runtime the installer no longer guarantees  — DONE, the gateway is published self-contained too

**Scope: Windows.**

**Symptom.** Pull request 2263 bundles the .NET runtime into the Director and the launcher and
deletes the Prerequisites step that used to require .NET. The gateway tray application is still
published framework-dependent, and the wizard still refreshes it on a machine already installed as
a gateway host. On such a machine with .NET missing, setup can replace the gateway, stop the old
process, and fail to start the new one - reported, but the gateway is left down.

**Where.** `.github/workflows/release.yml` gateway publish step,
`cc-director-setup/MainWindow.xaml.cs` (`RunGatewayTrayInstallAsync`),
`cc-director-setup-engine/GatewayTrayInstaller.cs`.

**Fix.** Flip the gateway publish to bundle the runtime as well. One word; the asset goes from
about 64 MB to about 144 MB, and this wizard never downloads it, so it costs nothing at install
time. The alternative - stop refreshing the gateway from the wizard at all - belongs to the
separate gateway pass, not here.

---

# Phase 4 - repository hygiene

## 4.1 The render harness expects a screen that no longer exists  — DONE, and it gained a hermetic --screens run

**Scope: Windows harness.** `tools/harnesses/setup-wizard-render-harness/Program.cs:70` treats the
first Next as Prerequisites and waits for `RefreshButton`. Next now begins the real install, so the
harness waits 60 seconds for a control that cannot exist and captures nothing. The repository's end
to end visual check is broken until this is updated to the three-step flow.

## 4.2 Continuous integration names deleted tests  — DONE

**Scope: both.** `.github/workflows/ci.yml:31` still lists `PrerequisiteClassificationTests` and
`CapabilityNoticeTests`, which pull request 2263 deletes. Stale comments in the same class: the
Windows rail comment in `MainWindow.xaml:41` still describes four steps renumbered 1-4, and both
Complete markups still say `CapabilityNotice` renders skipped recommended prerequisites.

## 4.3 The gateway-surface test proves less than it claims  — DONE, scope stated and the names pinned with it

**Scope: both.** `tools/cc-director-setup.Tests/InstallerNoGatewaySurfaceTests.cs` scans literal
`Text=` and `Content=` attributes only. It cannot see labels built in code, bindings, converters,
tooltips or automation names - and there is a live counterexample: the uninstall flow prints
"Gateway" and "Cockpit" from code-behind while the test passes. The uninstall case is legitimate
(it is describing what will be removed from a gateway host), so the fix is to state the scope
honestly in the test and extend the scan to the install and complete code-behind, where a leak
would be a real defect.

## 4.4 The command line installer carries the same wrong assumption  — DONE, and its duplicate agent detector is deleted

**Scope: both.** `tools/cc-director-setup-cli/Commands.cs` fails the whole install on
`if (!launcherStart.Success)`, with a comment stating the assumption 1.2 disproves: "Idempotent: an
already-running launcher just keeps serving." Fix it with 1.2 so the command line path and the
wizards agree.

## 4.5 The launcher's log path disagrees with its property list  — NOT A BUG

**Scope: macOS. Resolved as not a bug.** The property list points `StandardOutPath` at
`logs/launcher/` for launchd's capture of standard output and error, which is legitimately separate
from the launcher's own structured log in `logs/director/`. The directory is created by
`EnsureRegistered`, which item 1.3 now always calls on a first install. `logs/launcher/` never
existing on the Mac mini was evidence that the registration died before the write - not evidence of
a path mismatch. Nothing to change. (That the launcher's own log lands under `logs/director/` is
still confusing when hunting a launcher problem; moving it is cosmetic and touches shared logging,
so it belongs with the nice-to-haves.)

---

# Already done, in flight on `installer/three-step-parity` (pull request 2263)

Do not redo these; verify them.

- Both wizards are three steps - Welcome, Install, Complete - showing the same three cards in the
  same order: DevThrottle, cc-launcher, Tools.
- The Prerequisites step and everything behind it is deleted, because the Windows Director and
  launcher now publish with the runtime inside them and nothing the installer places needs anything
  already on the machine.
- The "Gateway & Cockpit" card is gone, and the gateway phase's streamed log lines no longer
  overwrite the heading line. A gateway failure still reaches the user in words.
- `InstallCompletion` moved into the shared engine, so both wizards reach one verdict.
- `AgentPresence` widened to all eight agents and now drives the amber "one thing left" state on
  both platforms - subject to 2.6, which is the real fix.
- macOS adopted the Windows Welcome and Complete wording, the DevThrottle name on the main card, an
  always-visible launcher card, a status on the Tools card, and "Open DevThrottle" in place of
  "Launch Director".
- A double-encoding mistake in `CompleteStep.xaml.cs` (`âœ“`, `Â·`) was found by review and repaired.

# What has NOT been verified

State these plainly rather than letting a reader assume otherwise.

- **Nobody has clicked through either wizard on this branch.** The parity work is proven by build
  and unit tests only.
- **The bundled runtime has not been proven on a machine without .NET.** That is the entire point of
  the change and a green build cannot show it. Install on a clean Windows machine with no .NET and
  confirm the application starts.
- **The cause of the original launch agent registration failure is unknown.** The exception message
  was in the log the wipe deleted. Everything else about that failure is corroborated two
  independent ways.
- **No causal link was established** between replacing `Director.app` and the Director going
  unresponsive. Correlated in time only.
- **Nothing on the Mac mini has been changed.** The investigation was read-only and orphan process
  34084 is still running on purpose, as the reproduction for 1.1 and 1.2.

# Out of scope

The Mac mini also reproduced issue #2241 - file search abandoning the whole root after 621
directories in 9 seconds. Real, unrelated to the installer, belongs on that issue.
