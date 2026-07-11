# Issue 1212 - Fleet list pivot, then remove the Fleet page - proof

Phase 3 of the Cockpit improvement plan. Verified against the live Gateway (23 real
sessions), driven with Playwright.

## What changed

- `apps/cockpit/src/fleet/FleetMapView.tsx` + `fleetmap.css`
  - A fourth pivot, "Fleet list": a flat responsive grid of every session, reusing the exact
    same `NodeCard` component the lane pivots use (so the number badge and the Phase 2 title
    search apply identically). No lane grouping - machines are already their own pivot. Order
    is by session number ascending (the identity the owner reads), so cards never jump.
- `apps/cockpit/src/AppShell.tsx` - the "Fleet" navigation entry is removed.
- `apps/cockpit/src/main.tsx` - the `/fleet` route now redirects to `/fleet-map` (bookmarks
  keep working); the `FleetView` import is gone.
- `apps/cockpit/src/fleet/FleetView.tsx` - deleted.

## Verified (Playwright, printed results)

- `REDIRECT /fleet -> http://localhost:5173/fleet-map` - the old route lands on the Fleet Map.
- `LIST CARD COUNT 23` - the Fleet list pivot shows every session exactly once; the count
  matches the rail's "SESSIONS 23".
- Search "mindzie" on the list pivot -> `matches= 4`.
- `CARD CLICK -> /session/08eaa65f-...` - clicking a card opens that session's page.

## Proof screenshots

- `fleetlist-pivot.png` - the four-pivot switch (By machine / By repository / By agent /
  Fleet list), the flat grid populated with the whole fleet, and the left navigation with NO
  Fleet entry (Fleet Map remains).
- `fleetlist-search.png` - the Phase 2 title search filtering the list pivot.

## Automated checks

- `tsc --noEmit` (cockpit) - clean; no dead imports (FleetView fully removed).
- `vite build` (cockpit) - clean.
