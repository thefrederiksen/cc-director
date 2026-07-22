# MTR fix-snooze — hosted voice-mode snooze

INTERNAL working note. Branch `fix/hosted-snooze-tenant-scope`.

## Symptom reported
The Snooze button on the hosted mobile **voice screen** "does not work". Owner uses snooze heavily.

## Hypothesis handed to this seat
#1909 (f575e7ee, deployed tonight in batch-4) gave the `snoozes` table a composite
`(tenant_id, SessionId)` primary key, making it tenant-scoped, and a snooze call path that runs
without a resolved ambient tenant on hosted now throws / writes the wrong partition — so the
write (hold), the fold read, and the display push disagree.

## Finding: hypothesis CORRECTED — the data path is already tenant-consistent on main

Traced the whole voice-mode snooze path on hosted. The voice screen's Snooze is the **same** verb
the roster uses:

`VoiceMode.tsx` bottom bar → `useSessionManage.toggleHold` → `holdSession(sid)` →
`POST /sessions/{sid}/hold` → `SnoozeRegistry.Snooze/SnoozeDeferred/Clear`; state is read back from
the same `GET /sessions` roster fold (`StampFleetRolesAndFold` → `HoldStateFor`).

Every snooze registry access on hosted runs **inside** a resolved tenant scope:

- **Hold WRITE** (`GatewayEndpoints` POST `/sessions/{sid}/hold`, ~1451) and **roster READ fold**
  (`StampFleetRolesAndFold`, ~2963) — the hosted device-key middleware
  (`GatewayHost.cs` ~1659-1675) enters the ambient scope from the authenticated device key for the
  whole request pipeline, and `ResolveReadTenant` resolves the same tenant. Write and read agree.
- **Display push from the hold endpoint** (`FleetDisplayStateObserver.PushSessionAsync`) — runs in
  that same request scope; its snapshot is `AmbientSnapshotFresh` (one tenant, issue #1966).
- **Periodic display sweep** — `GatewayHost.cs` ~2366 wraps `FleetDisplayState.Sweep()` in
  `_tenantPass.ForEachTenant(...)`, so each pass enters one tenant's scope.
- **DirectorHub push observers** (`SnoozeLandingObserver`, `FleetDisplayStateObserver`) —
  `DirectorHub` wraps them in `EnterBoundTenantScope()` (the tunnel Hello-bound tenant).
- **`Registry.OnDirectorRemoved -> ClearForDirector`** — guarded `if (!GatewayHostedMode.IsHosted)`,
  so it never runs unscoped on hosted.

#1909 changed **only** the primary key. `SnoozeEntity` already extended `TenantScopedEntity`
(tenant_id column) and `ApplyTenantScope<SnoozeEntity>` was already applied before #1909, so reads
and writes were already tenant-filtered. The composite key only removes a cross-tenant **write**
collision (two tenants presenting the same caller-supplied session id). For a normal single-account
user it changes nothing — it is not a regression.

## Proof added
`src/CcDirector.Gateway.Tests/HostedSnoozeLifecycleTenancyTests.cs` — drives a REAL two-tenant
hosted GatewayHost over REAL HTTP with two tunnel Directors that **collide on one session id**:

1. `Owner_snooze_is_visible_to_the_owner_and_invisible_to_a_colliding_tenant` — A snoozes
   `shared-sid` (Idle → armed): A's roster shows `onHold=true`, `holdState=Held`, a running
   `snoozeUntil`; B (same id, never snoozed) shows `onHold=false`, `holdState=None`.
2. `A_colliding_tenant_snoozes_the_same_id_without_collision_and_clears_do_not_cross` — B snoozes
   `shared-sid` and gets 200 (a `SessionId`-only PK would 500 here — the #1909 revert-proof); B's
   unsnooze does not clear A's snooze on the same id; A unsnoozes cleanly.

Result on current main: **PASS 2/2** (827 ms). This is coverage the existing #1869
`HostedSessionCommandRouteTenancyTests` did not have — that suite proves the hold *route* locates
the session, not that the snooze *lifecycle* survives the fold or the colliding-sid write.

## Where the real symptom likely lives
Not the snooze data path. Candidates, none owned by this seat's data-path brief:
- The **display-push / FleetDisplayState voice-enrichment** work seat 6fc00dc9 is doing
  concurrently (mission caution: do not touch that wiring).
- A **deferred-hold display nuance**: snoozing a *working* session defers (clock starts when work
  ends), so the endpoint returns `OnHold=false` and the button stays "Snooze" — reads as "did
  nothing", but is the owner's deferred-clock ruling, not a tenant bug.
- The deployed build predating a fix already on main.

## Action 1 done
Revert-proof test committed and shipped as its own PR: **#1987** (base main, no attribution).
Suite counts: `HostedSnoozeLifecycleTenancyTests` 2/2; Snooze+Hold 147/0;
`HostedSessionCommandRouteTenancyTests` (solo) 44/0; `CompositeTenantKeyTests` (solo) 7/0.

## Action 2 — DIAGNOSIS of the actual voice-mode symptom (read-only)

Root cause is CLIENT-SIDE, functional, single-account, voice-mode-specific — NOT the data path
and NOT the display push.

**The button wiring is IDENTICAL on every screen.** Voice bottom bar (`VoiceMode.tsx` L316-323)
and the Chat/Terminal overflow menu (`SessionAppBar.tsx` L142-155) both render
`disabled={manage.busy || manage.onHold === null}` and both call the same
`useSessionManage.toggleHold`. So the button is NOT uniquely disabled in voice mode, and it DOES
fire and hit `POST /sessions/{sid}/hold` there.

**What differs is the SESSION STATE at snooze time, and how the client renders the reply.**

1. In voice mode you snooze a session that is **WORKING/narrating** (the "working" / "preparing"
   voice card). The hold endpoint rules working -> **DEFER** (owner's ruling: the clock starts when
   the work ends). It returns `HoldResponse { OnHold=false, Pending=true }` — a real, accepted hold
   that has not landed yet.
2. `holdSession` (`packages/client-core/src/api/client.ts` ~L1254) returns `result.onHold ?? onHold`
   — it **drops `Pending` entirely**.
3. `useSessionManage.toggleHold` sets `onHold = false`, so `manage.held` stays false and the button
   label stays **"Snooze"**. The roster poll then folds the still-working session as blue "Working"
   (working wins over a deferred hold), so `manage.snoozed` is false too — **no "Snoozed" pill**.

Net: the user taps Snooze on a narrating session, the snooze IS recorded server-side (deferred),
but **nothing on the screen changes** — which reads exactly as "the Snooze button does not work in
voice mode." It self-heals only later, when the turn ends and the deferral lands (then the roster
shows Held + countdown).

**Why it looks fine on Chat/Terminal:** there you typically snooze an **idle/waiting** session,
which ARMS immediately -> `OnHold=true` -> the button flips to "Unsnooze" and the pill appears.

**The wire already carries the fix's input.** `HoldResponse.Pending` exists precisely so "the
caller's button can say 'it'll hold when it finishes what it's doing' instead of implying it is
already held" (its own doc-comment). The mobile client simply discards it. The fix is to thread
`Pending` through `holdSession` -> `useSessionManage` and give the deferred state a visible
affordance (e.g. button reads "Snoozing when it finishes" / a "Will snooze" pill), and to reflect
the accepted request optimistically. No Gateway change needed; the Gateway already returns the
right tri-state.

Answers to the four questions:
- Disabled by a voice-mode condition? **No** — same `disabled` expression as every other screen.
- Does toggleHold fire + hit /hold in voice mode? **Yes.**
- Does `manage.held` refresh after a snooze? It re-polls every 4s, but for a **deferred** hold the
  fold reports the working session as not-held, so `held`/`snoozed` correctly stay false until the
  work ends — there is no state to show as "held yet". The gap is that the **deferred acknowledgment
  is never surfaced**, not that the poll is stale.
- Desktop-only? **No** — this is the phone voice UI.

## Owner's exact symptom (confirmed)
"I hit snooze, go back to the sessions list, and the session is still there — sometimes clears
after a few seconds, sometimes never, sometimes works." This matches the diagnosis exactly: works
on an idle/settled session (arms, clears), silent/"never" on a working/narrating session (defers).

## Real bug vs by-design (the three suspects)

**(1) REAL — the client dropped `Pending`, so a deferred snooze gave no feedback.** The one true
defect. `holdSession` returned only `onHold` (false for a DeferredHold) and `useSessionManage`
reflected only that, so snoozing a working session showed no button/pill change. Fixed (below).

**(1b) REAL but minor — up-to-a-poll-interval lag on the SESSION screen.** `useSessionManage`
polled every 4s and only reflected a snooze on the next tick. Fixed: optimistic update on tap +
an immediate `refresh()` after `holdSession`.

**(2) BY DESIGN — a working session stays visible after a deferred snooze.** The Gateway defers a
working session's snooze and `SnoozeLandingObserver` only arms it when the work ends (owner ruling,
14 July 2026). So the row correctly stays until the turn ends. The bug was never that it stays —
it is that the UI never told the user the snooze was accepted. The fix makes that visible
("Snoozing when it finishes"); it does not (and must not) make a working session vanish.

**(3) NOT the bug — the Home retention cache / 5s poll, and #1986/Gap D.** Confirmed by reading the
code:
- The mobile list's held state comes from the **polled roster FOLD**: `Home.tsx` -> `getSessionsEnvelope`
  (`GET /sessions?envelope`) and `useSessionManage` -> `listSessions` (`GET /sessions`), both of which
  fold `HoldStateFor` (tenant-consistent, proven by PR #1987). **Neither reads the display-push.** So
  the FleetDisplayState `TenantId.Local` pin (Gap D / #1986, the Director *rail*) does **not** affect
  the mobile list — #1986 is not the mobile fix.
- The retention cache (`rosterRetention.mergeRosterRetention`) does **not** keep a just-snoozed row for
  a reachable Director: for an ONLINE Director the live set **replaces** the cache
  (`nextCache.byDirector.set(id, live)`), so the fresh snoozed copy (triageBucket flipped to `onHold`)
  supersedes the stale one. It only re-injects sessions for **offline/wobbly** Directors (keep-and-mark),
  which is unrelated to snooze. And `Home` is a sibling route: navigating back **remounts** it, firing an
  immediate poll — so there is no 5s wait on "go back to the list." No Home change was needed or made.

## The fix (client-side only; no Gateway change)

The wire already carries the tri-state; the client now honours it end to end.

- `client-core/api/client.ts` — `holdSession` returns `{ onHold, pending }` (a new `HoldResult`),
  reading `pending` from the response (defaults false for un-hold / old Gateway).
- `client-core/sessions/snoozeAction.ts` (new, unit-tested) — the pure display rules: `HoldUiState`
  (held/deferred), `optimisticHoldToggle` (instant affordance on tap: working -> deferred, settled ->
  held), `holdStateFromResponse`, `holdButtonLabel`, `holdPillLabel` ("Snoozing when it finishes" for
  deferred).
- `client-core/sessions/ordering.ts` — `isDeferredHold(s)` reads the Gateway `holdState` tri-state
  (`holdState` added to the augmented `GatewayStampedSession` type; the generated schema is stale).
- `apps/mobile/components/useSessionManage.ts` — adds `deferred`; hoists `refresh` and calls it
  immediately after a toggle (kills the 4s lag); updates the UI optimistically on tap; reconciles from
  the server tri-state and then the fold. Shared by Voice, Chat and Terminal, so all three surfaces get
  it.
- `apps/mobile/pages/VoiceMode.tsx` + `components/SessionAppBar.tsx` — the button reads
  `held || deferred` for its label; the pill uses `holdPillLabel`, so a deferred snooze reads "Snoozing
  when it finishes" the instant it is tapped and self-heals to "Snoozed - <countdown>" when the turn
  lands.

## Residual fixed (Codex CHANGES-NEEDED on #1987): failed /hold left a false "Snoozed"

Codex flagged one real defect in `useSessionManage.toggleHold`: the optimistic-on-tap flip was
**never rolled back when `POST /hold` FAILED**. The catch only set `error`, and the follow-up
`refresh()` ran unconditionally — so on a rejected hold the UI could keep the optimistic
`Held`/`Deferred` and falsely read "Snoozed" / "Snoozing when it finishes" for a snooze that never
happened. (A roster blip could re-assert the optimistic state, and a failed `refresh` swallows its
error and keeps the last-known — i.e. optimistic — state.)

Fix (client-side only):
- `client-core/sessions/snoozeAction.ts` — new pure `reconcileHoldToggle(preTap, outcome)` +
  `HoldToggleOutcome`: on success the server's authoritative tri-state wins; on FAILURE it rolls all
  the way back to the PRE-TAP state. Kept pure so the rollback is unit-tested, not buried in the async
  hook.
- `apps/mobile/components/useSessionManage.ts` — captures the pre-tap state, settles the UI from the
  TRUE outcome via `reconcileHoldToggle`, and only calls `refresh()` on SUCCESS. On failure it restores
  pre-tap and surfaces the error; the follow-up refresh can no longer re-assert or preserve the failed
  optimistic snooze. The SUCCESS tri-state feedback is unchanged.

## Tests
- `client-core/sessions/snoozeAction.test.ts` (6) — snooze a WORKING session shows the deferred
  affordance immediately; snooze an IDLE session shows Held immediately; un-snooze clears both; pill
  precedence; **a FAILED /hold rolls back to the pre-tap label (button returns to "Snooze", no pill),
  and a failed un-snooze keeps the armed state**; a SUCCESSFUL /hold still settles on the server
  tri-state (success path unchanged).
- `client-core/api/hold.test.ts` (3) — `holdSession` returns `{ onHold, pending }`; pending=true on a
  deferred answer; defaults on an old/empty body.
- `client-core/sessions/ordering.test.ts` — `isDeferredHold` reads the tri-state onHold cannot see.
- Registry revert-proof test (`HostedSnoozeLifecycleTenancyTests`) kept.

Counts: client-core vitest **578/578**; mobile `tsc --noEmit` + `vite build` clean; Gateway.Tests
build 0/0; `HostedSnoozeLifecycleTenancyTests` **2/2**.

## Status
Fix implemented + tested, plus Codex's residual (failed-/hold rollback) closed. Shipping on PR #1987
(base main, no attribution) alongside the revert-proof registry test. Reporting to the architect and
reaping.
