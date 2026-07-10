# Issue 1215 - Cockpit three-state rendering - proof

Phase 6, the Cockpit half. The Gateway grace-window + envelope work is the backend commit on
this branch (see `diagnosis.md`, `roster-envelope-shape.md`, `FleetRosterCache.cs`, and the
1606 passing Gateway tests). This is the Cockpit rendering of the three states, in place.

## What changed (Cockpit)

- `packages/client-core/src/fleet/fleetClient.ts` - `reachabilityFor(directors, directorId)`
  joins a session to its owning Director's reachability; `reachabilityLastSeen(ageSeconds)`
  gives the "last seen N ago" label. (`reachability.test.ts` - 4 tests.)
- `apps/cockpit/src/sessions/SessionsView.tsx` - the roster poll now reads
  `getSessionsEnvelope` (sessions + `directors`) and passes the reachability down.
- `apps/cockpit/src/sessions/SessionRoster.tsx` + `styles.css` - a Wobbly Director dims its rail
  rows (opacity 0.6) and shows an amber "last seen 22s ago"; Offline dims further (0.4). The row
  never moves - only its appearance changes.
- `apps/cockpit/src/fleet/FleetMapView.tsx` + `fleetmap.css` - the same, on the Fleet Map node
  cards (via a small reachability context), with a "Wobbly / Offline - last seen ..." line.

## In-place, no reflow

The reachability is joined by `directorId` onto the sessions the Gateway still serves during the
grace window (Wobbly sessions STAY in the list). So an entry changes appearance in place - it is
never removed or reordered on a transient miss. When a Director is fully Online (or the envelope
carries no reachability, e.g. an older Gateway), `reachabilityFor` returns undefined and the
session renders exactly as before - so this change is invisible until the new Gateway is deployed.

## Proof screenshots

Because the live production Gateway on this machine runs the old build (its envelope has no
`directors`), the three states were exercised by injecting a `directors` envelope (one Director
marked Wobbly at 22s, one Offline at 95s) into the live roster response via a Playwright route
intercept - the real fleet, with the reachability the deployed Gateway will supply:

- `cockpit-rail-wobbly-offline.png` - the rail: 18 rows dimmed in place, each with an amber
  "last seen 22s ago" (Wobbly) or "last seen 1m ago" (Offline); the layout does not reflow.
- `cockpit-fleetmap-wobbly-offline.png` - the Fleet Map: the affected machine's node cards dimmed
  with an "Offline - last seen 1m ago" line; the canvas layout is unchanged.

## Automated checks

- client-core + cockpit `tsc --noEmit` clean; cockpit `vite build` clean.
- client-core `vitest` passes (includes `reachability.test.ts` and the Gateway-side
  `FleetRosterCacheTests` on the .NET side).

## Owner-hardware acceptance items

Firewall or briefly disconnect a real machine's Director and watch the Cockpit dim it as Wobbly
(nothing disappears), then return to Online; keep it off past the grace window and watch it go to
Offline exactly once, then return to Online when it reconnects. This needs a real network drop on
owner hardware and the deployed Gateway.
