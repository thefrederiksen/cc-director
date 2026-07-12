# Gateway Connection - Phase 3 proof (one status box)

Design spec section 6. The two former bottom-left boxes (`GatewayIndicator` + `AccountIndicator`
in `MainWindow.axaml`) are merged into ONE `GatewayStatusBox` with two check lines and four visual
states. One click opens the Gateway Connection panel on the resolver's current step.

Both states below are from the running app on SOREN_NORTH (slot 5 for the real green state; a fully
isolated slot-6 instance with a fresh `CC_DIRECTOR_ROOT` for the amber first-run state, so the real
config was never touched).

## Amber - first run (NotConfigured)

`gateway-statusbox-amber-firstrun.png` / `gateway-statusbox-amber-closeup.png`

Amber box. Line 1: hollow amber ring + "Connect to Gateway". Line 2: muted hollow ring + "Sign in".
Clicking opens the panel on Step 1 (the automatic scan).

## Green - all good (AllGreen)

`gateway-statusbox-green-allgreen.png` / `gateway-statusbox-green-closeup.png`

Green box. Line 1: green check + "Connected". Line 2: green check + "Signed in:
soren@centerconsulting.com". Clicking opens the panel on the Done view.

## The other two visual states

Yellow (Connecting) and Red (ConnectFailed / WasConnectedNowUnreachable) are transient handshake
states that are awkward to hold still for a live screenshot. They are pinned by the presenter unit
tests (`GatewayStatusBoxPresenterTests`), which assert the four visual states and the load-bearing
line-text rules (amber both-lines-action, yellow "Connecting...", green shows the email, red names
the failing leg). Phase 5 exercises the red repair path end to end.

## Tests

- `GatewayStatusBoxPresenterTests` - 9 new tests, all four visual states + the line-text and
  muted-account rules.
- Full GatewayConnection + Account Core suite: 251 passed.
- `CcDirector.Avalonia` and `CcDirector.Avalonia.Tests` build clean, zero warnings.
