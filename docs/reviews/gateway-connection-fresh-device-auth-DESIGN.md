# DESIGN DECISION: fresh (token-less) cc-director sign-in / enrollment

Architect decision on the blocker in `gateway-connection-fresh-device-auth-blocker.md`. Approves a
minimal, correct **A** (same-machine unblock, ship today) and sets the design direction for **B**
(any-machine, epic #1069 "issue 0b" - a follow-up mission, NOT today).

## The deadlock, stated exactly

`GatewayEnrollmentClient.EnrollSignedInAsync` sends the Director's EXISTING Gateway token as the
Bearer. A brand-new Director has none, so the request arrives token-less and `AuthMiddleware` 401s it
at the path gate BEFORE it can reach the endpoint's own guardrails. The endpoint that exists to hand
a fresh co-located device its FIRST key is unreachable precisely by a device that has no key. That is
the trap.

## A. Same-machine unblock (approved - ship today)

### A1. AuthMiddleware: make `/devices/enroll-signed-in` public - and ONLY that endpoint

Add `"/devices/enroll-signed-in"` to `AuthMiddleware.PublicPaths`. This is the identical pattern the
public set already documents for `/devices/register` (#469) and `/account/sign-in-start` (#1076):
**the endpoint carries its own authorization**, so opening the route does not weaken the trust model.
Its guardrails (`SignedInEnrollmentEndpoint.Evaluate`) are transport-level and self-contained:

- Guardrail 1: caller's `RemoteIpAddress` must be loopback -> a tailnet/LAN attacker gets **403** from
  the endpoint itself. Never a self-asserted header/flag.
- Guardrail 2: the GATEWAY must be signed in -> **409** otherwise.
- Guardrail 3: idempotent `RegisterIfAbsent` -> mints at most once per device (the #1136 leak guard).

A remote attacker therefore gains nothing (403), and a local process is already inside the endpoint's
designed trust boundary (loopback = same machine = physically trusted - the same bar `enroll-signed-in`
was built to). The token wall in front is pure redundancy that only creates the deadlock.

### A2. DO NOT open `/account/status` (correction to the brief's proposal A.1)

The brief proposed opening BOTH `enroll-signed-in` and `/account/status`. Reject the second.
`/account/status` returns account DATA (email, provider, credits); it must stay credential-gated. The
device does not need it public: it enrolls first (public, loopback-guarded), earns its per-device key,
then polls `/account/status` WITH that key. Opening account data to any unauthenticated caller would be
a real weakening for zero benefit. Only the self-guarding enroll endpoint opens.

### A3. Panel orchestration: token-less -> loopback enroll FIRST, browser sign-in only on 409

The connect flow, when THIS Director holds no local Gateway key, must attempt loopback enrollment as
the way to earn one - not dead-end on the register 401. Branch on the enroll result:

- **200** (key minted or returned): persist the key to the local credential file (same as the pairing
  path did), then register/heartbeat succeeds -> both checks green. On a machine whose Gateway is
  already signed in (the common same-machine case, e.g. SOREN_NORTH today) this reaches green with
  **zero** user clicks after "This computer" - better than "one sign-in click".
- **409** (Gateway not signed in): route to Step 2 "Sign in with DevThrottle" (browser). That signs
  the GATEWAY in; then retry enroll -> green. This is the only path that shows the sign-in button.
- **403** (not loopback): this Director is NOT on the Gateway's machine -> that is case B. Until B
  ships, show a clear "this device isn't on the Gateway's machine yet" message, NOT a raw 401.

The forward-compatible panel change the Manager is already building (a 401/Unauthorized connect
failure routes to Sign in instead of a dead-end) is correct and stays; A3 just makes the token-less
case attempt loopback enroll before/at that 401 so the same-machine case needs no browser step at all.

### A acceptance

A fresh same-machine cc-director from merged main, with the Gateway signed in, reaches two green checks
by picking "This computer" - no URL, no code, no dead-end. If the Gateway is signed out, one DevThrottle
sign-in gets there. Prove it live on SOREN_NORTH (this closes the real 11.3 happy path). Add an
`AuthMiddleware` unit test that `/devices/enroll-signed-in` is public, and keep the endpoint's own
`Evaluate` tests (403/409/allow) green.

## B. Any-machine flow (design direction - epic #1069 "issue 0b", a follow-up mission)

A Director on machine B gets **403** from loopback enroll (correctly). It must obtain an
account-issued key through the CLOUD sign-in, whose surfaces are ALREADY public and proven for the
phone (`/m/enroll`) and the browser Cockpit (`/signin` + `/device-callback`, key handed back in the URL
fragment): `/account/sign-in-start`, `/account/sign-in-callback`, `/signin`, `/device-callback`.

Direction:

1. The remote Director opens the DevThrottle cloud sign-in in machine B's own browser (desktop-OAuth
   style: the Director runs a transient loopback listener and passes it as the redirect target).
2. The user signs in once in their own browser.
3. The cloud issues device B its OWN per-device key, bound to the account, and hands it back to the
   Director's loopback listener (the native analog of the Cockpit `/device-callback` fragment).
4. The Director authenticates to the Gateway with that account-issued key.

**The crux (the one architectural decision):** the Gateway must TRUST a key it did not mint. The
`ChildDeviceMirrorService` already mirrors devices UP to the account roster, so the account/cloud is
already the shared source of truth. Recommendation, consistent with the epic #1069 north star (the
account is the single auth source, Tailscale model): the Gateway SYNCS the account's device roster
DOWN and accepts any active account-issued key in it (a pull that mirrors the existing push), rather
than verifying against the cloud on every first use. That keeps one authority (the account) and one
validation path (the local `DeviceRegistry`, now populated from the roster).

**Dependency to flag:** B requires cloud-side work in devthrottle.com / Supabase (issue per-device
keys bound to the account, expose the roster for the Gateway to sync). That is a separate codebase and
deploy - which is why B is a follow-up mission, not today's change. Recommend: ship A now for green on
this machine; take B as the next phase under epic #1069, sequenced with the cloud work.
