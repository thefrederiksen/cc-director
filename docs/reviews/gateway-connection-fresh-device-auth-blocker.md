# BLOCKER: a fresh (token-less) cc-director cannot sign in / connect

Status: LIVE BLOCKER found during the Windows go-live (Part B). This blocks ALL go-live of the new
cc-director build - none of it is usable until a brand-new device can reach green with only the
DevThrottle sign-in. Soren and the Manager are working this together as the single priority.

## Symptom

A brand-new Director (no device token yet) opens the connection panel, the issue-1233 scan finds the
real Gateway, the user picks "This computer", and the handshake dies with:

> Could not finish connecting - Gateway refused registration: HTTP 401 Unauthorized

with only "Try again" / "Back to scan". There is NO way to sign in from that screen.

## Root cause - the fresh device is locked out on all three paths

The Gateway runs with auth enforcement ON. A device must already hold a device token before the
Gateway will talk to it. But a brand-new device has none, and every path that would let it obtain
one is behind the SAME token wall (`AuthMiddleware`, path-based allow-list, no loopback exemption):

- `POST /directors/register` (the connect handshake) -> 401 (this is the visible error)
- `GET /account/status` (what the sign-in step polls) -> gated
- `POST /devices/enroll-signed-in` (the loopback endpoint whose whole job is to hand a fresh
  co-located Director its first token) -> gated, even though it has its OWN hard guardrails
  (proven-loopback caller + Gateway-signed-in)

Only the browser sign-in SURFACES are public (`/account/sign-in-start`, the callback, `/signin`,
`/device-callback`) - not the Director's device-enrollment API. So the device needs a token to get
in and must get in to obtain a token.

The UI compounds it: a 401 is shown as a dead-end failure instead of "sign in to authorize this
device".

## The north star (Soren, confirmed) - epic #1069

The DevThrottle account sign-in is the ONE and ONLY thing the user does, and it must work on ANY
machine, not just the Gateway's machine. The cloud account = the single source of auth (the
Tailscale model). Signing in once issues THIS device its own key, tied to the account; the Gateway
trusts account-issued keys.

## What exists vs. what is missing

- EXISTS (Phase 2): same-machine LOOPBACK enrollment - `/devices/enroll-signed-in` mints the
  co-located Director a key because the Gateway is signed in. It is just gated behind the auth wall.
- MISSING (explicitly deferred): the REMOTE path. `AccountSignInStartEndpoint`'s own comment says it:
  "the remote-vs-loopback redirect mechanics of the flow itself are a separate follow-up (epic #1069,
  issue '0b'); here the start reuses the existing host-local loopback mechanism unchanged." So a
  Director on another machine gets no key back today.

## Proposed shape (for the Architect to design / correct)

### A. Same-machine unblock (immediate - so Soren can reach green on THIS machine today)

1. `AuthMiddleware`: let a PROVEN-LOOPBACK caller reach the two self-guarded endpoints
   `/devices/enroll-signed-in` (already checks loopback + signed-in) and `/account/status`. Either add
   a narrow loopback exemption, or make enroll-signed-in public like `/devices/register` already is
   (it carries its own authorization). A remote attacker still gets 403 from the endpoint's own
   loopback guard, so the trust model is not weakened.
2. Panel: on a 401/Unauthorized connect failure, route to Step 2 "Sign in with DevThrottle" instead
   of the dead-end. After sign-in enrolls the device and issues the token, re-apply -> register
   succeeds -> green.

### B. Any-machine flow (the real feature - epic #1069 "issue 0b")

A Director on machine B opens the DevThrottle CLOUD sign-in in machine B's browser -> the user signs
in once -> the cloud issues device B its own key, tied to the account -> the Director captures it
(loopback hand-back on machine B) -> authenticates to the Gateway with it. Design questions for the
Architect:

- How the cloud issues per-device keys bound to the account, and how the Gateway validates
  account-issued keys (does the `Devices` registry sync from the account / cloud, or does the Gateway
  verify against the cloud on first use?).
- The redirect / loopback hand-back mechanics for a device NOT co-located with the Gateway.
- Whether the same-machine loopback enrollment stays as a fast path, or the cloud flow is used
  uniformly.

## Acceptance

A brand-new cc-director on ANY machine, with only the user's DevThrottle sign-in, reaches two green
checks (Connected + Signed in), registers in the fleet, and can create sessions - proven live.
