# Mission: Headless Gateway - COMPLETE

Mission id: f39c439c-3752-4631-9c0c-bb48290e507b
Written: 2026-07-14. **Rewritten 2026-07-15 after delivery**, because the original plan was wrong in ways
that matter to anyone reading this next. Verified against origin/main at e266fa0b.

Status: **delivered**. Seven pull requests, all merged, all green. Roughly 2,500 lines net removed.

## The WHY (unchanged, and it held up)

The Gateway had a user interface of its own - a tray flyout, a pairing window, a consent window. Every one
of those was a second place where Gateway state could be read and changed, and a second place for it to
get out of sync. That split was the disease.

Scope limit from the owner: we did NOT turn the Gateway into a Windows service. That is later. This
mission was headless plus move-the-interaction-to-the-Cockpit, nothing more.

## What the original plan got wrong

Read this before trusting any other plan document in this directory.

1. **It planned to DELETE `src/CcDirector.GatewayApp`** and move the `devthrottle-gateway` assembly name
   onto the library. The owner ruled the opposite on 2026-07-15: the Gateway STAYS a tray app for now, and
   the tray shrinks to a Start/Stop shim. Lower risk, and it makes the eventual service switch a deletion
   rather than a rename.

2. **It treated the tray controller as a screen.** It was not - it was the whole application: it owned the
   host lifecycle, the self-update loop, autostart and the shutdown watchdog, tangled in with a flyout.
   **That, not the existence of screens, was what actually blocked the Gateway from becoming a service.**
   Deleting screens did not fix it; moving the lifecycle into the library did.

3. **It deferred the pairing code as an open design question.** The owner settled it immediately ("a relic
   of an old application"), and it turned out to be already dead: `GatewayEnrollmentClient.EnrollAsync` had
   zero callers, and the Director's own panel already told users "there is no pairing code".

4. **It proposed a public "is the Gateway signed in" probe** so the Cockpit could discover a signed-out
   Gateway. Unnecessary. The Gateway already answers `POST /m/enroll` with 409 and a clear message; the
   defect was that the message was not ACTIONABLE, not that it was missing. No new public surface needed.

## The rule that now defines the shape

**Delete `src/CcDirector.GatewayApp` and nothing breaks.**

`GatewayService` (in `CcDirector.Gateway`, headless) owns the host lifecycle, the managed self-update loop,
autostart, the Cockpit's settings hooks, port diagnostics, and the issue #880 shutdown watchdog. A host
supplies only what a service cannot know about its own process - the port, whether it is a managed install,
what to write into the autostart Run key - and gets a complete Gateway.

Two hosts drive it today, which is what makes the rule a fact rather than a claim:

- `CcDirector.GatewayApp` - the tray shim (161 lines: an icon, two menu items, a tooltip)
- `GatewayWorker` - the dev console host, **no user interface at all**

A Windows service will be a third host, and nothing in the library will change.

`HeadlessGatewayGuardTests` pins it: the library must not reference a windowing toolkit, the shim, or
TrayUi. **Known limit:** it reads the compiled assembly's references, and the compiler only emits a
reference the code actually USES. A bare unused `PackageReference` does not trip it - confirmed by trying.
Green means "no windowing code is reachable", not "no windowing package is listed".

## The tray is Start and Stop

That is the whole menu, because start and stop are the only verbs a service - or a cloud Gateway - offers.
The owner was explicit, twice, and rejected keeping "Open Cockpit" on exactly that reasoning.

There is deliberately **no Quit item**: a service has no quit, only stop. `QuitAsync` survives as the
self-update `/shutdown` handler only. Consequence: closing the tray app itself needs Task Manager.

The Gateway **never opens a browser and never draws a window**. A service has no desktop to draw on.

## What shipped

| Pull request | What |
|---|---|
| #1586 | Remove the 4-digit pairing code and its window |
| #1589 | Remove the first-run consent window |
| #1597 | Point the Cockpit's Sign in at the front door that works from another machine |
| #1599 | Cut the tray to Start and Stop |
| #1600 | Give the signed-out Gateway a way out instead of a loop |
| #1603 | Move the lifecycle out of the tray and into the service |
| #1611 | Authenticate `POST /shutdown` so self-update can actually happen (#1609) |

### Where the screens went

| Old Gateway surface | Home now |
|---|---|
| Flyout status rows | Cockpit `/settings`, `/about` |
| Settings | Cockpit `/settings` (four tabs; it already had them) |
| Start on login | Cockpit `/settings` -> `PUT /gateway/autostart` |
| Add a device / pairing code | **Deleted.** Account sign-in enrollment replaced it |
| First-run consent | **Deleted.** No behavioral, startup, login, or usage reporting remains (issue #494) |
| Sign in to DevThrottle | Cockpit `/account` -> `POST /account/sign-in-start` |
| Restart | **Deleted.** Stop then Start |
| Logs / Config folders | **Deleted.** A service cannot open Explorer |

## Two sign-ins - the thing most likely to be conflated

1. **The PERSON signs in** at devthrottle.com.
2. **The GATEWAY signs in** to a DevThrottle account.

A device cannot enroll until BOTH have happened, and a fresh install has only done (1). The Gateway reports
that as 409 from `/m/enroll`. `DeviceCallback` now gives that its own screen naming the GATEWAY as the
missing one, with an action that fixes it. Previously the only button was "Try again", which returned to
the sign-in screen and failed identically, forever.

## Things found along the way that were NOT this mission

- **Self-update had never succeeded** (#1609, fixed in #1611). Both helpers posted `/shutdown` with no
  token, got 401, so the process never exited, its exe never unlocked, and the swap aborted blaming the
  lock. Pre-existing, and it meant **cutting a release delivered it to nobody**.
- **The test that would have caught it was itself dead.** `scripts/test-gateway-selfupdate.ps1` looked for
  `cc-director-gateway.exe`; the exe was renamed to `devthrottle-gateway.exe` in 4e606e29 and the harness
  had not been touched since a commit predating that, so it failed at "build not found" before exercising
  anything. Nobody saw the 401 because nobody could run the test. **That is the more useful lesson than
  the missing header.**
- **The Cockpit's Sign in button was wired to the dead loopback flow** (fixed in #1597). It had to be fixed
  BEFORE the flyout was deleted, because the flyout's Sign in was the only one that worked.

## Still open, none blocking

- **No behavioural test of `GatewayService`.** The guard constructs it with no host, which pins the SHAPE,
  not the behaviour. Starting a real one in a test would write the developer's live Director folder
  (forbidden, #1580). Needs an injectable host seam - its own step.
- **`Gateway/Pairing/` holds only `DeviceRegistry` and `DeviceSignInQrCode`**, neither of which is a
  pairing code. Renaming touches many files.
- **No Quit item** (see above). One line if the owner changes their mind.
- **The first upgrade past #1611 cannot self-update itself** - installed Gateways still carry the old,
  token-less helper. That hop needs the installer or a manual swap; self-update carries every hop after.

## Process notes that still apply

- Build from a worktree off origin/main. Never switch branches in the shared checkout.
- **Launching a Gateway on a developer machine is dangerous without four guards** (proven 2026-07-15):
  `CC_GATEWAY_NO_TAILSCALE=1`, or it re-points the tailnet front door 443 at your test instance;
  `--no-autostart`, or it rewrites the user's Run key to your build (it defaults ON);
  `CC_DIRECTOR_ROOT` at PROCESS scope only, or it writes the live `missions.json`/`devices.json`;
  `--port <free>`, or it collides with 7878. `scripts/test-gateway-selfupdate.ps1` already does all four.
- Never kill a cc-director process. Shut a test Gateway down with an authenticated `POST /shutdown`.
