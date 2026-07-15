# Snooze lengths proof

Drives the REAL Cockpit snooze editor in a real Chromium against a fake Gateway, and writes the
evidence it asserts on. Run it from this directory:

```
node run-proof.mjs
```

It prints PASS or FAIL per claim, exits non-zero on any failure, and writes `evidence-<date>.json`
plus the screenshots beside it.

## What is real and what is not

REAL: the shipping `SnoozeCard` component, the shipping `settingsClient`
(`getGatewaySettings` / `setSnoozePresets`), the shipping `snoozeFormat` helpers, and the Cockpit's own
`settings.css` (served straight from the Cockpit source, so the screenshot cannot drift from what ships).

SIMULATED: the Gateway. An in-page fetch shim answers `GET /gateway/settings` from its own stored list
and records every `PUT /gateway/snooze-presets` body. It applies each PUT to that state and answers the
next read from it, exactly as a real Gateway would, so the card re-renders from the server's truth rather
than from local optimism.

This proves the CLIENT flow. The Gateway's own storage, validation, and the invariant it enforces are
proven separately in C# by `SnoozePresetsConfigTests` and the Gateway end-to-end suite. This is not the
real Gateway and not the phone.

## The claims

- **A** - the list renders the Gateway's lengths, in the Gateway's words ("15 minutes" through
  "8 hours"), with the dot on the Gateway's default.
- **B** - picking a different row sends ONE PUT carrying BOTH the list and the new default, so the two
  can never be written apart, and the dot lands on the picked row.
- **C** - adding a length sends the widened list and keeps the existing default.
- **D** - removing the row that holds the dot moves the dot to the shortest remaining length, rather than
  leaving the default off the menu.
- **E** - the last remaining length cannot be removed, so the menu can never be empty.
- **F** - "Add a length" is disabled once the menu is full.

Claims B, D, and E are the interesting ones: they are the client half of the invariant that the default
snooze length is always one of the lengths the menu offers.
