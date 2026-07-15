# Issue #1627 - the fleet map inside cc-director, with click-to-select: proof

## It needed ZERO Gateway work

The Gateway already aggregates the fleet (`GatewayEndpoints.cs`, `GET /sessions`), and the Director already
had an authenticated, address-resolved client method to fetch it - `GatewayClient.ListFleetSessionsAsync`
(`GatewayClient.cs:213`). It was simply never called from the UI. The only missing piece was a public
accessor on `ControlApiHost`, mirroring the existing `GetLatestTurnBriefAsync` (`ControlApiHost.cs:148`).

A point worth recording, because it reads like a contradiction: the roster comes over plain OUTBOUND HTTP,
not the tunnel. The tunnel is push-only - every `DirectorHub` verb returns `void`, and there is no verb that
returns a roster. "Tunnel-only" means the Gateway never DIALS the Director; it does not mean the Director
stopped calling out. `GatewayClient.Start` says so in its own comment: it survives as "the on-demand caller
for the Director's outbound Gateway operations".

## What changed

- `src/CcDirector.ControlApi/ControlApiHost.cs` - `ListFleetSessionsAsync` passthrough. Null when no
  Gateway is configured, so the view can say "not connected" rather than show an empty fleet, which would
  be a lie.
- `src/CcDirector.Avalonia/Fleet/FleetMapTree.cs` - NEW. The spawn tree (order + indent).
- `src/CcDirector.Avalonia/Fleet/FleetMapLanes.cs` - NEW. The repository / agent pivots.
- `src/CcDirector.Avalonia/Controls/FleetMapView.axaml(.cs)` - NEW. The view.
- `src/CcDirector.Avalonia/MainWindow.axaml(.cs)` - the overlay, the toolbar button (beside Cockpit), and
  the click routing.
- `src/CcDirector.Avalonia.Tests/FleetMapTreeTests.cs` - NEW. 20 tests.

## Roles and colours are READ, never recomputed

- Colour: `StatusPalette.BrushFor(SessionDto.EffectiveColor)` - the ONE desktop palette (defect 18).
- State label: `SessionDto.StateLabel`.
- Role: `SessionDto.SessionRole`, resolved by the Gateway's `FleetRoleResolver`.

`SessionManager.ResolveLocalRole` exists on this same Director and was deliberately NOT used: it mirrors the
role logic against the LOCAL roster only, so it is wrong for exactly the cross-machine case this view exists
to show. The `FleetMapTree` docs record that.

The TREE ORDER is restated in C# rather than shared with the Cockpit's TypeScript, because no code path
could carry one implementation to both. The thing that must not drift - the ROLE - is not restated; it
arrives on the wire already decided.

## The decision the issue left open: what a remote click does

- **Local session** -> selected in the rail, overlay closes. This is the entire reason the map is worth
  having in the desktop rather than the browser.
- **Remote session** -> opens in the Cockpit via the `ViewUrl` the Gateway already stamps on every session,
  reusing `OpenUrlInBrowser` - the same path the Cockpit button beside it uses. It CANNOT be selected here:
  there is no `SessionViewModel` for a session this Director does not run.

The card states which of the two it is BEFORE it is clicked ("click to open" / "on another Director - opens
Cockpit"), so the behaviour is never a surprise and there is no dead click.

## Proved in the running app, against the real fleet

A test Director was built to slot 5 and launched via the `cc-director-launch` scheduled task (per CLAUDE.md
rule 0b - a Director spawned from an agent's own process tree gives its child sessions a nested pseudo-
console and they die in ~3s). Two local sessions were spawned on it; every other session in the map belonged
to other Directors.

The app was driven through UI AUTOMATION - the same accessibility tree a screen reader uses - not
coordinate clicks. See "what went wrong" below for why that matters.

- `map-open.png` - the Fleet Map overlay inside cc-director: 19-20 real sessions, 1 machine, 6-7 repos,
  grouped by repository, spawn tree indented (Worker under Manager), role badges, Gateway colours.
- `click-selects-in-rail.png` - **the core acceptance test**. Invoking the card
  `124 Fleet Map proof 2 (#1627) - Ready - on this Director` closed the overlay and SELECTED 124 in the
  rail: highlighted, title bar reads "124 Fleet Map proof 2 (#1627)", terminal live.
- `scroll-held.png` - the map still scrolled after two polls (see the defect below).

Both click branches, from the Director's own log:

```
[FleetMapView] Card clicked: session=75322193-..., local=True
[MainWindow] FleetMap_SessionActivated: selecting local session 75322193-...
[FleetMapView] Card clicked: session=f0d6aea7-..., local=False
[MainWindow] FleetMap_SessionActivated: opening remote session f0d6aea7-... in the Cockpit
```

## A defect found by driving it, that no test would have caught

The first build polled every 3 seconds and rebuilt the lane list by `Clear()` + refill. That resets the
`ScrollViewer` to the top - so a reader scrolled halfway down the fleet was yanked back to the top every
three seconds, making the map unusable past the first screenful. Found by trying to scroll it, not by
reasoning about it. Fixed by capturing `MapScroll.Offset` before the rebuild and restoring it at `Loaded`
priority afterwards (setting it inline lands before the new content has an extent and is silently clamped
to zero). `scroll-held.png` is the map still scrolled after a 7-second wait spanning two polls.

## What went wrong, and what it changed

The cards were first written as bare `Border`s with a `PointerPressed` handler. Two consequences:

1. They were invisible to UI Automation and unreachable by keyboard - an accessibility gap the Cockpit's
   cards (role="button", tabIndex, Enter/Space) do not have.
2. Driving them therefore needed raw coordinate clicks, and one such click landed in an unrelated
   "Google Chrome for Testing" window that held the foreground and refused to yield to
   `SetForegroundWindow`. A blind coordinate click goes wherever the pixel is, not where you meant.

Both are fixed by the same change: a card IS a button, so it is now a `Button` with a flat template, an
`AutomationProperties.Name` carrying the number, name, state, and where the click goes, and keyboard focus.
The view is then driven through the accessibility tree, which needs no foreground and cannot stray into
another window.

## The tests fail on purpose

The corpse guard was neutralised and exactly one test went red with its own symptom (1 failed, 19 passed);
restoring it returned 20/20. Note the first attempt at sabotage - deleting the guard outright - did not
compile, because it left `IsAlive` unused: the compiler refused to let the test be faked.

## Checks

- `dotnet build src/CcDirector.Avalonia` - 0 errors.
- `dotnet test --filter FullyQualifiedName~FleetMap` - 20 passed.

## Deliberately not done

- **Reachability dimming** (issue #1215's Wobbly/Offline states). `ListFleetSessionsAsync` calls
  `GET /sessions` WITHOUT `envelope=true`, so it gets the flat list and not `machineErrors` / per-Director
  reachability. Adding the envelope is a one-line Director-side change; the desktop equivalent of the
  dimming is a design question, not a mechanical port, and it is not in this issue's Definition of Done.
- **The `groupId` team clustering** was never ported, because it does not exist: no call site ever sets a
  `groupId` (see #1626).
