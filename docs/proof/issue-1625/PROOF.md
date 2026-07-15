# Issue #1625 - the agent badge no longer squeezes the session title off the card: proof

## What changed

- `apps/cockpit/src/fleet/FleetMapView.tsx` - the agent badge moved off the title row
  (`.fmap-card-top`) and onto the meta row (`.fmap-card-tags`), beside the machine / repository /
  Director chips it belongs with. The meta row now renders when there is a badge OR tags, not only
  when there are tags.
- `apps/cockpit/src/fleet/fleetMapFormat.ts` - NEW. `agentBadgeText`, the one rule for what the badge
  says: the agent name, `?` when the agent is unknown, and nothing at all on the agent pivot, whose
  lane header already states the agent for every card in the lane. Pure, so it can be tested - the
  Cockpit's vitest run has no DOM environment, so a helper inside the component file is a helper that
  cannot be tested.
- `apps/cockpit/src/fleet/fleetMapFormat.test.ts` - NEW. Covers all three branches.
- `apps/cockpit/src/fleet/fleetmap.css` - the title may now wrap to two lines (`-webkit-line-clamp: 2`)
  instead of being ellipsized on one, since nothing contests its width any more. Two lines is a cap, not
  a target: a very long name still ellipsizes rather than growing the card without bound. `.fmap-card-top`
  is top-aligned rather than centred, because a centred dot beside a two-line title floats in the middle
  of the block instead of marking its start.

## Why the title was cut off

`.fmap-card-name` was `flex: 1` on a row whose other occupant, `.fmap-agent`, was `flex: 0 0 auto`. The
badge was therefore rigid and the title was the only thing on the row that could absorb the shortfall,
so it was ellipsized to a couple of words on every card regardless of the card's width.

## How this was proved

Not against the live Gateway. A throwaway fixture server served one route (`GET /sessions?envelope=true`)
with a fleet built to exercise the case - deliberately long session titles - and the Cockpit dev server
was pointed at it via `COCKPIT_PROXY_TARGET`. Nothing was registered into, or read from, the developer's
Gateway. (See issue #1628 for why that isolation matters.)

## Result

`after.png` - the Fleet Map, By machine pivot. Every title is readable:

- 111 "Session States - Input Handling and Permission..." - wraps to two lines, then clamps (the backstop)
- 101 "Fleet Map card layout and title truncation" - fully readable
- 100 "Gateway registration sweep and test isolation" - fully readable
- 121 "Orphan whose controller is not in this fleet" - fully readable

The `ClaudeCode` badge now sits on the meta row next to `repo devthrottle`.

Compare against the reported symptom, where the same cards read `Session State...`, `devthrottle - ...`,
`Banya/Yibo -...` - every title truncated after roughly two words.

## Checks

- `npm run typecheck` (tsc --noEmit) - clean.
- `npx vitest run src/fleet/` - 31 tests pass across 4 files, including the 3 new ones.
