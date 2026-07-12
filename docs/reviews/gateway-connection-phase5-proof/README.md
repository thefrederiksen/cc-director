# Gateway Connection - Phase 5 proof (repair mode)

Design spec sections 5 / 7 + the section-4 state table. The last build phase.

## 1. Red-state routing + named failing leg

`red-status-box-names-leg.png` - a Director configured with an unreachable Gateway
(`http://127.0.0.1:59999`). The bottom-left status box is RED and names the failing leg on line 1
("Cannot reach the Gateway"); the home card reads "Gateway not connected - The Gateway is
unreachable or unverified" with a Reconnect button. Both ConnectFailed and
WasConnectedNowUnreachable resolve to Step 1, and WasConnectedNowUnreachable outranks ConnectFailed
when `wasEverConnected` is true this run - pinned by the resolver tests (see below).

## 2. Step 1 REPAIR mode with rediscovered address as a one-click fix

`repair-mode-rediscovered-oneclick.png` - clicking the red box (or the home Reconnect) opens the
panel in repair mode: the header reads "Reconnect to your Gateway", the red banner names the exact
failure ("Cannot reach the Gateway at http://127.0.0.1:59999: ... the target machine actively
refused it ... It may have moved - pick its current address below to reconnect"), and the
issue-1233-order rediscovery scan has run automatically and offers the Gateway's CURRENT address as
a one-click fix ("This computer SOREN_NORTH [Recommended]" and "Over Tailscale
soren-north....ts.net"). Picking one reconnects - no separate Test step.

## 3. Inline diagnostics (the troubleshooter, reused, dialog-as-destination gone)

`repair-mode-inline-diagnostics.png` - expanding "Show diagnostics" renders the SAME
`GatewayConnectivitySelfTest` ladder INLINE: the two-way verdict plus every rung (Tailscale up,
Serve mapping present, local listener answers, advertised URL reaches this Director, build versions,
Windows firewall) with its finding and fix. The logic is reused; the `GatewayTroubleshootDialog`
window is DELETED (grep below), and `MainWindow.OpenGatewayTroubleshooter` and the HomeView gateway
click now open the panel instead.

## 4. Re-sign-in lands directly on Step 2

Verified two ways, because a live re-capture against the real Gateway is blocked by its own auth
enforcement (an unauthenticated fresh test device is correctly refused registration with HTTP 401,
so it can never reach a live "connected but signed out" state against the production Gateway):

- The resolver test `Resolve_HandshakeProvenButSignedOut_IsConnectedNotSignedIn_OpensOnSignIn`
  asserts ConnectedNotSignedIn resolves to `GatewayPanelStep.SignIn` (Step 2).
- The panel's routing to Step 2 is UNCHANGED from Phase 2 (approved + screenshotted then):
  `CreateForCurrentState` returns the Done step for a Verified monitor, and `OnAttached` ->
  `RefreshSignedInViewAsync` shows Step 2 whenever the resolved state is not AllGreen. Phase 5 added
  the repair branch alongside this path without touching it.

## Grep proof - the troubleshooter dialog is gone

```
GatewayTroubleshootDialog : 0   (src, excluding bin/obj; both .axaml and .axaml.cs deleted)
```

## Resolver precedence tests (the red-state ordering)

- `Resolve_HandshakeFailedNeverConnectedThisRun_IsConnectFailed_OpensOnConnectRepair`
- `Resolve_WasConnectedThenHandshakeFails_IsWasConnectedNowUnreachable_OpensOnConnectRepair`
- `Resolve_WasEverConnected_OutranksConnectFailed_OnlyWhenFailed`
- InlineData: ConnectFailed -> Connect, WasConnectedNowUnreachable -> Connect
- `Resolve_HandshakeProvenButSignedOut_IsConnectedNotSignedIn_OpensOnSignIn`

30 GatewayConnection Core tests pass; 115 Avalonia tests pass; Avalonia builds with zero warnings.
