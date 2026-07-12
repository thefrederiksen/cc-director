# Gateway Connection - Phase 4 proof (Settings cleanup + deletion list)

Design spec sections 8, 9. The scattered Gateway plumbing is gone; the one reusable
`GatewayConnectionPanel` is now embedded in its three hosts.

## What changed

1. **Settings Gateway tab IS the panel.** The whole old control set is replaced by the embedded
   `GatewayConnectionPanel`, created on the resolver's current step.
   `settings-gateway-tab-embeds-panel.png` shows the tab opened on the Done view (connected +
   signed in), with the Advanced disclosure collapsed. No URL box, Detect, Test, Director-public-URL
   + Detect, plain-text token box, "Connect to Gateway..." button, or "Re-run setup wizard..." button.
2. **Account tab = status summary + one button.** The read-only Gateway/identity summary stays; a
   single "Manage connection..." button selects the Gateway tab (the panel on its current step).
3. **Onboarding wizard Gateway step IS the panel.** `onboarding-gateway-step-embeds-panel.png`
   shows Step 1 of 3 embedding the panel (auto-scan, "2 FOUND", recommended "This computer",
   manual-entry under Advanced), with the wizard's own Skip / Next. Next advances once the panel
   raises `ConnectionVerified`.
4. **Deletion list executed.** The whole `ConnectToGatewayDialog` (`.axaml` + `.axaml.cs`) is
   deleted - signing in with DevThrottle in the panel replaces the pairing code. The manual address
   entry and the masked token survive only inside the panel's Advanced disclosure.

## Grep proof - none of the deleted controls/dialogs remain in source

```
DetectGatewayButton   : 0
TestGatewayButton     : 0
DetectPublicUrlButton : 0
GatewayTokenBox       : 0
ConnectToGatewayButton: 0
RerunOnboardingButton : 0
ConnectToGatewayDialog: 0
GatewayUrlBox         : 0   (was a primary control in Settings + onboarding; the panel's own
GatewayAdvertisedBox  : 0    ManualUrlBox under Advanced is the manual-entry fallback now)
BtnConnectToGateway_Click : 0
BtnRerunOnboarding_Click  : 0
BtnDetectGateway_Click    : 0
BtnTestGateway_Click      : 0
```

(`grep -rn <name> src/ --include=*.cs --include=*.axaml`, excluding bin/obj.)

## Build / tests

- `CcDirector.Avalonia` and `CcDirector.Avalonia.Tests` build clean, zero warnings.
- No test referenced the changed `SettingsDialog` constructor or the deleted dialog; the
  `OnboardingModel.PersistGatewayUrl` Core method (and its tests) are untouched - only the wizard
  stopped calling it, because the panel writes gateway.url on connect.
