# Mission Brief: Network Diagnostics (fast at home, always visible)

Status: research + build in progress, written 2026-07-13 on machine SOREN_NORTH, in the `devthrottle`
repo, session "Network Diagnostics - Standalone" (3cc96cc5). Companion documentation is owned by the
"Network Diagnostics - Docs" session (52749c5a) in `devthrottle_internal`
(docs/networking/network-speed-and-diagnostics.md).

## WHY (front and center)

The home network should be fast by default, and a user must never be silently slow on their own LAN
without knowing it or how to fix it. The owner was slow for about two weeks and did not know the cause
existed. Never again: make the connection quality visible, make the diagnosis automatic (agents and the
apps, not the user), keep the fast path warm, and show the whole story - including how much the product
is used, and how well the network performs, at home versus away.

## The discovery that reframed everything

The home slowness was NOT a misconfiguration and NOT the "Tailscale is required" problem. It is
Tailscale's normal cold-start behavior: every connection starts relayed through the nearest DERP server
(Toronto here) and auto-upgrades to a DIRECT peer-to-peer LAN path a moment later. Measured during that
window you see the relay (155 ms, 8 Mbps); a moment later it is direct (11 ms). Proven with
`tailscale ping` (direct 192.168.1.15, 11 ms; UDP open, NAT clean, local-network access granted). So the
owner's settings are already correct; the only cause of slowness is landing in the cold-start window.

## What is already built (branch feat/mobile-net-diagnostics, verified, NOT yet deployed)

Endpoints (Gateway, per-device-key gated): `GET /diag/echo`, `GET|POST /diag/payload`, `GET /diag/ping`
(featherweight latency), `GET /diag/network` (server-side tailscale status/ping/netcheck ->
per-device direct-vs-DERP + latency + UDP/NAT, NO phone needed), `POST /diag/result` + `GET /diag/results`
(logged recent history). Client-core measurement + read helpers. Mobile `/m/diagnostics` page with the
authoritative direct-vs-relay verdict + "how to make it faster" checklist + result logging. Cockpit
"Network" view (per-device table + self-test + recent results). CLI: `cc-devthrottle diag network` /
`diag results`. 32 unit tests pass; all workspaces typecheck.

## The enabler for everything below

Stamp every relevant event - each diagnostic result AND each feature-usage event (voice, typing, etc.) -
with its ROUTE at the time: "home" (direct LAN) vs "away" (Tailscale), plus the measured quality. Then
PERSIST it (today the results ring is in-memory). This one dimension powers usage-by-location,
network-quality-by-location, trends, and drift alerts. It must EXTEND the existing DevThrottle Stats /
Your Throttle instrumentation (see devthrottle-stats mission), not create a parallel stats system.

## Feature roadmap (phased; each ships and is proven before the next)

Phase 0 - DEPLOY the foundation already built, and verify /diag/network against real Tailscale on the
live Gateway. (Prereq for seeing any live data.)

Phase 1 - Automatic detection.
  - Auto-run a quiet diagnostic on app open (phone + Cockpit); a small status pill (green=direct/fast,
    amber=warming, red=relaying/slow) in the header so quality is always visible without opening a page.
  - Server-side scheduled monitor: the Gateway runs the network check on a timer (existing schedule
    system), logs each run, and flags a problem only when relaying PERSISTS past the cold-start window
    (no false alarms during warmup).

Phase 2 - Keep it fast, not just measured.
  - Keep-warm heartbeat while an app is open: a tiny periodic ping holds the direct path open so the user
    never falls back to the relay. The targeted fix for "always fast at home."
  - Contextual fix guidance when relaying persists; optional agent assertions that Tailscale is set up
    correctly (report-only by default).

Phase 3 - Persist + Cockpit dashboard.
  - Persist diagnostic results (survive Gateway restart) so trends are real.
  - Cockpit Network dashboard: at-a-glance status light, live per-device direct/relay table,
    latency/throughput TREND over time, recent-results history, per-device drilldown.

Phase 4 - Network statistics + home-vs-away (the Your Throttle counterpart).
  - Network statistics surface (styled like Your Throttle): avg latency, throughput, and percent of time
    direct vs relayed, over time.
  - Home vs away USAGE: how much the product is used at home vs away (tag usage events with route,
    extending DevThrottle Stats).
  - Home vs away NETWORK QUALITY: side-by-side comparison of latency/throughput/reliability at home vs
    away. "At home should be direct/fast; away, relay is expected and fine" - so comparisons and alerts
    are judged against the right baseline.
  - A per-user baseline (known-good numbers) so "slow" is judged against this network, not a fixed number.

Phase 5 - Tell the user when it matters.
  - Drift alert: when the monitor sees persistent relaying/slowness AT HOME, notify (phone needs-you
    badge or the owner-email channel) with the specific fix. Closes the loop so no one is silently slow.

## Design constraints (do not violate)

- A phone PWA cannot run in the background, so "automatic" = app-open auto-run PLUS a server-side
  scheduled monitor. The server-side monitor is the always-on truth source.
- Home vs away is judged from the route the Gateway sees (LAN 192.168.x = home; Tailscale 100.x = away).
- Never fail silently on mobile; never claim "fast" without the measured proof.
- Extend the existing stats instrumentation; do not build a second analytics pipeline.
- ASCII only, plain English, no fallback programming.

## Coordination

Docs session 52749c5a keeps docs/networking/network-speed-and-diagnostics.md in sync; it holds unshipped
names as not-yet-merged until this session confirms them post-deploy with final names + a warm screenshot.
