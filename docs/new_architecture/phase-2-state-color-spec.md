> # SUPERSEDED - DO NOT IMPLEMENT FROM THIS DOCUMENT
>
> **Retired 14 July 2026.** The authoritative definition of session state is now
> [`docs/architecture/session-state-authoritative-2026-07-14.html`](../architecture/session-state-authoritative-2026-07-14.html).
> Read that instead. This file is kept only as a record of how the single fold was built.
>
> **Why this is retired:** it names `state-and-color-architecture.html` as its source of truth, and that
> document is wrong in two directions. It ranks the slate "controlled sub-agent" colour ABOVE blue
> "Working", which discards the fact that a session is working and paints a busy sub-agent gray. The
> owner's ruling of 14 July 2026 voids that: **a session that is working is BLUE, always - nothing
> outranks Working.** It also claims red breaks through slate, which the shipped code has never done for a
> live Worker. The "target precedence" list in this file's "current fold" section reproduces both errors.
>
> **Historical note:** this spec's increment 2.3 was explicitly flagged "owner please confirm" before
> changing standalone desktop colour. No confirmation was ever recorded, and the work shipped anyway.

# Phase 2 spec: Gateway owns state + color (the single fold)

**For:** STREAM WORKER 1 (`05896efe`). **Controller:** `c9f9a8e3`. Branch `feat/director-gateway-stream-1a`, worktree `D:/ReposFred/dt-stream-wt`. Build/test with `dotnet`. **Do NOT commit.**
**Source of truth:** ~~`docs/new_architecture/state-and-color-architecture.html`~~ - SUPERSEDED, see the banner above. **Read `docs/CodingStyle.md`.**

## Goal

Make the Gateway the SINGLE fold: it computes `EffectiveColor` + `TriageBucket` + `StateLabel` from RAW facts, covering every color, and no longer depends on the Director's already-cooked `StatusColor`. Clients render the Gateway field; none re-derive.

## Risk posture (important)

The arch doc's decisions D2/D3/D4 are marked CONFIRM (not fully DECIDED), and color is user-visible. So we go ADDITIVE and BEHAVIOR-PRESERVING first, and defer the destructive Director-fold removal to a flagged final increment for owner review:

- Increments 2.1 + 2.2 add the raw facts to the wire and make the Gateway's fold self-sufficient + add `StateLabel`, while KEEPING the Director's `StatusColor` exactly as today. `EffectiveColor` output MUST stay byte-identical for every existing `SessionOrderingTests` + `SessionsAggregationTests` case. Zero user-visible regression.
- Increment 2.3 (retire the Director overlay fold + dumb standalone map + delete the dormant `/assessment` endpoint + fix TS raw-color bypasses) touches the standalone desktop behaviour that D3/D4 leave as CONFIRM. Do it LAST, keep it isolated, and flag it in OVERNIGHT-STATUS.md for the owner to confirm. If it fights, STOP and report - do not force it.

## The current fold (verified) - what you are unifying

- Director fold (`src/CcDirector.Core/Wingman/SessionStatusWingman.cs`, `ResolveActivityColor` 373-421 + `ColorFor` 346-362): the single writer of `Session.StatusColor`. Layers: transcribing->orange; base Working->blue / Waiting->red; briefing(at turn-end)->yellow; explaining(turn-end)->yellow; background(turn-end)->purple; brand-new(turn-end)->green; controlled+controller-alive->slate (red breaks through).
- Gateway fold (`src/CcDirector.Gateway.Contracts/SessionOrdering.cs`, `EffectiveColor` 92-98): `OnHold->grey : Transcribing->orange : Explaining->orange : Briefing->yellow : VoicePreparing->yellow : s.StatusColor` (falls through to the Director's cooked color for purple/slate/green/blue/red). `Classify` 107-110 -> TriageBucket. Stamped in `GatewayEndpoints.cs` ~561-578; `NeedsYouClock` stamps `NeedsYouSince`.
- Target precedence (arch doc section 7, first match wins): 1 grey=OnHold|Exited; 2 orange=Transcribing|Explaining; 3 yellow=Briefing(turn-end)|VoicePreparing; 4 purple=IsBackgroundRunning(turn-end); 5 slate=IsControlled&controller-alive; 6 green=IsBrandNew(turn-end); 7 blue=ActivityState==Working; 8 red=ActivityState==Waiting. ("turn-end" = not actively Working, i.e. a Waiting/idle base.)

## Increment 2.1 - widen the wire with raw facts (pure additive, zero behavior change)

- Add to `SessionDto` (`src/CcDirector.Gateway.Contracts/SessionDto.cs`) the raw local facts the fold needs, as nullable/defaulted additive fields: `IsBrandNew` (bool), `IsControlled` (bool), `ControllerSessionId` (string?), `IsBackgroundRunning` (bool). (`IsExited` is derivable from `Status`/`ActivityState`; add an explicit `IsExited` bool only if cleaner.) Document each as a RAW FACT the Director reports, that the Gateway folds.
- Populate them in the Director's shared mapper `ControlEndpoints.Map` (`src/CcDirector.ControlApi/ControlEndpoints.cs`, ~3131-3213) directly from the `Session` (find the properties `SessionStatusWingman`/`Session` already expose for brand-new, controlled/controller, background-running). Keep stamping `StatusColor` exactly as today.
- These fields ride the existing stream snapshot/delta path for free (same mapper). No fold change yet.
- Tests: the mapper emits the raw facts for representative sessions (brand-new, controlled sub-agent, background-running). Build warnings-as-errors; run `SessionOrdering|SessionsAggregation|ControlApiHost|CockpitParity` + the stream filters - all still green (nothing changed behaviorally).

## Increment 2.2 - the Gateway single fold + StateLabel (behavior-preserving)

- Rewrite `SessionOrdering.EffectiveColor` to compute ALL 8 colors from RAW facts + the Gateway-stamped overlays (Transcribing, Briefing, VoicePreparing, Explaining) it already sets, following the target precedence above. It must NO LONGER read `s.StatusColor` as the fall-through; it derives blue/red from `ActivityState`, purple from `IsBackgroundRunning`, slate from `IsControlled` (+ controller-alive - see note), green from `IsBrandNew`.
  - Controller-alive for slate: the Director fold used "controller session is alive". On the Gateway, approximate with `IsControlled && ControllerSessionId present` (the roster can confirm the controller session exists if needed; keep it simple and note any approximation). Red must break through slate.
  - "turn-end" gate: apply purple/green/briefing only when `ActivityState != Working` (i.e. the base is Waiting/idle), matching the Director's turn-end gate.
- Add a Gateway-computed `StateLabel` (string) to `SessionDto` and stamp it in the aggregation next to `EffectiveColor`. It is the human label every client currently hand-rolls: e.g. Needs you / Working / Ready / Wingman reading / Preparing voice / Transcribing / Explaining / Sub-agent / Background / On hold / Idle. Derive it from the same fold inputs (one switch on the winning color + the overlay that won). Consolidate the label logic that lives in `FleetMapView.stateLabel`, `ExesView.humanizeState`, `DirectorDetailView` into this one Gateway function as the reference.
- Keep `TriageBucket` + `NeedsYouSince` exactly as today (they already derive from `EffectiveColor`).
- Tests - the heart of Phase 2:
  - ALL existing `SessionOrderingTests` + `SessionsAggregationTests` cases stay GREEN unchanged (proves EffectiveColor is byte-identical from raw facts).
  - MIGRATE the color cases from `SessionStatusWingmanTests` (state->color, transcribing orange, briefing/explain yellow + gating, purple background, brand-new green, controlled Supporting/slate, red-breaks-through) into `SessionOrderingTests` against the new raw-fact fold - proving the Gateway now covers EVERY color the Director used to. Build the DTO inputs with the raw facts.
  - New `StateLabel` tests: one per color/overlay.
- The Director's `StatusColor` and `SessionStatusWingman` are UNTOUCHED in 2.2 (standalone desktop still works; the Gateway simply no longer reads the cooked value).

## Increment 2.3 - retire the Director overlay fold (FLAGGED - do last, isolated)

Only after 2.1 + 2.2 are confirmed. This realizes "the Director stops folding" (arch doc section 6) and touches CONFIRM decisions:
- Reduce `SessionStatusWingman` to the dumb standalone map (arch doc section 8): Working->blue, Waiting->red, Exited->grey; NO overlays. Keep it as the standalone fallback only.
- Update `SessionStatusWingmanTests` to the reduced behavior.
- Delete the dormant `POST /sessions/{sid}/assessment` push-down endpoint (`ControlEndpoints.cs` ~1053-1065) + `Session.SetAssessedStateAnnotation` if no caller remains (verify with grep first).
- TypeScript: fix the raw-color bypasses to render Gateway fields - `ExesView.colorClass`/`humanizeState` -> use `effectiveColor` + `stateLabel`; `DirectorDetailView` inline briefing -> `effectiveColor` + `stateLabel`; `FleetMapView.stateLabel` -> render the Gateway `stateLabel`; the `ordering.ts` `isWorking` raw `statusColor=="blue"` bypass -> derive from `effectiveColor`/`activityState`.
- Flag clearly in OVERNIGHT-STATUS.md: "2.3 changes STANDALONE desktop color (loses purple/green/slate overlays with no Gateway) per arch doc section 8 (D3/D4 = CONFIRM) - owner please confirm."

## Non-negotiables
- Additive + behavior-preserving through 2.2 (EffectiveColor byte-identical; existing tests green unchanged). CodingStyle: no `!`, FileLog on new public methods, try-catch at boundaries only, warnings-as-errors, tests for every color + label.
- Do NOT commit. Report each increment to the controller (`cc-devthrottle message send c9f9a8e3 "..."`) and WAIT for confirmation before the next.
- One increment at a time. If 2.2 can't keep the pinned tests byte-identical, STOP and report the mismatch - do not paper over it.
