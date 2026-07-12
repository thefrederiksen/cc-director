# Verification: CC Launcher on macOS (mission Session 1 - Mac port)

> **ACTIVE.** An erroneous stand-down was relayed on the evening of 2026-07-11 and
> reversed the same evening by the owner directly. Everything below except section 8 is
> complete and evidenced; section 8 (the full Director restart cycle) runs as soon as
> the screen is unlocked.

Branch `cc-launcher-mac-port`, pull request #1365. All evidence gathered live on
Sorens-Mac-mini (Apple silicon, macOS, Darwin 25.5.0) on 2026-07-11, driven by the
mission session "CC Launcher - Mac Port" (5ad3c44f). No process belonging to the owner
was touched; the test Director is a slot-8 build inside a sandbox.

## What was verified

### 1. Build, tests, publish

- `CcDirector.Launcher` builds for both targets (`net10.0-windows` and `net10.0`) on this
  Mac; `CcDirector.GatewayApp` (the Windows consumer of the retargeted `CcDirector.TrayUi`)
  still builds.
- Launcher tests: 27 of 27 pass on macOS (`dotnet test -f net10.0`), including the new
  macOS launch-shape tests (bundle via `/usr/bin/open`, shell script via `/bin/bash`,
  batch file refused).
- Setup engine: 8 new `LauncherLaunchdAutostart` tests pass. The 27 pre-existing
  failures in that suite on macOS are Windows-shaped tests (`.exe` paths, Windows shims)
  unrelated to this change.
- Publish (the exact line the release workflow uses):

      dotnet publish src/CcDirector.Launcher/CcDirector.Launcher.csproj \
        -c Release -f net10.0 -r osx-arm64 --self-contained true \
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

  produced a working 143 megabyte single-file `cc-launcher`.

### 2. Headless degraded mode (screen locked)

The screen on this Mac was genuinely locked throughout the evening
(`CGSSessionScreenIsLocked=Yes`). The published launcher, started under a locked screen:

- logged `User-interface platform failed to initialize: ... RenderTimer ... -6661`
  followed by the loud `DEGRADED: running HEADLESS (no tray icon) - web host + Gateway
  stream only` line,
- stayed alive, and `/healthz` answered
  `{"ok":true,"version":"1.0.7+f8211973...","userInterface":"degraded"}`.

The fallback triggers only when the user-interface platform fails before application
code runs (`App.FrameworkInitialized` flag); faults in a running application still exit
loudly.

### 3. Registration against the PRODUCTION Gateway

With the sandbox launcher pointed at the real Gateway
(`http://soren-north.taildb08ed.ts.net:7878`):

- `POST /launchers/register` answered 201 and `GET /launchers` listed
  `Sorens-Mac-mini` (pid, port 7900, version `1.0.7+f8211973...`) beside the two
  Windows launchers `SORENLAPTOP` and `SOREN_NORTH`.
- Heartbeats ran every 30 seconds.
- `POST /shutdown` on the launcher produced a graceful
  `DELETE /launchers/Sorens-Mac-mini` and the entry disappeared from `GET /launchers`.

**Environment gap found:** the production Gateway runs release v1.0.7 (commit
`af963b38`), which predates the `LauncherHub` - `/launcher-stream` answers 404 there.
The launcher handles this correctly (connect retries with backoff while registration
and heartbeat keep working), but stream commands to ANY launcher cannot work fleet-wide
until the production Gateway is updated to a build that contains launcher-persistent-join.

### 4. Stream commands (real GatewayHost from this branch, run locally)

Because of the gap above, the stream leg was proven against a real `GatewayHost` from
this branch, booted locally with stream mode on and token authentication - the same
class the production Gateway runs:

- The launcher joined `/launcher-stream` and sent `Hello`
  (`connected to http://127.0.0.1:7978`, `Hello sent: machine=Sorens-Mac-mini`).
- `POST /machines/Sorens-Mac-mini/launch` (`/bin/sleep 45`, headless) returned
  `{"ok":true,"via":"stream"}` and the process was really running - the command
  traveled DOWN the persistent stream, not over the REST relay.
- `POST /machines/Sorens-Mac-mini/director/restart` returned `{"ok":true,"via":"stream"}`;
  the launcher's `DirectorSupervisor` ran the full path (stop: no Director running;
  start: `/usr/bin/open` accepted the bundle launch), and the sandboxed test Director
  process started and wrote its startup log.

### 5. The locked-screen limit of the Director itself

The launched test Director (an Avalonia application with no headless mode) then died
with the same locked-screen error (`RenderTimer ... -6661`). This is the documented
platform constraint the owner-accepted never-lock posture exists for - macOS will not
start a graphical application while the screen is locked. The full restart cycle
(instance file appears, graceful Control API stop, new process id) was verified after
the screen was unlocked - see section 8.

### 6. launchd autostart, live

`LauncherLaunchdAutostart` proven against the real launchd gui domain using the
harmless target `/usr/bin/true`:

    plist path: /Users/soren/Library/LaunchAgents/com.devthrottle.cc-launcher.plist
    before: registered=False, loaded=False
    EnsureRegistered -> True
    after register: registered=True, loaded=True
    registered command line: /usr/bin/true --managed
    EnsureRegistered again (idempotence) -> False
    Unregister -> True
    after unregister: registered=False, loaded=False

The property list uses `RunAtLoad` plus `KeepAlive` gated on `SuccessfulExit=false`:
launchd resurrects a crash but a clean quit (or the self-update helper's shutdown)
stays down, so launchd can never race a swap by relaunching the old binary.

### 7. SIGTERM (the launchctl bootout scenario)

`kill -TERM` on the headless launcher logged `SIGTERM received`, unregistered from the
Gateway (`DELETE /launchers/Sorens-Mac-mini` -> `Unregistered`), and exited cleanly;
`GET /launchers` was empty afterwards.

### 8. Full Director restart cycle over the stream (screen unlocked)

NEVER RAN - the mission was parked before the screen was unlocked. The leg was fully
scripted and staged: `director/restart` over the stream starts the sandboxed test
Director; its instance registration file appears; a second `director/restart` finds it
through the instance file, stops it gracefully through `POST /shutdown` on its Control
API, and a new process id replaces the old one. If the mission resumes, this is the
only unproven step - everything up to and including the Director process actually
starting from the stream command is evidenced in sections 4 and 5.

## Latent Windows bug fixed on the way

`DirectorSupervisor.FindDirectorPort` read `instances/*.json` directly under the
storage root and looked for a `"port"` property - a directory and shape no Director
ever writes (real files are `config/director/instances/{id}.json` with `Pid` and
`ControlEndpoint`). The port was therefore never found and the "graceful" stop always
fell back to a hard process kill - on Windows too. The supervisor now reads the real
instance registration files and only trusts one whose process is alive and belongs to
the installed Director.
