# Mobile Device Authentication - Security Model

Status: reference for security audit (2026-07-04)
Scope: how the mobile Progressive Web App (served at `/m`) authenticates to the Gateway (issue #908).
Companion to the design spec `MOBILE_DEVICE_AUTH.md` in this folder.

This document states the credentials, the trust boundaries, the flows, the threats each control
defends against, and the known gaps. It is written to be read cold by a reviewer.

---

## 1. Credential inventory (who holds what, and who NEVER sees what)

There are FOUR distinct credentials. Keeping them distinct is the core of the model.

| # | Credential | Minted by | Lives where | Used for | NEVER sent to |
|---|-----------|-----------|-------------|----------|---------------|
| A | Supabase session (access + refresh JWT) | Supabase (on sign-in) | The phone browser, `devthrottle.com` origin only | Proving the person to devthrottle.com during sign-in | The Gateway. Ever. |
| B | Cloud device key (`dtd_...`) | devthrottle.com (`/devices/register`) | Handed to the phone once (URL fragment) | One-time enrollment proof to the Gateway | Stored long-term; discarded after enrollment |
| C | Local device key | THIS Gateway (`DeviceRegistry`) | The phone (localStorage, `/m` origin) | The Bearer on EVERY Gateway request | The cloud. Ever. |
| D | Gateway account token | Supabase (Gateway's own sign-in) | The Gateway (OS credential store) | The single "verify" call to the cloud at enrollment | The phone. Ever. |

The security win over the previous model: the old `/m` shell was served with the **master Gateway
token injected into the page**, so reaching the URL (or viewing page source) was full, permanent,
unrevocable fleet control. That token is gone from the shell. The phone now holds only (C) - a local,
per-device, individually revocable key that is not the master token and not a cloud credential.

---

## 2. Trust boundaries (what crosses which line)

```
   PHONE BROWSER                 PUBLIC INTERNET              OWNER'S NETWORK (Tailscale/VPN/LAN)
   ============                  ===============              ===================================
   /m origin (the app)           devthrottle.com  <--(D)-->   THIS GATEWAY (devthrottle-gateway.exe)
     holds (C)                   + Supabase                     holds (D), issues + validates (C)
     |                             holds/mints (A),(B)          talks to Directors (fleet token)
     |                                                          |
   devthrottle.com origin  <-- (A) lives ONLY here             DeviceRegistry (the (C) issuer/record)
     (separate browser origin;
      the /m app cannot read it)
```

- (A) never leaves the `devthrottle.com` browser origin. The `/m` app is a different origin and cannot
  read it. So the account session cannot be exfiltrated to the Gateway or through it.
- (B) crosses from devthrottle.com to the phone ONCE, in the URL **fragment** (`#device_key=...`), which
  is not sent to any server, not logged, and not in the Referer header. It is presented to the Gateway
  once and never reused.
- (C) never leaves the owner's network. Every request bearing it is `phone -> Gateway` over Tailscale/
  VPN/LAN. The cloud is not in the request path.
- (D) never leaves the Gateway except as the Bearer on the single cloud "verify" call.

---

## 3. The flows

### Enrollment (one time per phone)

```
 PHONE (/m)                 DEVTHROTTLE.COM (cloud)               THIS GATEWAY
 ----------                 ----------------------                ------------
   tap Sign in ------------> you sign in (Google/GitHub/email)
                            -> Supabase issues (A)
                            -> register phone, mint (B)
   #device_key=(B) <-------- (URL fragment; NO (A) tokens handed back)
   POST /m/enroll { (B) } ------------------------------------------>
                            cloud <---- verify (B), authorized by (D) ----
                            cloud ----- "is (B) a live device on         |
                                         (D)'s account?" -> id or null -->|
                                                              if id: mint (C),
                                                              map (C) -> cloud id
   { deviceKey=(C) } <---------------------------------------------------
   store (C); discard (B)
```

Key property: the Gateway only issues (C) when the cloud confirms (B) belongs to **the Gateway's own
account** (account-scoped, by full-key hash - not a masked prefix/last-four compare). A device key for a
different account returns "not a match" and enrollment is refused (403).

### Steady state (every request - the cloud is not involved)

```
 PHONE --- GET /sessions   Authorization: Bearer (C) ---> GATEWAY
                              validate (C) offline against DeviceRegistry -> 200
 (no cloud call; fast; works even if the cloud is unreachable)
```

The terminal WebSocket cannot set an Authorization header, so it authenticates via a `cc-gateway-token`
cookie carrying (C); `AuthMiddleware` accepts (C) on both the Bearer header and that cookie.

### Revoke (kill one phone)

```
 Website "Your devices" -> Remove
   -> cloud roster drops the phone
   -> Gateway's periodic reconcile sweep sees it gone
   -> deletes local key (C) from DeviceRegistry
   -> phone's next request -> 401 -> app clears (C) -> returns to Sign in
```

Revocation is per-device: removing one phone never affects any other device or the master token.

**Guaranteed revocation-latency bound (issue #924).** Revocation is PULL-based, not push: there is no
inbound cloud-to-Gateway channel (sub-second/instant revoke is a Non-goal of epic #916 - it would need a
persistent relay). A website revoke reaches the Gateway only on its next OUTBOUND reconcile sweep, so the
bound is exactly **one sweep interval - `GatewayHost.CronSweepInterval`, ~1 minute**. A revoked device may
keep working up to that one interval and is refused on the first request after the sweep that drops its
key; it is never refused sooner (nothing pushes the revoke) and never later than one sweep (the sweep is
periodic). Under host-wide enforcement (issue #917) that refusal is a hard `401`.

**Revoke-propagation failures are visible (issue #924).** The reconcile sweep no longer swallows a broken
revoke path into an indistinguishable retry line. `ChildDeviceMirrorService` counts consecutive reconcile
failures and exposes `HasPersistentReconcileFailure` (plus `ConsecutiveReconcileFailures` /
`LastReconcileError`) for the Cockpit/tray to read, and logs a distinct escalated signal once the path is
persistently stuck. That status is also set when the Gateway's account-token refresh is persistently
failing (the issue #911 signal) - the case where reconcile has no usable token and would otherwise skip in
silence. So a "website revokes are not reaching this Gateway" condition surfaces instead of retrying
forever unseen.

---

## 4. What each control defends against

| Threat | Control |
|---|---|
| Reaching `/m` grants access | Shell carries no credential; the app must present (C), which it only has after enrollment |
| Someone enrolls a phone on another person's Gateway | Enrollment verifies (B) belongs to the Gateway's OWN account (D) |
| A tailnet party brute-forces an enrollment | Verify is a full-key hash match (not the ~24-bit masked last-four), and account-scoped |
| Account-session theft via the sign-in redirect | (A) never leaves the devthrottle.com origin; only (B) crosses to the phone, in the fragment |
| A leaked phone key compromises the account/fleet | (C) is one revocable device key; it is not (A), (B), (D), or the master token |
| A stolen/lost phone keeps access | Revoke the one device from the website; the reconcile sweep drops (C) |
| Cloud outage locks the phone out | Steady-state validation of (C) is fully local; no cloud dependency after enrollment |

---

## 5. Known gaps (open audit items)

> Update (issue #924): epic #916 has since landed its enforcement and refresh phases. Gap 1 is closed by
> Phase 1 (#917 - the host-wide auth gate is now ON by default) and gap 2 by Phase 3 (#911 - the refresh
> exchange now sends the `apikey`). Phase 4 (#924) additionally makes revoke-propagation failures visible
> (see the Revoke section above). The gaps are retained below as the original audit record; gap 3 (a live,
> end-to-end enforced audit across the whole fleet, including the owner's real-phone revoke round-trip)
> remains the epic's real done-check and is owner-run.

1. **Host-wide enforcement is OFF by default.** The device-key check only *enforces* when the Gateway
   runs with `CC_GATEWAY_AUTH=1`. With it off (the shipped default; the tailnet is the boundary today),
   the Gateway accepts (C) but does not *require* a credential - an unauthenticated request on the tailnet
   still succeeds. Turning it on is a fleet-wide change (Cockpit login, cc-* tools, Directors, native
   app) and needs a smoke test. **This is the most important audit item.**
2. **Gateway account-token refresh is broken (#911).** The Gateway's `GatewayHttpTokenRefresher` does not
   send the Supabase `apikey` on the refresh exchange, so the account token (D) dies ~1 hour after each
   sign-in and every cloud call 401s until a manual re-sign-in. `signedIn` still reports true (local
   check only), masking it.
3. **Enforcement mode not yet audited end to end.** The 401-on-missing-credential path is unit-tested,
   but no live audit has been run with `CC_GATEWAY_AUTH=1` across the whole fleet.

---

## 6. Audit checklist

- [ ] Confirm the served `/m` shell contains no token (`GET /m` body has no `__GATEWAY_TOKEN__`/master token).
- [ ] Confirm `/m/enroll` refuses a device key from a different account (403), and a random string (403).
- [ ] Confirm the mobile hand-back carries only (B) in the fragment - never (A) (`buildMobileCallbackUrl`).
- [ ] Confirm `validateMobileRedirectUri` pins the callback path and the session path stays loopback-only.
- [ ] With `CC_GATEWAY_AUTH=1`: confirm `GET /sessions` is 401 without a credential and 200 with (C).
- [ ] Confirm revoke from "Your devices" drops (C) and the phone returns to Sign in.
- [ ] Confirm the local `DeviceRegistry` holds no stray/test keys (issue: test isolation, now fixed).
- [ ] Confirm no credential (C)/(D)/device key is written to any log (search logs for key values).
</content>
