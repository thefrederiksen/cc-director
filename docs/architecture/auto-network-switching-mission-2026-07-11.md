# Mission Brief: Automatic Network Switching (home = direct, away = Tailscale)

Status: research + design, written 2026-07-11 on machine SOREN_NORTH. This is a research
brief and an Architect handover in progress. It captures what the code actually does today,
the one hard constraint that reshapes the whole mission, and the design fork the owner must
settle before the Manager starts building. It covers ONLY the two browser apps - the mobile
PWA (`/m`) and the Cockpit - plus their shared library `packages/client-core`. The desktop
Avalonia app is out of scope (it already does multi-address probing, issue #1233).

## The mission, in the owner's words

"When I use my phone at home, I do not want to go through the Tailscale network. If I am on
my normal home network, go straight to the computer. If I am away from the network the
gateway is on, switch to Tailscale." Automatic. No toggle to remember. Just for the phone
PWA and the Cockpit.

## DECISION (v1, settled by the owner 2026-07-12)

**Tailscale is a hard requirement for anyone who wants to use the Cockpit or the mobile app.**
It is optional in the code today; v1 makes it required. This stands until we find a different
way to solve the same problem (an alternatives review is explicitly deferred, not abandoned).

Why this is the decision, not a compromise:
- Tailscale is required for remote access anyway - only it punches through home routers. We
  cannot replace that ourselves without running tunnel infrastructure.
- Tailscale automatically gives the gateway a free, trusted TLS certificate for its
  `<machine>.<tailnet>.ts.net` name. That certificate IS the HTTPS the browsers demand. So
  requiring Tailscale erases the entire "run our own certificate service for thousands of
  users" problem - no wildcard DNS, no Let's Encrypt pipeline, no name-to-IP table to operate.
- There is genuinely no easy alternative: browsers require a publicly-trusted certificate for
  the powerful features below on any non-localhost address, and self-signed certificates do not
  work (scary full-page warning every visit, and the features stay blocked even after the user
  clicks through; iOS Safari is the strictest). This is not our quirk - every self-hosted
  product accessed from a phone (Plex, Home Assistant) solves it the same way.

Precise scope of the requirement:
- The **phone always** needs Tailscale (it is a browser on another device).
- A user running everything on ONE machine and using the Cockpit only at `http://localhost`
  does NOT technically need Tailscale (`localhost` is already a secure context). v1 may still
  require it uniformly for a single setup path; this is the one case that would otherwise work
  without it.
- Our **backend and CLI never need it**: the Director Control API (`http://127.0.0.1:787x`),
  the Gateway (`http://0.0.0.0:7878`), and the Director-to-Gateway REST + SignalR tunnel are
  all plain HTTP and have no HTTPS/Tailscale dependency. Only a browser loading the app from a
  non-localhost address needs HTTPS.

Strategic note: this takes a hard third-party dependency and adds an onboarding step (install
Tailscale + sign in). Keep a mental escape hatch for later (our own cert pipeline, or a
self-hosted Tailscale control server) - not built now.

### The browser features that require HTTPS and therefore force this decision

Verified live on the gateway 2026-07-12 by loading `/m` over plain `http://SOREN_NORTH:7878`:

| Browser capability | What it powers for us | On plain HTTP |
|---|---|---|
| Microphone (`getUserMedia`) | Dictation, Car Mode, mobile Speak | dead |
| Service worker | PWA install to home screen, offline cache | dead |
| Web Push (needs a service worker) | The "needs you" phone notification/badge | dead |
| Camera (`getUserMedia`) | Any future QR/photo capture | dead |
| `crypto.randomUUID` / `crypto.subtle` | Sign-in / device enrollment, client crypto | broken (randomUUID code-fixable; subtle not) |

Works on plain HTTP anyway: live terminal (WebSocket), typed input, `localStorage`. So voice
and notifications - the two things that make the phone useful - are exactly the casualties.

### Follow-on work (deferred, tracked here)

1. Installer/runtime gate that makes Tailscale required with a clear, actionable error when it
   is absent (today it silently runs without remote HTTPS).
2. Close the half-wired local-first gap found in research: the Director's REST client already
   prefers the local address (issue #1233), but the **SignalR tunnel** (`GatewayStreamClient`
   -> `/director-stream`) and the **cc-launcher** still dial the single configured `gateway.url`
   and do not do the candidate walk - so an on-LAN machine can still ride Tailscale for those.
3. The alternatives review the owner asked for: revisit ways to get browser-trusted HTTPS on a
   self-hosted LAN server without requiring Tailscale.

## What actually happens today (verified against the working tree 2026-07-11)

Read this before designing anything. It is why this mission is not the simple app-layer
change it first sounds like.

1. **The browser apps are hard-wired to same-origin.** Every API call in the shared client
   is root-relative - `fetch("/sessions")`, `fetch("/healthz")`, `fetch("/m/enroll")`
   (`packages/client-core/src/api/client.ts`). The live terminal WebSocket is built from
   `window.location.host` (`packages/client-core/src/terminal/stream.ts:42-44` and
   `interactive.ts:71-73`). There is NO configurable base URL, no candidate list, and no
   reachability probe. The app talks to whatever host served the page - full stop. So "which
   gateway path" is decided entirely by "which URL loaded the app," not by any runtime logic
   the app owns today.

2. **The phone reaches the Gateway ONLY over the Tailscale HTTPS front door.** The Gateway
   is plain HTTP on `0.0.0.0:7878` and terminates no TLS of its own
   (`src/CcDirector.Gateway/GatewayHost.cs:871-876`). HTTPS is provided entirely by Tailscale
   Serve mapping `443 -> localhost:7878`, provisioned automatically by
   `TailscaleServeProvisioner` (`src/CcDirector.Gateway/Tailscale/TailscaleServeProvisioner.cs`).
   So the working phone URL is `https://<machine>.<tailnet>.ts.net/m` - the tailnet front door.

3. **On the raw LAN there is HTTP only. No LAN HTTPS exists anywhere in the code.** Searches
   for `UseHttps`, `X509`, certificate, pfx, mkcert, self-signed TLS termination return
   nothing. `LanIdentity.BuildLanUrlForPort` yields `http://192.168.1.x:7878` and
   `TailscaleIdentity.BuildMachineNameUrl` yields `http://SOREN_NORTH:7878` - both plain HTTP
   (`src/CcDirector.Core/Network/`). The Gateway simply cannot serve HTTPS on the LAN today.

4. **A browser will not let a PWA use those LAN addresses - two separate hard walls.**
   - *Secure-context wall.* The PWA needs a secure context for `crypto.randomUUID`, the
     service worker, and enrollment. We already learned the hard way that plain HTTP on a
     non-localhost host is NOT a secure context, so Sign In and dictation die silently
     (memory `mobile-pwa-url-and-deploy`). `http://SOREN_NORTH:7878/m` breaks the app.
   - *Mixed-content wall.* Even if we did nothing but health-check, a page loaded over
     `https://...ts.net/m` is forbidden by the browser from making any `fetch` or WebSocket
     to an `http://` address. So the C# client's trick - "load once, then probe candidates
     and switch the base URL" - is illegal in a browser when the candidate is plain HTTP.

5. **Client identity is origin-scoped, so "home" and "away" cannot be two casual origins.**
   The per-device key and install id live in `localStorage` (`cc.deviceKey`, `cc.installId`
   in `packages/client-core/src/auth/deviceKey.ts`), which is scoped to the exact origin. If
   the home path and the away path are different origins, they are two separate enrollments,
   two device keys, and for the installed PWA, effectively two apps. Switching origins is not
   transparent - it means re-enrolling.

**The consequence.** Automatic network switching in the browser is not blocked in the app
layer - it is blocked in the transport layer. The app cannot prefer a direct-local path until
there IS a direct-local path the browser will accept, and today the only browser-acceptable
path is the Tailscale HTTPS front door. The real mission is: *give the home network a path
the browser trusts, then teach the app to choose it.* The second half is easy; the first half
is the whole decision.

## One fact the owner should weigh first

When the phone is on the same physical network as the gateway AND Tailscale is running on the
phone, Tailscale already connects **directly over the LAN** - a peer-to-peer path, not a relay
out to the internet. So "going through Tailscale" at home is not an internet round-trip today;
the packets already travel straight across the home WiFi. The genuine thing the owner gains by
this mission is **not needing Tailscale running on the phone at all when home** (and a snappier,
more robust local path when the tailnet itself is flaky). That is the requirement that decides
whether we build anything - and how much.

## The design fork (owner decides - everything downstream hangs on this)

The app-layer piece is the same in every case: a small startup connection resolver in
`client-core` that health-checks a home candidate and an away candidate and locks onto the
best reachable one, plus a visible "Home (direct) / Away (Tailscale)" indicator and a manual
override. What differs - and what the owner must pick - is HOW the home network gets an
HTTPS path the browser will accept without Tailscale.

- **Option A - Do nothing new; keep Tailscale on the phone.** Accept that Tailscale already
  routes direct on the LAN. Zero build. Fails the "do not depend on Tailscale on the phone"
  goal. Listed for honesty; not recommended if the goal stands.

- **Option B - LAN HTTPS via a locally-trusted certificate (recommended).** Teach the
  Gateway to also serve real TLS on the LAN under a stable name (a bound `https://` endpoint
  or a small local reverse step), using a certificate the phone trusts. The phone installs
  the local certificate authority ONCE. Then there are two HTTPS origins that both work:
  `https://<home-name>/m` (home, no Tailscale) and the existing tailnet front door (away).
  The app health-checks the home origin at startup and prefers it when reachable. Costs:
  certificate generation and trust on the phone (one-time), a stable home address (DHCP
  reservation - see the Orbi router notes), and handling the origin-scoped identity so the
  two origins do not force a double enrollment.

- **Option C - One stable HTTPS name, split-horizon DNS (best UX, heaviest infra).** A single
  real domain name with a publicly valid certificate on the Gateway, where the home router
  answers that name with the gateway's LAN IP at home and the tailnet path answers it away.
  ONE origin means ONE device key and truly transparent switching - the app changes nothing;
  the network underneath does. Costs: a real certificate on the Gateway with auto-renewal, and
  split-horizon DNS configured on the home router - home-network-specific, does not generalize
  to arbitrary users' networks.

Recommendation: **Option B.** It meets the stated goal (home works with Tailscale off),
keeps the away path exactly as it is today, and its costs are one-time and local. Option C is
a cleaner end state but front-loads real-cert and router-DNS infrastructure that is not worth
it for a one-machine, one-owner setup right now. Option A is only right if, on reflection, the
owner is fine keeping Tailscale on the phone at home.

## The app-layer design (identical regardless of A/B/C, built only if B or C is chosen)

A startup **connection resolver** in `client-core`, sitting in front of the same-origin
client. Given a home candidate origin and the away (tailnet) origin, it:

1. Fires a short-timeout `GET /healthz` at the home candidate. If it answers within, say, one
   second, home is reachable -> use it. If not, fall back to away. (Mirrors the C# client's
   `GatewayEndpointSelector` from #1233/#1236, but browser-side and HTTPS-only.)
2. Locks onto the chosen path for the session and re-walks on a hard connection failure, so
   walking out the door mid-session fails over to away without a manual step.
3. Surfaces the chosen path in the UI - a small, quiet "Home (direct)" vs "Away (Tailscale)"
   indicator - and offers a manual override for when the automatic choice is wrong. A manual
   choice wins until cleared. Never fail silently: if neither path answers, say so loudly
   (consistent with the mobile fail-loud rule).

Two subtleties the Manager must design against, not paper over:

- *Origin-scoped identity.* Under Option B the two origins each carry their own device key.
  The resolver must not present a signed-in home app as signed-out just because the away
  origin was where enrollment happened (or vice versa). Either enroll both origins as part of
  one flow, or make the app aware it may load from either front door and keep the identities
  reconciled. This is the sharpest design question in the app layer and must be settled before
  Phase 1 of the app work.
- *Same-origin assumption is everywhere.* The WebSocket builder, the cookie mirror
  (`ensureGatewayCookie`), the 401 redirect, and every `fetch` assume same-origin. If the
  resolver ever points the app at a different origin than the one that served the page, all of
  those must go through the resolved base, not `window.location`. Cleanest is to keep the app
  served from, and talking to, ONE origin at a time (whichever front door loaded it) rather
  than cross-origin calling - which again favors Option C's single-origin end state and makes
  Option B's two-origin reconciliation the real work.

## Decisions already implied - confirm, do not re-litigate

1. Scope is the two browser apps and `client-core` only. The desktop app is out of scope.
2. The away path stays exactly as it is today: the Tailscale HTTPS front door. This mission
   adds a home path and a chooser; it does not touch remote access.
3. Switching is automatic (health-check driven) with a manual override and a visible
   indicator. No silent failure - if nothing is reachable, the app says so.
4. ASCII only, plain English, no fallback programming. If the home certificate is not trusted
   or the home path is misconfigured, fail with a clear instruction - never silently limp on a
   broken secure context (that is the exact failure that made Sign In "do nothing" before).

## Open questions for the owner (blocking)

1. **Which option - A, B, or C?** This decides whether we build the transport half at all and
   how much. Recommendation above is B.
2. If B: is a one-time "install this certificate on your phone" step acceptable? (It is the
   price of LAN HTTPS the browser trusts.)
3. What is the stable home address to trust - a reserved DHCP IP, the machine name, or a
   chosen local hostname? (Feeds the certificate's name and the health-check candidate.)

## The work, in phases (only if B is chosen - restated for C on request)

Each phase ships alone: implemented, merged to origin/main per the trunk rule, deployed to the
real Gateway, and owner-verifiable on the real phone before the next begins.

- **Phase 0 - LAN HTTPS on the Gateway.** Give the Gateway a trusted TLS path on the LAN under
  the chosen stable name; document the one-time phone trust step. Proof: on the real phone,
  with Tailscale turned OFF, load `https://<home-name>/m`, sign in, and run dictation - all
  working over the direct LAN path.
- **Phase 1 - The browser connection resolver.** Add the startup health-check chooser to
  `client-core`, the "Home (direct) / Away (Tailscale)" indicator, and the manual override.
  Settle the origin-scoped identity question first. Proof: on the real phone, home path is
  chosen automatically at home; walking off the home network fails over to the tailnet path;
  the indicator reflects the live choice; the override works.
- **Phase 2 - Cockpit parity and hardening.** Bring the same resolver and indicator to the
  Cockpit, confirm the WebSocket/terminal path honors the resolved base, and prove the
  failure messaging is loud when neither path answers. Proof: Cockpit chooses correctly on the
  home LAN and away; a deliberately broken home path shows a clear error, never a silent hang.

## Definition of done

1. On the home network with Tailscale OFF on the phone, the PWA loads, signs in, streams a
   terminal, and dictates - over the direct LAN path, automatically, with no manual step.
2. Away from home, the app is unchanged - it uses the Tailscale front door exactly as today,
   automatically.
3. The switch is automatic, has a visible Home/Away indicator and a manual override, and fails
   loudly (never silently) when no path is reachable.
4. Both the mobile PWA and the Cockpit behave identically. The desktop app is untouched.
5. A verification report (HTML, in docs/reviews/) with real-phone screenshots: home-direct
   with Tailscale off, automatic failover to away, the indicator, and the loud no-path error.
