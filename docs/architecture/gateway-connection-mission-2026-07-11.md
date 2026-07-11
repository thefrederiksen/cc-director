# Mission Brief: Gateway Connection

Status: active mission. Written 2026-07-11 by the Architect session ("Architect - Gateway Connection",
session 4e603d07, machine SOREN_NORTH). This document is the Architect's handover to the Manager
session. The Manager owns execution from here; the Architect does not gate the Manager.

## The mission

Replace the desktop Director's Gateway setup - today a pile of settings (three address boxes, a
Detect button, a Test button, another Detect button, a plain-text token box, a separate
"Connect to Gateway..." pairing dialog, and a "Re-run setup wizard..." button) - with ONE guided
flow in ONE place, built on a hard split between two separately-verified states:

- CONNECTED: this Director can reach the Gateway, proven by the existing two-way nonce handshake.
- SIGNED IN: this device is trusted by the Gateway (device key valid) and the Gateway reports a
  signed-in account.

The flow must be reachable both from the Settings dialog and from a single status box in the
bottom-left corner of the main window, where one click always takes the user to the next
unfinished step. First-time setup and re-sign-in are the same flow. The result must be so simple
that a brand-new user on a brand-new machine gets from first launch to fully connected by doing
three things: click the amber box, click Connect, type the pairing code.

Source document (read it before starting):

- docs/reviews/gateway-connection-redesign-2026-07-11.md - the full design: the two-state
  model, ASCII wireframes of every screen and state, the three flows (first-time, re-sign-in,
  Gateway moved), the deletion list, the implementation map with file locations, and the
  five-phase plan with proof requirements. That document is the specification; this brief is
  the handover wrapper around it. (The original HTML draft was lost in a session move on
  2026-07-11; this markdown specification, grounded in the current code, supersedes it.)

The design was verified against the working tree on 2026-07-11 by the Architect: the two
verification sources already exist (GatewayConnectionMonitor for the handshake,
GatewayAccountStatusClient for account status), the two stacked indicator boxes live in
MainWindow.axaml around lines 251 and 286 with their presenters in MainWindow.axaml.cs, the
Gateway settings tab is SettingsDialog.axaml lines 150 to 204, and the onboarding wizard
duplicates the URL-plus-Detect-plus-Test controls in OnboardingWizardDialog.axaml.

## Roles and rules of the mission

- The Architect (this document's author) settled the design in the source document above. Do not
  re-open design questions it already answers; the design document is the specification.
- The Manager (you) owns execution: sequencing, spawning workers, reviewing their work, merging,
  and the final verification report. You are allowed to spawn agent sessions yourself
  (cc-devthrottle session spawn <repo> --controlled-by self ...) - that is the DevThrottle rule.
- Escalate to Soren only for product decisions the design document does not answer. Do not stop
  the whole mission to wait; keep working everything that is not blocked.
- Work fully autonomously otherwise.

## Decisions already made - do not re-litigate

1. Two user-visible checkpoints, no more: Connected and Signed In. Device pairing is the
   Director-side half of "signed in", not a third checkbox.
2. The Director never gates on any of this (issues 641 and 664 removed the Director login;
   the app always boots and works locally). The status box is a nudge, never a wall.
3. The Director never holds account credentials (epic 1069: the account lives at the Gateway).
   The Director's part is device pairing plus watching GET /account/status.
4. Green is earned, never assumed: the Connected check comes only from the proven two-way
   handshake; the Signed-in check only from the Gateway's own report.
5. Scanning is automatic (the Detect button dies); connecting is the test (the Test button dies).
   Manual address entry stays available as the fallback behind a link.
6. The Director public URL is auto-detected and lives under a collapsed Advanced section; it
   surfaces as a question only when the callback leg fails and auto-detection cannot resolve it.
7. The device key / token is never displayed in the clear - masked, under Advanced.
8. One panel, three hosts: the Settings Gateway tab embeds it, the status-box click opens it in
   a window, the first-run onboarding wizard embeds it as its Gateway step. One control, no copies.
9. The two bottom-left boxes (GATEWAY and ACCOUNT) merge into one box with two check lines.
   One click target, one destination, any state.
10. The troubleshooter dialog stops being a click destination; its diagnostics render inline in
    the panel's repair mode.
11. No fallback programming; fail loudly with a named failing leg and a specific fix.
12. Plain English everywhere - no abbreviations, no jargon. ASCII only in code and output.
13. Windows first: build and human-verify every phase on SOREN_NORTH against the real Gateway;
    the Mac gets a single verification pass at the end (the code is one Avalonia codebase, so
    there is no porting step).

## The work, in phases

Each phase ships alone: implemented, merged to origin/main per the trunk rule, deployed to a
real machine, and clickable by Soren before the next phase begins. The design document carries
the full detail and proof requirement for each.

- Phase 1 - The panel, connect step. New GatewayConnectionPanel user control with the state
  resolver and Step 1: automatic scan (this machine, tailnet, local network - the issue 1233
  discovery order), found Gateways as one-click picks, manual entry fallback, live handshake
  progress, named failures. Reachable from a temporary menu entry for testing.
- Phase 2 - Sign-in step. Step 2: pairing-code entry (absorbing the register-device dialog) and
  the open-the-account-page path that polls account status and auto-advances. Done view with the
  collapsed Advanced section.
- Phase 3 - One status box. Merge the two bottom-left boxes into one GatewayStatusBox with two
  check lines and four visual states (not connected, connected only, all green, was-working-now-
  unreachable red). Click opens the panel on the current step.
- Phase 4 - Settings cleanup. Gateway tab becomes the embedded panel; Account tab becomes a
  status summary plus one button; delete Detect, Test, the plain-text token box, the register
  button, and the re-run-wizard button; the onboarding wizard embeds the panel.
- Phase 5 - Repair mode. Troubleshooter diagnostics inline in Step 1; changed-address
  rediscovery offered as a one-click fix; red-state routing; re-sign-in lands directly on Step 2.

## Definition of done for the mission

1. All five phases merged to origin/main, each with unit tests for the state resolver and
   presenters, and each human-verified on this Windows machine against the real Gateway.
2. The deletion list from the design document is fully executed - none of the removed buttons,
   boxes, or dialogs are reachable from the user interface.
3. The end-to-end success test passes on a fresh profile: from clean start to two green checks
   without typing a URL - click the amber box, click Connect, type the pairing code.
4. The Mac verification pass is explicitly scheduled as the final step (network discovery,
   browser launch, and device-key storage path are the three platform-sensitive spots).
5. A final verification report (HTML, in docs/reviews/) showing every state and step with
   screenshots from the running app.
