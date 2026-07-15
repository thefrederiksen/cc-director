# Issue #1626 - the Fleet Map shows the controller spawn tree: proof

## What this did NOT need

No new Gateway concept. The relationship was already modelled, already resolved, and already on the wire:

- `SessionDto.ControllerSessionId` + `IsControlled` - the edge, stamped at birth (issue #815).
  `tools/cc-devthrottle/src/session_ops.py:510` already defaults it to the spawning session, so every
  `cc-devthrottle session spawn` from inside a session records the edge today.
- `src/CcDirector.Gateway/Fleet/FleetRoleResolver.cs` - already walks that edge set fleet-wide and stamps
  `SessionDto.SessionRole` (Architect / Manager / Worker / Standalone).
- `packages/client-core/src/api/schema.ts:7660-7662` - all three fields already generated on the client.

The Fleet Map simply read none of them. This change is a display fix.

## What changed

- `apps/cockpit/src/fleet/fleetMapFormat.ts` - `buildControllerTree`: orders a lane's sessions as the
  spawn tree and returns each with its depth. Pure and unit tested.
- `apps/cockpit/src/fleet/FleetMapView.tsx` - `LaneCards` renders that tree; `NodeCard` takes a `depth`
  and indents by it, and shows the Gateway-resolved role. `sessionRole` is READ, never re-derived - only
  the Gateway can answer it, because a controller may live on another Director.
- `apps/cockpit/src/fleet/fleetmap.css` - depth indent plus a connector rail; role badge styling.

## The groupId "team" clustering is GONE - it never rendered

The issue asked whether the tree should replace the existing `groupId` clustering or sit beside it. It
replaces it, because the clustering was a live consumer of a producer that does not exist:

- `SessionDto.GroupId` is only ever assigned from `SessionManager.CreateSession`'s `groupId` parameter.
- That parameter defaults to null on every overload.
- NO call site passes it. The only two lines that mention it (`MainWindow.axaml.cs:1596`, `:1621`) pass
  their own default-null parameter through, and every caller of those omits it.
- `NewSessionRequest` has NO group field at all - so nothing created through the Gateway, the CLI, or a
  Director can ever carry one. The spawn path (`SessionCommandExecutor.cs:501`) passes
  `controllerSessionId` instead.

So `.fmap-team` had never rendered, for anyone. It is removed along with `splitTeams` / `roleRank` /
`Team`. The DTO field and its C# plumbing are deliberately left alone: removing those is a separate
decision with its own blast radius.

## The four rules, each proved

Read out of the live DOM (`marginLeft` is the computed indent, 14px per level):

```
111 | indent=0px  | Architect | Session States - Input Handling and Permission Prompts
101 | indent=14px | Manager   | Fleet Map card layout and title truncation
130 | indent=28px | Worker    | A session in another repository entirely
110 | indent=28px | Worker    | Move the agent badge onto the meta row
117 | indent=28px | Worker    | Controller tree indentation in the Fleet Map view
118 | indent=42px | Worker    | Sub-worker nested one level deeper again
100 | indent=14px | Manager   | Gateway registration sweep and test isolation
102 | indent=28px | Worker    | Test Directors registering into the real Gateway
122 | indent=0px  | -         | Exited controller - must not indent under a corpse
123 | indent=0px  | -         | Child of the exited controller
121 | indent=0px  | -         | Orphan whose controller is not in this fleet
120 | indent=0px  | -         | A standalone session with a very long descriptive title
```

- **Depth is not capped at two.** 118 renders at depth 3 (42px).
- **Dead parent.** 123's controller (122) has exited, so 123 renders at top level, not under the corpse.
- **Parent outside the lane.** On the By repository pivot, 130 (mindzieWeb) - whose controller 101 is in
  the devthrottle lane - renders at `indent=0px` in its own lane. On the By machine pivot above, where
  both are present, the same session correctly nests at 28px. Same session, both rules, both correct.
- **Every session renders exactly once.** All 12 present, none duplicated, none lost.

## The tests fail on purpose

Each guard was removed in turn and the matching test went red with its own symptom while the other 12
stayed green:

- corpse guard removed -> `does not indent under an exited controller` FAILED (1 failed, 12 passed)
- cycle guard removed -> `does not hang or lose cards on a cycle` FAILED (1 failed, 12 passed)

One test expectation was wrong when first written (a two-node cycle promotes BOTH members to roots, not
one to depth 1). The expectation was corrected to the real behaviour; the code was not bent to match a
wrong test.

## How this was proved

Against a throwaway fixture roster served on a local port, with the Cockpit dev server pointed at it via
`COCKPIT_PROXY_TARGET`. Nothing was registered into, or read from, the developer's live Gateway (see
issue #1628 for why that isolation matters).

## Checks

- `npm run typecheck` (tsc --noEmit) - clean.
- `npx vitest run src/fleet/` - 41 tests pass across 4 files (13 in fleetMapFormat).
