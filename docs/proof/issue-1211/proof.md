# Issue 1211 - Session rail and Fleet Map polish - proof

Phase 2 of the Cockpit improvement plan. Verified with the Cockpit dev server pointed at
the live Gateway on this machine (23 real sessions across two machines), driven with
Playwright.

## What changed

- `apps/cockpit/src/sessions/SessionRoster.tsx` + `apps/cockpit/src/styles.css`
  - Every rail card is now the same height: the title area reserves exactly two lines
    (`.roster-name-text`, `-webkit-line-clamp: 2` + `min-height: 2 lines`), wraps onto the
    second line, and clips with an ellipsis past two lines. The meta line is single-line
    (the machine name yields before the state truncates), so no card grows taller.
  - The selected card is a filled accent-tinted background plus a full accent ring
    (`.roster-row-selected`), unmistakable and distinct from the red attention color even
    when the same card is also in the "needs you" state.
- `apps/cockpit/src/fleet/FleetMapView.tsx` + `apps/cockpit/src/fleet/fleetmap.css`
  - Every node card shows the session number in a shared badge (`.num-badge`, defined once
    in `styles.css` and used by BOTH the rail and the Fleet Map, so the number reads as the
    same identity everywhere) - in all pivots.
  - A title search box in the Fleet Map header filters by case-insensitive substring. It is
    derived from the full-fleet lanes, so it only removes non-matching cards and drops empty
    lanes - a matching card never moves and lanes never reorder. Not persisted across reloads.

## Proof screenshots

- `rail-selected.png` - the uniform rail with number badges; card 112 is SELECTED (accent
  ring + filled tint) AND red "needs you" at the same time, the two states clearly distinct.
  One-line titles and two-line titles occupy the same card height.
- `fleetmap-by-machine.png`, `fleetmap-by-repo.png`, `fleetmap-by-agent.png` - the number
  badge on every node card in all three pivots, visually identical to the rail badge.
- `fleetmap-search-help.png` - typing "help" hides every non-matching session; matches keep
  their positions; the header count updates.
- `fleetmap-search-cleared.png` - clearing the box restores the whole fleet.

## Automated checks

- `tsc --noEmit` (cockpit) - clean.
- `vite build` (cockpit) - clean (see the PR build log).
- Sessions with no number render without a broken badge: the badge is only emitted when the
  session carries a number (`hasNum` guard in both the rail and the card).
