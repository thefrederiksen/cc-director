# Gateway Connection - QA punch list

A running list of deferred cosmetic and quality items found during the mission, to be resolved by
the end and folded into the final verification report (definition-of-done item 5). Kept so nothing
is lost between phases. Append new items as they are found.

## Open

- **Remote / headless Director sign-in enrollment** (deferred from Phase 2). Phase 2 ships the
  co-located (same-machine, loopback) enroll-through-the-Gateway path only. A Director that is NOT on
  the Gateway's machine (remote or headless) still needs a way to sign in and receive its own
  per-device token - the tailnet-callback path, mirroring the phone's remote enrollment. Design and
  build as a Phase 2 follow-up.
- **Delete the legacy pairing dialog** (Phase 4). `ConnectToGatewayDialog` and its "Connect to
  Gateway..." pairing-code entry are superseded by the sign-in Step 2; remove them from the user
  interface as part of the Phase 4 settings cleanup / deletion list. (The Gateway's pairing-code
  endpoint `/devices/register` can stay for now; this is about the desktop UI surface.)

## Resolved

- **Step 1 Advanced disclosure trailing cell** (found Phase 1, Architect review of PR #1327). The
  collapsed "Enter the address manually" row used Avalonia's default `Expander` chrome, which left a
  thin trailing bordered cell on the right and put the caret mid-row, so the row did not span full
  width like the option rows above. Fix: replaced the `Expander` with a full-width `ToggleButton`
  disclosure (caret flush right, no stray cell). Written during Phase 1; lands and is
  screenshot-verified with the Phase 2 pull request.
