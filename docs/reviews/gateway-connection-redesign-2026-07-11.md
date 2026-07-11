# Gateway Connection - Design Specification

Status: authoritative specification for the Gateway Connection mission. Rebuilt 2026-07-11 by the
Architect (session "Gateway Connection - Architect", b58cdd43, machine SOREN_NORTH) after the
original HTML draft was lost during a session move. This markdown document supersedes the lost
HTML and is now the single source of design truth referenced by the mission brief
(docs/architecture/gateway-connection-mission-2026-07-11.md).

This document is written in plain English, ASCII only, no abbreviations. It is grounded in the
current code, verified against the working tree at commit a85e2eec on 2026-07-11.

---

## 1. The problem, stated plainly

The desktop Director's Gateway setup is scattered across too many controls that each expose a
piece of plumbing instead of an outcome. In the Settings Gateway tab (SettingsDialog.axaml lines
150 to 205) the user faces, top to bottom:

- a Gateway URL text box,
- a Detect button (look for a gateway on this machine),
- a Test button (check the URL is reachable),
- a second address box "Director public URL",
- a second Detect button (fill from this machine's network address),
- a plain-text Gateway token box,
- a "Connect to Gateway..." button that opens a separate pairing dialog (ConnectToGatewayDialog),
- a "Re-run setup wizard..." button that reopens the onboarding wizard.

The onboarding wizard (OnboardingWizardDialog.axaml) duplicates the URL-plus-Detect-plus-Test
controls as its Gateway step. The main window shows two stacked status boxes in the bottom-left
corner (MainWindow.axaml): GatewayIndicator (line 251, amber "GATEWAY NOT SET") and
AccountIndicator (line 286, "ACCOUNT"). A third destination, the GatewayTroubleshootDialog, opens
when a connection leg fails.

The user has to understand the plumbing to make it work. That is the defect.

---

## 2. The target, stated plainly

ONE guided flow, in ONE reusable panel, reachable from ONE status box. A brand-new user on a
brand-new machine reaches "fully connected" by doing exactly three things:

1. click the amber status box,
2. click Connect,
3. type the pairing code.

No URL typed. No Detect. No Test. No separate dialogs.

The whole design rests on a hard split between two states that are verified separately and never
conflated:

- **CONNECTED** - this Director can reach the Gateway, proven by the existing two-way nonce
  handshake (GatewayConnectionMonitor). Heartbeats alone never prove it; both legs must complete.
- **SIGNED IN** - this device is trusted by the Gateway (a valid per-device key) AND the Gateway
  reports a signed-in account (GatewayAccountStatusClient reading GET /account/status).

Green is earned, never assumed. Connected green comes only from the proven handshake; Signed-in
green comes only from the Gateway's own report. Device pairing is the Director-side half of
"signed in", not a third checkbox.

---

## 3. Decisions already settled - do not re-open

These thirteen are fixed. They are restated from the mission brief so this spec stands alone.

1. Two user-visible checkpoints, no more: Connected and Signed In.
2. The Director never gates on any of this (issues 641 and 664 removed the Director login; the app
   always boots and works locally). The status box is a nudge, never a wall.
3. The Director never holds account credentials (epic 1069: the account lives at the Gateway). The
   Director's part is device pairing plus watching GET /account/status.
4. Green is earned: Connected only from the proven two-way handshake; Signed-in only from the
   Gateway's own report.
5. Scanning is automatic (the Detect button dies); connecting is the test (the Test button dies).
   Manual address entry stays available as a fallback behind a link.
6. The Director public URL is auto-detected and lives under a collapsed Advanced section; it
   surfaces as a question only when the callback leg fails and auto-detection cannot resolve it.
7. The device key / token is never displayed in the clear - masked, under Advanced.
8. One panel, three hosts: the Settings Gateway tab embeds it, the status-box click opens it in a
   window, the first-run onboarding wizard embeds it as its Gateway step. One control, no copies.
9. The two bottom-left boxes merge into one box with two check lines. One click target, one
   destination, any state.
10. The troubleshooter dialog stops being a click destination; its diagnostics render inline in the
    panel's repair mode.
11. No fallback programming; fail loudly with a named failing leg and a specific fix.
12. Plain English everywhere - no abbreviations, no jargon. ASCII only in code and output.
13. Windows first: build and human-verify every phase on SOREN_NORTH against the real Gateway; the
    Mac gets a single verification pass at the end (one Avalonia codebase, so no porting step).

---

## 4. The state model

The panel and the status box both render from ONE resolver that reduces the two verification
sources into a single enumeration. This resolver is the heart of Phase 1 and must be unit-tested
in isolation (it takes plain inputs, returns a plain state - no UI, no I/O).

Inputs to the resolver:

- `gatewayConfigured` - is a Gateway address set in config.json at all.
- `connection` - the GatewayConnectionMonitor result: Unknown / Verifying / Connected / Failed,
  plus, when Failed, WHICH leg failed (outbound reach, or the Gateway's callback to this Director).
- `wasEverConnected` - has the handshake ever succeeded in this run (distinguishes "never set up"
  from "was working, now unreachable").
- `deviceKeyPresent` - is a per-device key stored for this Director.
- `account` - the GatewayAccountStatusClient result: Unknown / Unavailable / SignedOut / SignedIn
  (with email when signed in).

Output - the resolved overall state (drives both colors and which step the panel opens on):

| State | Meaning | Box color | Panel opens on |
|-------|---------|-----------|----------------|
| NotConfigured | No Gateway address, never connected | Amber | Step 1 (connect) |
| Connecting | Address set, handshake verifying | Yellow | Step 1 (progress) |
| ConnectFailed | Handshake failed, a named leg is down | Red | Step 1 (repair) |
| ConnectedNotSignedIn | Handshake proven, device not paired or account signed out | Amber | Step 2 (sign in) |
| AllGreen | Handshake proven AND Gateway reports signed in | Green | Done view |
| WasConnectedNowUnreachable | Was green this run, now the handshake is failing | Red | Step 1 (repair) |

Rules the resolver must enforce:

- Connected green requires `connection == Connected`. A heartbeat or a cached value never paints
  green (decision 4).
- Signed-in green requires BOTH `deviceKeyPresent` AND `account == SignedIn`. Either missing means
  ConnectedNotSignedIn (never a false green, never a false "signed out" - decision 3, and the
  AccountIndicator's existing "never a false signed out" rule at MainWindow.axaml line 283).
- `account == Unavailable` while Connected must NOT read as SignedOut; it is "cannot tell yet",
  shown as a muted/verifying sub-line, not a red or amber alarm.
- WasConnectedNowUnreachable outranks ConnectFailed only when `wasEverConnected` is true this run;
  it exists so a mid-session Gateway move reads as "was working" (repair), not "never set up".

---

## 5. The one panel: GatewayConnectionPanel

A single Avalonia UserControl with an internal step model. It renders whichever step the resolved
state points to. It is embedded three ways (decision 8) and never duplicated.

### Step 1 - Connect

Purpose: get to CONNECTED without the user typing anything.

On show, the panel automatically scans for Gateways in the issue-1233 discovery order:

1. this machine (a Gateway process on localhost),
2. the tailnet (the Gateway's published Tailscale address),
3. the local network (the Gateway's published machine-name and LAN IP).

Found Gateways appear as one-click picks. Picking one runs the connect - which IS the test
(decision 5): it fires the two-way handshake and shows live progress. There is no separate Test
button and no separate Detect button.

This is the ONLY screen a brand-new user sees, so it must teach as well as work. It carries a
plain-English intro, a clearly-marked recommendation, and a one-line "when to use this" on every
option. Approved by Soren 2026-07-11; the rendered reference is
docs/reviews/gateway-connection-step1-mockup-2026-07-11.html.

Copy (use these words - decision 12, plain English):

- Title: "Connect to your Gateway", with a small state pill in the header showing the count
  ("2 FOUND" while found, "SCANNING..." while the scan runs).
- Intro paragraph: "Your Gateway is the hub this Director connects to for voice, the fleet view,
  cross-machine sessions, and shared keys. Pick how this Director should reach it - we looked on
  this computer and over Tailscale. Not sure? Start with the one marked Recommended."
- Section label above the list: "FOUND ON YOUR NETWORK".

Each found option is a framed row: an icon, a name plus its address, a "when to use this" line, and
a chevron. The per-kind copy:

- This computer (Gateway on localhost): name "This computer", address = the machine name; when:
  "The Gateway runs on this machine. Fastest and always available - the computer name does not
  change, so this keeps working."
- On your network (Gateway found by machine-name / LAN IP): name "On your network", address = the
  machine name; when: "The Gateway is on another computer on your network. The computer name rarely
  changes, so this keeps working - best when you are on the same network."
- Over Tailscale: name "Over Tailscale", address = the ts.net name; when: "Reaches the Gateway from
  any network. Use this on a laptop you travel with, or from a remote office - anywhere you are not
  on the same network as the Gateway."

The Recommended rule (decides which ONE row gets the badge): recommend the most stable LOCAL name
that is reachable, in priority order This computer > On your network > Over Tailscale. Tailscale is
recommended ONLY when it is the sole option found. When more than one is found, exactly one row
carries a "Recommended" badge and a green left-accent; the rest are plain. This encodes Soren's
rule: the local machine name rarely changes, so it is the default; Tailscale is the off-network
fallback for travel or a remote office.

A footnote under the list states the no-hidden-test rule: "Picking an option connects right away
and checks the two-way connection - there is no separate Test step." A subtle "Scan again" link
sits below the list.

Visual direction (match the dictation card, TranscriptionComponent.axaml, and docs/VisualStyle.md;
single dark theme, ASCII only): card surface #252526 on the #1E1E1E panel, 1px #3C3C3C borders,
9-10px corner radius; the header count pill tinted blue (background #16283F, text #5AA9F0); the
Recommended row uses a green left-accent bar (#34D06E) and badge (background #1B3A2A, text #34D06E)
with a faint green wash; option icons in small tinted tiles (a monitor glyph for local, a globe for
Tailscale); guidance/intro text in muted blue #6C79A0; the "when to use" lines in #888888 with the
key phrase emphasized in #AAAAAA. No new accent colors beyond the app palette.

ASCII wireframe - scanning, then found (two options, the local one recommended):

```
+----------------------------------------------------------+
|  Connect to your Gateway                       [ 2 FOUND ]|
|  Your Gateway is the hub this Director connects to for    |
|  voice, the fleet view, cross-machine sessions, and       |
|  shared keys. Pick how this Director should reach it -     |
|  we looked on this computer and over Tailscale. Not sure? |
|  Start with the one marked Recommended.                   |
|                                                          |
|  FOUND ON YOUR NETWORK                                    |
|  | []  This computer  SOREN_NORTH   [Recommended]     > | |  <- green accent
|  |     The Gateway runs on this machine. Fastest and    | |
|  |     always available - the name does not change.     | |
|  | ()  Over Tailscale  soren-north....ts.net           > | |
|  |     Reaches the Gateway from any network. Use this   | |
|  |     on a laptop you travel with or a remote office.  | |
|                                                          |
|  Scan again                                              |
|  [ v Enter the address manually - name+port or full URL ]|  <- collapsed Advanced
|  (check) Picking an option connects right away and checks |
|          the two-way connection - no separate Test step.  |
+----------------------------------------------------------+
```

ASCII wireframe - connecting (live progress), the click IS the test:

```
+--------------------------------------------------+
|  Connecting to Gateway on SOREN_NORTH...         |
|   [x] Reached the Gateway                         |
|   [.] Waiting for the Gateway to reach this       |
|       Director back...                            |
+--------------------------------------------------+
```

ASCII wireframe - a named failure (decision 11, no fallback - name the leg and the fix):

```
+--------------------------------------------------+
|  Could not finish connecting.                    |
|  The Gateway could not reach this Director back   |
|  (the callback leg). This Director advertises     |
|  http://SOREN_NORTH:7879 - the Gateway could not  |
|  open that address.                               |
|                                                  |
|  Fix: make sure port 7879 is reachable from the   |
|  Gateway host, or set the Director public URL     |
|  under Advanced.                                  |
|   [ Try again ]   [ Advanced v ]                  |
+--------------------------------------------------+
```

The manual-entry fallback and the Director public URL both live under an Advanced disclosure that
is collapsed by default (decisions 5, 6). The Director public URL only surfaces as a REQUIRED
question when the callback leg fails and auto-detection cannot resolve it (decision 6).

### Step 2 - Sign in (Phase 2)

Once CONNECTED, Step 2 gets the device paired and the account signed in. It absorbs the current
ConnectToGatewayDialog pairing flow.

Two paths, shown together:

- **Pairing code** - the user reads a one-time code off the Gateway host's own screen and types it
  here. On success the Gateway issues this device a unique key, saved locally (this is exactly what
  ConnectToGatewayDialog does today; the field moves into the panel).
- **Open the account page** - a button that opens the Cockpit Account page in the browser; the
  panel then polls GET /account/status and auto-advances to the Done view the moment the Gateway
  reports signed in.

ASCII wireframe:

```
+--------------------------------------------------+
|  Sign in                                         |
|  Connected to the Gateway. One more step to make  |
|  this device trusted.                            |
|                                                  |
|  Pairing code (shown on the Gateway host screen): |
|   [ _ _ _ - _ _ _ ]        [ Pair this device ]   |
|                                                  |
|  or                                              |
|   [ Open the account page ]                       |
|   Waiting for you to sign in...                   |  <- appears after click, polls status
+--------------------------------------------------+
```

### Done view (Phase 2)

Both checks green. Compact confirmation plus the collapsed Advanced section (masked device key,
Director public URL, manual Gateway address - all read-only summaries, never plain text keys,
decision 7).

```
+--------------------------------------------------+
|  Gateway                                         |
|   [x] Connected to Gateway on SOREN_NORTH         |
|   [x] Signed in as soren@centerconsulting.com     |
|                                                  |
|   [ Advanced v ]   [ Sign out ]                   |
+--------------------------------------------------+
```

---

## 6. The one status box: GatewayStatusBox (Phase 3)

The two bottom-left boxes (GatewayIndicator at MainWindow.axaml line 251, AccountIndicator at line
286) merge into ONE box with two check lines and four visual states. One click target; the click
opens GatewayConnectionPanel in a window, on the step the resolver points to (section 4).

The four visual states:

| Visual | When | Two lines read |
|--------|------|----------------|
| Amber (needs attention) | NotConfigured or ConnectedNotSignedIn | one or both lines show a hollow marker + the next action |
| Yellow (working) | Connecting | "Connecting..." |
| Green (all good) | AllGreen | both lines filled, account email on line 2 |
| Red (was working, now broken) | ConnectFailed or WasConnectedNowUnreachable | the failing leg named on the relevant line |

```
+--------------------------+      +--------------------------+
|  o Connect to Gateway    |      |  x Connected             |
|  o Sign in               |  ->  |  x Signed in: soren@...  |
+--------------------------+      +--------------------------+
     (amber, first run)                  (green, done)
```

The click always takes the user to the next unfinished step. First-time setup and re-sign-in are
the same flow into the same panel.

---

## 7. The three flows

- **First-time (brand-new machine).** Box is amber -> click -> panel Step 1 auto-scans -> user
  clicks the found Gateway -> handshake proves Connected -> panel advances to Step 2 -> user types
  the pairing code -> both checks green -> Done. Three user actions total: click box, click
  Connect, type code.
- **Re-sign-in (device known, signed out at the Gateway).** Box is amber on line 2 only (Connected
  is already green) -> click -> panel opens directly on Step 2 -> "Open the account page" or pair ->
  green. It never sends the user back through Step 1.
- **Gateway moved (was connected, address changed).** Box goes red as WasConnectedNowUnreachable ->
  click -> panel opens Step 1 in repair mode -> the rediscovery scan runs automatically and offers
  the Gateway's new address as a one-click fix (Phase 5).

---

## 8. The deletion list

By the end of Phase 4 none of the following is reachable from the user interface. Each line is the
control and its current location.

From SettingsDialog.axaml Gateway tab (lines 150 to 205):

- `DetectGatewayButton` "Detect" (line 160) - scanning is automatic now.
- `TestGatewayButton` "Test" (line 164) - connecting is the test now.
- `DetectPublicUrlButton` "Detect" for the public URL (line 174) - auto-detected under Advanced.
- `GatewayTokenBox` plain-text token box (line 185) - never shown in the clear; masked under
  Advanced.
- `ConnectToGatewayButton` "Connect to Gateway..." (line 190) - pairing moves into panel Step 2.
- `RerunOnboardingButton` "Re-run setup wizard..." (line 198) - the panel is the flow; no re-run
  button.
- The bare `GatewayUrlBox` / `GatewayAdvertisedBox` as primary controls (lines 158, 172) - they
  survive only as the manual-entry fallback under Advanced, not as the front door.

From the main window (Phase 3):

- The two separate boxes `GatewayIndicator` (line 251) and `AccountIndicator` (line 286) - replaced
  by the single GatewayStatusBox.

As a click destination (Phase 5):

- `GatewayTroubleshootDialog` as a place the user is sent - its diagnostics render inline in the
  panel's Step 1 repair mode instead. (The diagnostic logic is reused; the dialog-as-destination
  goes away.)

The onboarding wizard's duplicated URL-plus-Detect-plus-Test Gateway step is replaced by the
embedded panel (Phase 4).

---

## 9. Implementation map

Reuse what exists; do not rebuild verification.

| Concern | Existing code to reuse | Where the new work goes |
|---------|------------------------|-------------------------|
| Connected verification | `GatewayConnectionMonitor` (src/CcDirector.ControlApi) | resolver consumes its result |
| Account/signed-in status | `GatewayAccountStatusClient` + `AccountIndicatorPresenter` (src/CcDirector.Core/Account) | resolver consumes its result |
| Device pairing | `ConnectToGatewayDialog` + `DeviceRegistryClient.Register` | pairing UI moves into panel Step 2 |
| Diagnostics | `GatewayTroubleshootDialog.axaml.cs` logic | rendered inline in Step 1 repair mode |
| Settings Gateway tab | `SettingsDialog.axaml` 150-205 + `.axaml.cs` handlers | tab embeds the panel; handlers deleted |
| Two status boxes | `MainWindow.axaml` 251 + 286, presenters in `MainWindow.axaml.cs` | merged into GatewayStatusBox |
| Onboarding Gateway step | `OnboardingWizardDialog.axaml` | embeds the panel |
| New | - | `GatewayConnectionPanel` UserControl + `GatewayConnectionStateResolver` (Core, unit-tested) |

Put the resolver in a testable Core location (alongside AccountIndicatorPresenter, which already
has AccountIndicatorPresenterTests as the pattern to follow). The resolver has NO UI and NO I/O -
plain inputs in, resolved state out - so it is fully unit-tested. The panel is thin over it.

---

## 10. The five phases, with proof

Each phase ships alone: implemented, merged to origin/main per the trunk rule, deployed to
SOREN_NORTH, and clickable by Soren before the next phase begins.

- **Phase 1 - The panel, connect step.** New GatewayConnectionPanel + GatewayConnectionStateResolver.
  Step 1: automatic scan in the issue-1233 order, found Gateways as one-click picks, manual-entry
  fallback under Advanced, live handshake progress, named failures. Reachable from a TEMPORARY menu
  entry for testing (not yet wired into Settings or the status box).
  Proof: unit tests for the resolver across all six states; screenshots of scanning, connecting,
  connected, and a named failure, from the running app against the real Gateway on SOREN_NORTH.

- **Phase 2 - Sign-in step.** Step 2 pairing-code entry (absorbing ConnectToGatewayDialog) and the
  open-the-account-page path that polls account status and auto-advances. The Done view with the
  collapsed Advanced section (masked key, public URL, manual address).
  Proof: unit tests for the resolver's signed-in transitions; screenshots of Step 2, the polling
  wait, and the green Done view, from the running app.

- **Phase 3 - One status box.** Merge the two bottom-left boxes into GatewayStatusBox with two check
  lines and the four visual states. Click opens the panel on the current step.
  Proof: unit tests for the box presenter across the four visual states; screenshots of amber,
  yellow, green, and red in the real main window.

- **Phase 4 - Settings cleanup.** Gateway tab becomes the embedded panel; the Account tab becomes a
  status summary plus one button; execute the section 8 deletion list; the onboarding wizard embeds
  the panel.
  Proof: the deletion list is fully executed (grep proof that the named controls are gone);
  screenshots of the new Settings Gateway tab and the onboarding Gateway step.

- **Phase 5 - Repair mode.** Troubleshooter diagnostics inline in Step 1; changed-address
  rediscovery offered as a one-click fix; red-state routing; re-sign-in lands directly on Step 2.
  Proof: screenshots of the repair view after a simulated Gateway move, and of re-sign-in opening
  on Step 2.

---

## 11. Definition of done for the mission

1. All five phases merged to origin/main, each with unit tests for the resolver and presenters, and
   each human-verified on this Windows machine against the real Gateway.
2. The section 8 deletion list is fully executed - none of the removed buttons, boxes, or dialogs
   are reachable from the user interface.
3. The end-to-end success test passes on a fresh profile: from clean start to two green checks
   without typing a URL - click the amber box, click Connect, type the pairing code.
4. The Mac verification pass is explicitly scheduled as the final step (network discovery, browser
   launch, and device-key storage path are the three platform-sensitive spots).
5. A final verification report in docs/reviews/ showing every state and step with screenshots from
   the running app.
