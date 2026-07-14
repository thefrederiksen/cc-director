# Mission: Headless Gateway

Mission id: f39c439c-3752-4631-9c0c-bb48290e507b
Architect: Headless Gateway - Architect (session 6b58543b), SOREN_NORTH
Written: 2026-07-14. Verified against origin/main at fe00dc25.

## The WHY

The Gateway has a user interface of its own - a tray application, a flyout, a pairing
window, a consent window. Every one of those is a second place where Gateway state can be
read and changed, and a second place for it to get out of sync. That split is the disease.

Today it produced a provable, owner-visible failure: the Gateway cannot sign in to a
DevThrottle account, so it cannot enroll any new browser or phone as a device. We fix that
by deleting the Gateway's user interface and moving every human interaction to the Cockpit,
which becomes the one and only user interface for the Gateway.

Scope limit from the owner: we are NOT turning the Gateway into a Windows service. That is
later. This mission is headless plus move-the-interaction-to-the-Cockpit, nothing more.

## What I verified with my own eyes

The shared checkout at D:\ReposFred\devthrottle was 55 commits behind origin/main. All code
findings below come from a clean worktree off origin/main (fe00dc25), not that checkout.

### The live failure, confirmed and timed

- `GET /account/status` on the running Gateway returns `{"signedIn":false}`.
- The credential blob `config\gateway\devthrottle-credential.bin` does not exist.
- `config\gateway\devthrottle-auth-events.jsonl` ends with
  `{"Kind":"logout","At":"2026-07-14T11:40:38Z"}` = 07:40:38 local. The Gateway had been
  signed in continuously since 2026-07-08.
- The Gateway process (pid 27920) started at 07:51:09 local, AFTER that logout - so it
  booted signed-out.
- A loopback login callback is still live right now, over an hour later:
  `netsh http show servicestate` reports request queue
  `HTTP://127.0.0.1:57455:127.0.0.1/DEVTHROTTLE-LOGIN-CALLBACK/` State: Active.
  (It shows as pid 4 in netstat because HttpListener registers with the kernel http.sys.)

That is the deadlock: boot signed-out -> auto-fire a sign-in -> open a loopback listener and
wait with NO timeout -> hold the single-flight lock forever -> every later Sign in click is
swallowed.

### Corrections to the mission brief

The brief was right about the disease and the shape of the deadlock. Four details are wrong,
and two of them change the plan materially.

1. **Port 57455 is not hardcoded.** `git grep 57455` over origin/main returns nothing.
   `LoopbackLoginListener` asks the OS for an ephemeral free port
   (`FindFreeLoopbackPort()`, binds TcpListener to port 0 and reads back the assignment).
   57455 is just what this boot happened to get. The load-bearing fact is not the port - it
   is that the wait has **no timeout at all**.

2. **`FirstRunLoginCoordinator` does not auto-fire and does not hold the lock.** It is a
   passive class. The auto-fire is `GatewayTrayController.PromptSignInIfNeeded`
   (src/CcDirector.GatewayApp/GatewayTrayController.cs:513, issue #637), called right after
   `host.StartAsync()`. The single-flight lock is `GatewaySignInService._singleFlight`
   (src/CcDirector.Gateway/Account/GatewaySignInService.cs:31), and the
   "already in flight - ignoring" message is at GatewaySignInService.cs:106 and
   GatewayTrayController.cs:593.

3. **The Gateway library is ALREADY headless.** There are two projects, and the brief treats
   them as one:
   - `src/CcDirector.Gateway` - the library plus a console host. `OutputType=Exe`,
     `net10.0`, **zero UI packages**. Its `Program.cs` is already a clean generic-host
     worker. Its csproj already says "The Gateway serves NO UI (one-URL plan)".
   - `src/CcDirector.GatewayApp` - the shipped `devthrottle-gateway.exe`. `WinExe`,
     `net10.0-windows`, Avalonia. This holds 100% of the UI.
   The headless skeleton we are supposedly building already exists. This mission is mostly
   "delete GatewayApp, move the shipped exe name onto CcDirector.Gateway, rehome the
   lifecycle logic the tray controller happens to own."

4. **The replacement is not half-built - it is built, merged, and LIVE right now.** The
   brief calls the remote-vs-loopback redirect mechanics "the backbone" and "a separate
   follow-up". They landed in `bd6119fc` (Fix #1080, pull request #1105) and are present in
   the running build ff7a571a. I proved it against the live Gateway:

   ```
   POST https://soren-north.taildb08ed.ts.net/account/sign-in-start
   -> 302 Location: https://devthrottle.com/signin
        ?redirect_uri=https%3A%2F%2Fsoren-north.taildb08ed.ts.net%2Faccount%2Fsign-in-callback
   GET  /account/sign-in-callback -> 200 (public, no credential)
   GET  /account/sign-in-start    -> 200, a real "Sign in with DevThrottle" page
   ```

   A routable https callback, no loopback, no host browser. The doc comment in
   `AccountSignInStartEndpoint` that calls this "a separate follow-up (epic #1069, issue 0b)"
   is **stale** - the code beneath it already has the branch.

### The finding that reframes the mission

The Cockpit already has a "Sign in to DevThrottle" button
(`apps/cockpit/src/account/AccountView.tsx:360`). It calls `POST /account/sign-in`
(`packages/client-core/src/account/accountClient.ts:144`), and that endpoint
(`src/CcDirector.Gateway/Api/AccountSignInEndpoint.cs:76`) fires
`RunSignInAsync()` - **the dead loopback flow**.

So the Cockpit's Sign in button is broken for exactly the same reason the tray's is: it
opens a browser on the Gateway's desktop and waits on a loopback a remote browser can never
reach, behind a lock that is currently held forever. The Cockpit shows
"Waiting for your browser..." indefinitely.

The single highest-value change in this mission is small: **repoint the Cockpit's existing
Sign in button from `/account/sign-in` (loopback) to `/account/sign-in-start` (remote).**
The working flow already exists; the only user interface that is supposed to survive is
wired to the wrong one.

## What the Cockpit already has (the capability gap is small)

| Tray surface | Cockpit home today | Gap |
|---|---|---|
| Open Cockpit | the Cockpit itself | none |
| Sign in to DevThrottle | `/account` button exists | **wired to the dead flow** |
| Settings | `/settings`, 4 tabs | none |
| Start on login | `/settings` -> `PUT /gateway/autostart` | none |
| Status (version, uptime, Directors, port) | `/about`, `/settings` machine tab | none |
| Add a device | `/account` "Your devices"; `/signin` + `/device-callback` | verify vs PairingWindow |
| Restart Gateway | nothing | **real gap** |
| Logs folder / Config folder | nothing | **real gap, and local-only by nature** |
| Consent window (#650) | nothing | **real gap** |

The Cockpit has **no onboarding wizard** of any kind. The only wizard in the repo is Avalonia
desktop (`src/CcDirector.Avalonia/OnboardingWizardDialog.axaml`, issue #370), whose UI-free
logic is already extracted to `src/CcDirector.Core/Onboarding/OnboardingModel.cs`. That
extraction is the natural seam to reuse.

## The plan

### Phase 0 - Unblock the owner today. No code.

Open `https://soren-north.taildb08ed.ts.net/account/sign-in-start` in any browser and click
Sign in. The Gateway signs in; device enrollment works again.

WHY: the owner is blocked right now and the fix is already deployed. It also proves the
replacement flow end-to-end BEFORE we delete the thing it replaces, which de-risks every
later phase. Nothing in this mission should be built on an unproven assumption that the
remote flow works.

### Phase 1 - Point the Cockpit at the working flow, and kill the deadlock.

Remove the GATEWAY's use of the loopback sign-in. Do NOT remove the mechanism itself - see
"the loopback is not the villain" below.

- Repoint the Cockpit Sign in button to `/account/sign-in-start`.
- Delete the startup auto-fire (`PromptSignInIfNeeded`, #637).
- Delete `GatewaySignInService.RunSignInAsync` and the single-flight lock that exists only to
  guard it, `POST /account/sign-in`, and the same-machine loopback branch in
  `/account/sign-in-start` (AccountSignInStartEndpoint.cs:221).
- Extract `FirstRunLoginCoordinator`'s URL statics (`ResolveSignInBaseUrl`, `BuildSignInUrl`,
  `DefaultSignInBaseUrl`) to a new home BEFORE touching the class. They are pure URL helpers
  with nothing to do with loopback, and the flow we are KEEPING depends on them
  (`RemoteSignInRouting.cs:85`), as do both installers.

WHY: this is precisely what broke today. The deadlock - no timeout, a permanent lock, a
browser on the wrong desktop - dies here, in the smallest possible change, and it shrinks
what Phase 3 has to delete. The Gateway's only sign-in becomes the one that already works
from anywhere.

#### The loopback is not the villain - the headless Gateway is the wrong host for it

The brief says `FirstRunLoginCoordinator`, `BrowserLauncher.OpenSystemDefault` and
`LoopbackLoginListener` are "all on the chopping block". That is too broad, and following it
literally would break two shipping things:

- **The installer stands up its own `LoopbackLoginListener`.**
  `tools\cc-director-setup\Services\SignInRunner.cs:73` and
  `tools\cc-director-setup-engine\GatewayAccountEnrollRunner.cs:462` drive it directly, for
  the installer's sign-in step and Gateway-connect step. That is a **legitimate** use: an
  installer is a desktop program, running at the machine, with a human in front of it.
  Opening the local browser and catching the callback on loopback is exactly right there.
  Loopback is only wrong for a process with no desktop. Deleting the mechanism deletes the
  installer's sign-in.
- **`BrowserLauncher.OpenSystemDefault` has non-sign-in callers.** The terminal's link
  context menu uses it (`LinkContextMenuBuilder.cs:237` and `:256`, including the explicit
  "Open in system default browser" item). Remove only the sign-in caller
  (`FirstRunLoginCoordinator.cs:84`); keep the method.
- **`CredentialHandbackPage` survives.** It is shared with the remote front-door callback
  (`AccountSignInCallbackEndpoint.cs:140/159`), which is the flow we are keeping. It exists
  (issue #1082, absorbing security issue #877) precisely to be shared by both.

Build-breaking landmine: `src\CcDirector.Core.Tests\NoCrossMachineLoopbackGuardTests.cs:53`
allowlists `LoopbackLoginListener.cs`, and its `Allowlist_has_no_stale_entries` test fails if
the file stops existing. Any commit that removes the file must remove that line too.

Also note issue **#651** is the standing ticket to finish removing the account/credential
types that #664 left behind - the Manager should read it before deleting anything in
`CcDirector.Core/Account`.

### Phase 2 - The Cockpit gains what the tray still owns.

Close the three real gaps: Restart Gateway, logs and config access, first-run consent. Verify
device-add parity against PairingWindow.

WHY: no capability may be lost when the tray dies. This lands BEFORE deletion so there is
never a window where the owner can do less than before.

### Phase 3 - Delete the Gateway's user interface.

Delete `src/CcDirector.GatewayApp` entirely (GatewayTrayController, PairingWindow,
GatewayConsentWindow, App.axaml, Avalonia, the icons). Move the `devthrottle-gateway`
assembly name onto `CcDirector.Gateway`. Rehome the non-UI logic the tray controller happens
to own: GatewayHost lifecycle, the `/shutdown` self-update hook and its #880 watchdog,
`SettingsHooks`, the `--managed` update loop, autostart registration, port-conflict
diagnostics. Update the installer (`GatewayTrayInstaller`), scripts, and docs.

Do NOT delete `CcDirector.TrayUi` - the Launcher shares it
(src/CcDirector.Launcher/CcDirector.Launcher.csproj:44).

WHY: the UI is the disease. The library is already headless and its Program.cs is already a
generic-host worker, so this is a move, not a rewrite.

### Phase 4 - The Cockpit onboarding wizard.

Walk a fresh install through: reach the Gateway -> sign the GATEWAY in -> enroll THIS browser
as a device -> confirm it actually works.

WHY: the centerpiece. A fresh install has no guided path today, and the two sign-ins below
are not something a new user can be expected to reason about unaided.

## Things the Manager must not get wrong

**There are TWO different sign-ins.** They are easy to conflate and the wizard depends on the
order:
1. **The Gateway signs in** to the cloud account (`/account/sign-in-start` ->
   credential blob). This is the prerequisite.
2. **A browser or phone enrolls** as a device (`/signin` -> devthrottle.com -> 
   `/device-callback` -> `POST /m/enroll` -> device key in localStorage).
A signed-out Gateway cannot do (2). That is exactly today's failure.

**The bootstrap constraint.** The Cockpit is served BY the Gateway from `wwwroot/c`, and its
whole router sits behind `RequireDeviceKey` except `/signin` and `/device-callback`. Anything
the wizard must do before a device key exists has to be on the `AuthMiddleware` public
allow-list, next to the existing `/account/sign-in-start`.

**The Cockpit only builds in Release.** `RunCockpitBuild` is gated to
`Configuration == Release` or `BuildCockpit == true`. On a Debug build `wwwroot/c` does not
exist and the Cockpit answers 404.

**Cross-repo dependency, flagged in-tree (#1081).** `packages/client-core/src/auth/
enrollRequest.ts` notes that devthrottle.com pins `/m/device-callback` and hard-rejects any
platform other than `android`/`ios`. Browser enrollment with `platform: "browser"` may need a
site-side change. Phase 4 must confirm this before designing around it.

**Seven test projects, not two.** Running only Core plus Gateway tests is a false green. The
installer has its own tests (`tools\cc-director-setup.Tests\SignInRunnerTests.cs`, 290 lines,
entirely about the loopback runner) and they are directly in this mission's blast radius.

**Process rules.** Build from your own git worktree off origin/main - never switch branches in
the shared checkout, never `git add -A` there. The shared checkout lags; verify against
origin/main. Redeploy with `scripts\redeploy-gateway.ps1`. Never kill a cc-director process.

## Open design questions for the owner

1. **Who starts and restarts a headless Gateway?** With no tray and no service yet, if the
   Gateway stops, nothing brings it back, and "Restart Gateway" has no home. The natural
   answer is `cc-launcher` - it already exists, already has a tray, and already shares
   `CcDirector.TrayUi`. Making the Launcher the Gateway's supervisor keeps the "no service
   yet" limit intact. Needs the owner's call.

2. **Logs and Config folders.** The tray opens local folders; a Cockpit on another machine
   cannot. Drop them, serve logs in the Cockpit (`/about` already shows diagnostics), or
   leave them to a CLI?

3. **First-run consent (#650).** Into the Phase 4 wizard, or does it die?
