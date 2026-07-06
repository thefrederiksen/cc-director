# Mobile App Device Authentication - Specification (Version 1)

Status: DRAFT for review (2026-07-03)
Area: Gateway + mobile Progressive Web App
Tracker: `thefrederiksen/devthrottle` issue #908
Related shipped work: the account-as-device-fabric issue set (#852-#858) and local device
enrollment (#469).

This document specifies how the mobile Progressive Web App (served at `/m`) authenticates a person
and registers the phone as its own revocable device, replacing the current model in which the app is
handed the master Gateway token from a public page.

> **Generalized to the desktop Cockpit (issue #1088, epic #1069).** The flow specified here is no
> longer phone-only: the desktop Cockpit browser (served at the site root) enrolls through the SAME
> shared `client-core` sign-in/callback screens and the SAME `POST /m/enroll` seam, with platform
> `browser` (recorded device type `browser` instead of `phone`) and its own callback route
> `/device-callback`. A signed-out browser navigation to any Cockpit route is redirected to `/signin`
> (this flow), never to the raw-token `login.html` wall. Cross-repo note (#1081): the devthrottle.com
> activation page must accept the non-phone platform and the Cockpit callback path.

---

## 1. The problem (today's model)

The mobile app has no app-level security. Reaching it equals full control of the fleet:

1. The `/m` shell is deliberately public. `AuthMiddleware` lets `/m` and `/m/*` through with no
   token check at all (`src/CcDirector.Gateway/Util/AuthMiddleware.cs`, issue #806).
2. The Gateway injects the per-machine master token into the served `index.html` in place of the
   `__GATEWAY_TOKEN__` placeholder (`src/CcDirector.Gateway/Mobile/MobileApp.cs`). The app reads it
   as `window.__GW_TOKEN__` and sends it as the Bearer on every call (`mobile/src/api/client.ts`).
3. That token is the master credential: a 32-byte value in `gateway-token.txt`, generated once,
   plain text, no expiry, the same credential the desktop uses (`GatewayAuth.cs`). Anyone who loads
   `https://<tailnet>/m`, or simply views page source, walks away with permanent, unrevocable
   control of the whole fleet.

The only barrier is Tailscale network membership. Once a device is on the tailnet - or once a phone
has ever loaded the page - it is root forever, and nothing times out.

---

## 2. Target model (two independent layers)

Security is split into two layers, and the cloud only ever touches the first of them for a moment,
never the traffic:

- **Network reachability.** The phone must have a direct network path to the Gateway. This is the
  owner's to provide: Tailscale today, any other virtual private network, or a local network.
  DevThrottle is not a relay or proxy - no session traffic ever routes through the cloud. This layer
  does not change.
- **Identity and authorization (new).** The person signs in against the central DevThrottle account.
  On success the phone obtains its own **per-device token**. From then on the phone presents that
  token as the Bearer on every direct-to-Gateway call. Being on the network is no longer enough;
  the token is what turns "I can reach the Gateway" into "I am allowed to use it."

Locked decisions from the conversation that produced this spec:

- **A token is required.** Network presence alone must not grant access.
- **The token is minted only after account sign-in against the central server.**
- **No timeout.** The device stays signed in until it is revoked (consistent with the shipped
  "revoke-only, no auto-expiry" device model).
- **No PC presence needed.** Registration is done from the phone via the cloud, never by reading a
  code off the Gateway host window.
- **Per-device, not master.** Losing one phone never compromises anything else; it is revoked in one
  action, and every other device keeps working.
- **The cloud is never in the request path.** After enrollment the Gateway validates the token
  itself, offline, so it stays fast and keeps working if the cloud is briefly unreachable.
- **All sign-in and device registration happen on devthrottle.com** (one fixed address the project
  owns), so email, Google, and GitHub all work the same way. The brief redirect out to a provider's
  own sign-in page (Google / GitHub) at first enrollment is accepted.

---

## 3. The enrollment flow

Sign-in and device registration happen on **devthrottle.com** - one fixed address - so every
provider (email, Google, GitHub) works the same way (see section 5 for why the fixed address is what
makes this possible). The phone is the courier: it is the only thing that can reach both the public
site and the owner's private Gateway.

One time, per phone:

1. The phone opens `/m`. The app finds no stored device token, so it shows a **Sign in** screen
   instead of silently receiving the master token. (The master token is no longer injected - see
   section 6.) The app passes its own `/m` origin along so the site knows where to return the phone.
2. Tapping Sign in sends the phone to **devthrottle.com**. The person signs in with email, Google,
   or GitHub. For Google / GitHub the browser briefly visits the provider's own sign-in page and
   returns to devthrottle.com; that redirect is registered once against the site's fixed address, so
   it works for every owner.
3. devthrottle.com registers this phone as a device on the account and issues its per-device token,
   then returns the phone to its `/m` origin carrying that token. (This return hop is the site's own
   redirect, not the provider's, so it is free to target any owner's `/m` address - see section 5.)
4. `/m` hands the token to a new, deliberately public enrollment endpoint on the Gateway (public the
   same way `/devices/register` is, because it carries its own authorization).
5. The Gateway verifies the token identifies **the same account the Gateway itself is signed into**
   (it already knows its own account identity locally, `GatewaySignInService.GetIdentity()`). On a
   match it issues the phone a **local** device key it can validate offline and mirrors the phone to
   the account's cloud roster (so it appears under "Your devices" and is revocable there). See
   section 4 for why the working credential is a local key.
6. The phone stores its key and uses it as the Bearer from then on. No further sign-in, no expiry,
   until it is revoked.

Every real request after enrollment - roster, prompts, the terminal stream - is validated by the
Gateway locally and goes straight phone-to-Gateway over the network.

---

## 4. The bridge (the one genuinely new mechanism)

The hard part is step 4-5: how a **local** Gateway comes to trust a phone that authenticated against
the **cloud**. Two shipped facts constrain the design:

- The existing Gateway sign-in uses a **local loopback browser callback**
  (`GatewaySignInService` + Core `FirstRunLoginCoordinator` + `LoopbackLoginListener`). That only
  works when the browser is on the same machine as the Gateway. A phone browser is remote, so it
  **cannot reuse** this flow. The phone must authenticate to the cloud directly.
- The cloud device **list** returns only masked keys (`KeyPrefix` + `KeyLast4`), never raw keys
  (`AccountDevicesEndpoint.cs`). So the Gateway cannot validate an arbitrary cloud-issued key by
  syncing the roster and comparing - there is nothing to compare against locally.

### Recommended design: local per-device key, mirrored to the cloud

The phone's working credential is a **local device key** that the Gateway can validate offline,
while the cloud copy exists only for visibility and revocation:

- **Verify identity at enrollment (cloud touched once).** The phone signs into the account and
  presents its account access token to the Gateway's enrollment endpoint. The Gateway confirms the
  token belongs to its own account (verify the token's signature and subject against the account
  service, or call the account "who am I" endpoint once). This is the only cloud interaction on the
  enrollment path and it never repeats per request.
- **Issue a local key the Gateway already accepts.** On a verified match, the Gateway registers the
  phone in the local device registry (`DeviceRegistry.Register`, issue #469) and returns that
  per-device key. `AuthMiddleware.HasValidToken` **already accepts a valid local device key as a
  Bearer** (`AuthMiddleware.cs`, the `IsValidDeviceKey` branch), so no new hot-path validation code
  is needed and the cloud is never called to authorize a request.
- **Mirror up for website visibility and revoke.** The Gateway mirrors the enrolled phone to the
  account's cloud roster using the existing Path B mirror (`ChildDeviceMirrorService`), so the phone
  shows under "Your devices" and can be revoked from the website or the Cockpit. A revoke propagates
  back down through the existing mirror/reconcile sweep, after which the Gateway rejects that key.

Why this over having the phone hold a cloud `dtd_` key directly: a cloud key cannot be validated by
the Gateway offline (the list is masked), so it would force a cloud call on every request - which
violates the "cloud is never in the request path" decision. The local-key-plus-mirror design keeps
authorization entirely local while still giving cloud-side visibility and one-click revoke.

### What already exists, and the one security fork (findings 2026-07-04)

Most of this is already built and can be reused:

- devthrottle.com already ships `/activate` (issue #106): a device-approval page that signs the
  person in with **any** provider (Google / GitHub / email / magic link), registers the device via
  `POST /api/v1/devices/register` (mints the per-device `dtd_` key), and hands the credential back to
  the app. `/signin` (issue #46) is the same hand-back contract without the approval metadata.
- The hand-back helper `buildCallbackUrl` (website `src/lib/loopback.js`) already supports returning a
  `device_key` alongside (or instead of) the session tokens.
- On the Gateway, `DeviceRegistry.Register` / `IsValidDeviceKey` issue and offline-validate a local
  key, and `DevThrottleAccountService` verifies a JWT signature and reads its identity locally.

The one genuinely new decision: `validateRedirectUri` (website `src/lib/loopback.js`) is
**deliberately loopback-only** (`http://127.0.0.1` / `localhost`, the one callback path) as the
anti-token-exfiltration control - handing a Supabase session to an arbitrary URL is account theft.
The phone's callback is `https://<tailnet>/m/...`, not loopback, so the phone flow cannot use the
existing hand-back unchanged.

**Chosen resolution (2026-07-04, owner to veto):** hand back to the phone's tailnet callback **only
the revocable per-device key, never the Supabase session tokens.** Worst-case interception then costs
one revocable device, not the account. The phone presents that key once to the new Gateway enrollment
endpoint, which confirms the key belongs to the Gateway's OWN signed-in account (matching it against
the account device roster it can already read via `DeviceRegistryClient`), then issues the phone a
**local** `DeviceRegistry` key for day-to-day use. A phone variant of the redirect validation accepts
the Gateway's own tailnet `/m` callback for a **device-key-only** hand-back (never a session
hand-back), keeping the strict loopback rule for the token path intact.

Alternative considered: hand back the full session, but only to a redirect origin pre-registered
under the same account (the Gateway publishes its tailnet URL to the cloud). Also secure; rejected for
V1 as more cloud plumbing with no better outcome than the device-key-only path.

### Alternative (documented, not recommended)

Have the cloud mint the phone's key and add a new cloud endpoint that lets the Gateway validate a
presented key by hash (so it can sync hashes and check locally). This is more cloud surface, more
sensitive data to sync, and no better an outcome than the recommended design. Recorded only so the
trade-off is on the record.

---

## 5. Why registering on devthrottle.com makes every provider work the same (DECIDED)

Signing a **phone browser** in directly against the app would hit one wall: the app is served from a
per-owner tailnet origin (for example `https://<tailnet-name>/m`), and social sign-in
(Google / GitHub) requires the exact **return address** to be registered in advance with the
provider. A different tailnet origin per owner cannot be pre-registered for everyone.

The decision (owner, 2026-07-03) is to sidestep this entirely by doing **all sign-in and device
registration on devthrottle.com**, one fixed address the project owns. This works because:

- The provider's return-address rule only governs the leg **from the provider back to us**. We
  register devthrottle.com's fixed callback with Google / GitHub **once**, and it is satisfied for
  every owner forever.
- Once the person is back on **our own** site, the hop from devthrottle.com back to the phone's `/m`
  address is **our** redirect, not the provider's - so it is not subject to the provider's
  registered-address list and can target any owner's tailnet `/m` origin.

So email, Google, and GitHub all become "sign in on devthrottle.com," i.e. identical. The owner has
accepted the brief redirect out to a provider's own page for Google / GitHub (it is inherent to those
sign-ins - the app never sees the provider password). This is a **one-time** step per phone, because
there is no timeout (section 7).

Cost of this decision (recorded honestly): it adds a device-registration surface on devthrottle.com
and a token hand-back to the phone, so it **does** require cloud/site work - this reverses the
earlier "probably no cloud change" assumption. It also means the phone needs internet reachability at
enrollment time (it needs the account to sign in regardless); after enrollment it is local-only.

---

## 6. Change to the `/m` shell (stop handing out the master token)

`MobileApp.cs` must stop injecting the master token into `index.html`. The shell still loads
publicly (it must, so the Sign in screen can render), but it no longer carries any credential. The
app authorizes its own calls with the per-device token it obtained at enrollment.

Backwards-compatibility note for the Developer Agent: confirm whether any surface other than the
Progressive Web App relies on `window.__GW_TOKEN__` injection before removing it. The native phone
application authenticates separately and is not in scope here.

---

## 7. Revocation and "keep logged in" semantics

- **Keep logged in:** the per-device token never expires. The account session on the phone
  auto-refreshes, so the person signs in once and is not asked again.
- **Revoke one device:** removing the device from the website or the Cockpit ("Your devices" ->
  Remove) drops it from the account roster; the mirror/reconcile sweep removes the local key and the
  Gateway then rejects that token on its next request. Every other device is unaffected.
- **Lost phone:** the person revokes that one device. The phone's own lock screen is the near-term
  protection; revoke is the durable one. No fleet-wide credential rotation is ever required.

---

## 8. Reused shipped components

| Concern | Existing component | Reused how |
|---|---|---|
| Local per-device key issue + validate | `DeviceRegistry` (#469), `AuthMiddleware.HasValidToken` | Phone gets a local key; the Bearer check already accepts it |
| Account identity on the Gateway | `GatewaySignInService`, `DevThrottleAccountService` | Read the Gateway's own account to verify the phone's token matches |
| Cloud device roster (list / revoke) | `AccountDevicesEndpoint`, `DeviceRegistryClient` | The phone appears under "Your devices"; revoke from the website |
| Mirror a device up / revoke down | `ChildDeviceMirrorService` (Path B) | Mirror the enrolled phone to the cloud; propagate a revoke back down |
| Serve `/m` | `MobileApp.cs` | Same serving; remove token injection only |

---

## 9. New work

- **devthrottle.com (site / cloud):** a device-registration surface reached from the phone that
  signs the person in (email, Google, GitHub - the provider callbacks registered once against the
  site's fixed address), registers the phone as a device on the account, issues its per-device
  token, and returns the phone to its `/m` origin carrying that token.
- **Gateway:** a public enrollment endpoint under `/m` that verifies the presented account token
  against the Gateway's own account and, on a match, issues a local device key and mirrors the phone
  up. Stop injecting the master token in `MobileApp.cs`.
- **Mobile:** a Sign in screen that hands off to devthrottle.com (passing its own `/m` origin) and
  receives the token back; store the returned per-device token; send it as the Bearer (replacing the
  injected-token path in `mobile/src/api/client.ts`); a signed-out state that routes to Sign in on a
  401.

---

## 10. Out of scope (Version 1)

- The native phone application's authentication - unchanged; it is a separate surface.
- Any change to the network layer (Tailscale, virtual private network, local network) - unchanged.
- Multi-person accounts / roles - the model is one account owning many devices.

---

## 11. Proposed issue breakdown (to confirm during build)

Small, independently testable steps, each proven on a real phone before the next:

1. **devthrottle.com device-registration surface.** Sign in (email, Google, GitHub via the site's
   fixed callback), register the phone as a device, mint its per-device token, and return it to a
   given `/m` origin. Provable on the site before any Gateway change.
2. **Gateway enrollment endpoint + stop injecting the master token.** Verify a presented account
   token against the Gateway's own account; issue a local device key; mirror the phone up. `/m` shell
   loads with no credential. Provable with a token round-trip and the Bearer check.
3. **Mobile Sign in screen + token storage + authorized calls.** Hand off to devthrottle.com and
   receive the token back; store the per-device token; every existing call sends it; a 401 routes
   back to Sign in.
4. **Revoke round-trip.** Removing the phone from "Your devices" causes the Gateway to reject its
   token; the app returns to Sign in. Proven end to end.

---

## 12. Assumptions (flagged for the human, per the Definition of Ready)

- Version 1 includes email, Google, and GitHub, all via devthrottle.com (section 5, DECIDED).
- Cloud/site work **is** required: a device-registration surface on devthrottle.com plus the token
  hand-back to the phone. (This reverses the initial "probably no cloud change" guess.) The exact
  split between reusing shipped account/register endpoints and new site pages is a design task.
- The recommended bridge (local key plus cloud mirror) is accepted over the cloud-key alternative
  (section 4).
- The native phone application does not depend on the `/m` master-token injection being removed.
</content>
</invoke>
