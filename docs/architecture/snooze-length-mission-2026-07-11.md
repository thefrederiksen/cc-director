# Mission Brief: Snooze Length (snooze that always comes back)

Status: active mission. Written 2026-07-11 by the Architect session ("Snooze Length - Architect",
machine SOREN_NORTH). This document is the Architect's handover to the Manager. The Manager owns
execution from here; the Architect settles the design and stays available for design questions but
does not gate the Manager.

## The mission

Today, snoozing a session is open-ended. You tap Snooze, the session drops out of the "needs you"
rotation and goes grey, and it stays that way until *something new happens* to it. That is the bug:
when a session dies or an agent misbehaves and goes quiet, nothing new ever happens, so the snooze
never lifts. The session silently falls off the radar forever. This actually happened - an
enrichment session went wrong, nothing surfaced it, and the owner had no way to know it needed him.

This mission turns snooze into a **time-bounded hold with a guaranteed return**. When you snooze a
session it is held for a fixed period (default one hour). When that period expires, the session is
thrown back into "needs you" on its own - even if the session or its whole Director has died in the
meantime. The worst case becomes: every snoozed session comes back. You can no longer lose a
session by snoozing it. It is a dead-man's switch, not just a hide button.

The one hard requirement that shapes the whole design: **the timer must live on the Gateway.** The
entire point is to catch a session whose Director is dead or unresponsive. A timer that lives on the
Director would die with it. The Gateway is the only place that survives to raise the alarm.

## The core finding - most of this already exists

The Architect verified all of the following against the working tree on 2026-07-11. Read it before
starting; it is why this is a focused mission, not a large one.

1. **Snooze today is the Director-owned `OnHold` boolean.** It is a property on the live `Session`
   object at `src/CcDirector.Core/Sessions/Session.cs:572`. It is set by
   `POST /sessions/{sid}/hold` on the Director (`src/CcDirector.ControlApi/ControlEndpoints.cs:909`;
   body is `HoldRequest { OnHold=true }`, empty body defaults to true). It is reported to every
   client as `SessionDto.OnHold`. There is NO timer anywhere - it is a plain flag.

2. **The Director already clears `OnHold` the moment the session comes back to life.** Issue #470:
   `Session.cs:1546` and `Session.cs:1642` clear the hold when a new turn starts or the user enters
   something ("stale Hold deferral no longer reflects intent -- clear it"). This is exactly the
   "if it comes back before the hour, the snooze just clears" behavior the owner wants, and it
   already exists on the Director side. We mirror this clear into the Gateway registry.

3. **The Gateway does NOT own hold today - it just forwards it.** The Gateway has no dedicated hold
   handler; `hold` rides the generic per-session catch-all proxy
   (`src/CcDirector.Gateway/Api/SessionWsProxyEndpoints.cs:96` lists "hold" among the forwarded
   verbs). So the snooze timer is genuinely new Gateway-owned state. We add a dedicated Gateway
   handler for `POST /sessions/{sid}/hold` that records the snooze-until AND forwards to the
   Director exactly as today (the same pattern the Local Files mission used: a specific route in
   front of the catch-all).

4. **The fold that decides "needs you" is one shared function, and snooze already sits in it.**
   `SessionOrdering.Classify` (`src/CcDirector.Gateway.Contracts/SessionOrdering.cs:216`) buckets a
   session as `OnHold`, `NeedsYou`, or `Active`. An `OnHold` session is pulled OUT of `NeedsYou`
   (`Classify` returns `OnHold` first), renders grey (`EffectiveColor`, line 96), and labels
   "Snoozed" (`StateLabel`, line 187). This is the single fold every client AND the Gateway's own
   push notifier share. To make a snooze expire, we make the session read as NOT on hold again -
   and `Classify` puts it straight back into `NeedsYou`. No new classification logic is needed.

5. **The phone already buzzes when the `NeedsYou` count rises - for free.**
   `src/CcDirector.Gateway/Push/WebPushNeedsYouNotifier.cs` polls the Gateway's own aggregated
   roster every 8 seconds (while a device is subscribed) and pushes the app-icon dot on the rising
   edge of `CountNeedsYou` (line 157 - the count of `Classify == NeedsYou`). This is the mechanism
   that makes an expired snooze reach the owner's phone an hour later: the moment expiry flips the
   session back into `NeedsYou`, this existing notifier pushes the dot. We do not build a new
   notification path.

6. **The roster survives a dead Director.** `FleetRosterCache` (`GatewayHost.cs:60`, issue #1215)
   keeps the last-known-good roster so a transient Director poll failure does not evict its
   sessions. This is what lets the Gateway still hold, and still surface, a snoozed session whose
   Director has gone silent - the dead-Director case that is the whole reason for the mission.

7. **There is one settings surface already.** `src/CcDirector.Gateway/Api/SettingsEndpoints.cs`
   backs the single Cockpit Settings page with `GET/PUT /gateway/*` actions
   (`packages/client-core/src/settings/settingsClient.ts`,
   `apps/cockpit/src/settings/SettingsView.tsx`). The default snooze length is one more setting
   here. Because every one of the owner's devices talks to their one Gateway, a Gateway-owned
   setting IS "one value across all the user's devices" - no cloud-account round-trip needed for v1.

## The design

**Snooze becomes: Director `OnHold` (as today) plus a Gateway-owned expiry timestamp.**

Gateway snooze registry: a persisted map `sessionId -> SnoozeUntilUtc` (absolute UTC time),
written to disk so a Gateway restart re-arms every pending snooze. Any entry already past its time
at startup fires immediately (surfaces as needs-you) rather than being lost or all firing at once.

Set (user taps Snooze): the new Gateway `POST /sessions/{sid}/hold` handler records
`SnoozeUntilUtc = now + defaultSnoozeMinutes`, then forwards the hold to the Director exactly as
today. Snooze and hold are set together; the Director still drops the session from the voice
rotation and still reports `OnHold=true`. Nothing about the existing hold behavior changes.

Early return (it comes back on its own): the Director clears `OnHold` on new activity (#470, already
built) and reports `OnHold=false`. The Gateway's aggregation sees a session that has a registry
entry flip to `OnHold=false` and clears the registry entry. The snooze-until "just clears," exactly
as the owner described. A genuinely new turn or a user keystroke therefore breaks through the snooze
immediately - the timer is a floor (guaranteed return by the hour), never a ceiling that gags a
session.

Manual unsnooze (user taps Unsnooze): `POST .../hold` with `OnHold=false` clears the registry entry
and forwards, as today.

Expiry (the watchdog): a Gateway background sweep - ride the existing 8-second notifier cadence or a
sibling timer - checks the registry for entries whose `SnoozeUntilUtc <= now`. On expiry the Gateway
(a) clears the registry entry, (b) stamps the aggregated `SessionDto` as no-longer-held so
`Classify` returns it to `NeedsYou`, and (c) if the Director is alive, forwards a `hold=false` so the
Director's own state agrees and its voice rotation resumes. If the Director is dead, step (b) alone
surfaces it from the cached roster - which is the case that matters most.

The returning-from-snooze marker: expiry also stamps a new display-only DTO field (e.g.
`SnoozeExpired: true`) that the clients render as a small "Snooze ended" badge, distinct from a
normal turn-complete, so the owner knows this is a *go investigate why nothing happened* item. This
marker is metadata on the roster/notification only. **Nothing is injected into the session's
conversation.** The push copy for an expired snooze should read differently from an ordinary
needs-you ("Snooze ended - still waiting on you") so a phone buzz is self-explanatory.

## Decisions already made - do not re-litigate

1. **No per-snooze duration.** There is exactly ONE snooze length - the user default. Snoozing a
   session always uses that length. Do NOT build a duration picker, a long-press menu, a
   split-button, or any per-session override. (The owner explicitly cut this; it can be a later
   improvement.)

2. **The default length is a per-user setting, default one hour, one value across all devices.** It
   lives on the Gateway settings surface (`SettingsEndpoints.cs`, `settingsClient.ts`, the Cockpit
   Settings page). Because all of the owner's devices read from their one Gateway, that is
   automatically "same snooze everywhere." Mobile reads the same value; mobile does not get its own.

3. **The timer is Gateway-owned and Director-independent, persisted to disk.** This is the whole
   point of the mission. It must fire even when the owning Director is offline or the session is
   dead. It must survive a Gateway restart.

4. **Re-snooze is not a special case - it is just snoozing again.** Like an alarm clock: when a
   session is not currently snoozed you can snooze it, and you can do that as many times as you want.
   There is no escalation, no cap, no "second snooze is different." Do not build any re-snooze logic;
   there is nothing to build - each snooze is an independent set of the same one-hour timer.

5. **Coming out of snooze re-raises "needs you"; it does not touch the conversation.** On expiry the
   session re-enters the `NeedsYou` bucket (which drives the roster, the ordering, and the existing
   phone push) and carries a "Snooze ended" marker. We do NOT write anything into the session's
   terminal or chat. The distinction lives in roster/notification metadata only.

6. **Reuse the existing fold and the existing push notifier.** Do not add a second notification
   path or a second triage classifier. Expiry works by making `SessionOrdering.Classify` see the
   session as un-held again; everything downstream (roster order, grey-vs-red, the app-icon dot)
   follows for free. Keep the Gateway as the single fold (issue #1177) - the cleanest implementation
   overrides `OnHold` on the aggregated DTO copy at the Gateway rather than teaching `SessionOrdering`
   about snooze expiry.

7. **Snooze ALWAYS goes through the Gateway - no local fallback (settled by the owner 2026-07-11).**
   Every snooze button - the cockpit session menu (`apps/cockpit/src/sessions/SessionMenu.tsx:207`),
   the cockpit roster (`apps/cockpit/src/sessions/SessionRoster.tsx`), the mobile manage bar
   (`apps/mobile/src/components/SessionManageBar.tsx:114`), AND the desktop Avalonia menus
   (`MainWindow.axaml.cs`, `FifoWindow.axaml.cs`) - must set the hold through the Gateway
   `POST /sessions/{sid}/hold` endpoint, so every snooze gets the registry timer. The desktop today
   sets `Session.OnHold=true` IN-PROCESS on the local Director and never calls the Gateway; that is a
   BUG to remove in Phase 3, not a second valid path. A Director may run without a Gateway, but it is
   then CRIPPLED for snooze: pressing Snooze with no Gateway connection does NOT snooze locally - it
   tells the user "you need to be connected to a Gateway to use snooze" and sets no hold at all. No
   local timer, no in-process hold, no silent degrade. This supersedes the earlier "stated
   limitation" framing (a Gateway-less Director does not get a weaker snooze - it gets none, loudly).

8. Plain English everywhere, ASCII only in code and output. No fallback programming: if the registry
   cannot be persisted, fail loud with a clear error - never silently run a snooze that will not
   survive a restart.

9. Verify agent-driven; do NOT route the owner through hands-on testing for this feature (settled
   2026-07-11 - the owner will not hand-test a simple feature, and is right). Prove the server logic
   with in-process end-to-end integration tests (boot a `GatewayHost` on an ephemeral port, drive the
   real endpoints, dispose and re-create to prove persistence). Never spin a standalone test Gateway
   that binds the tailscale/443 front door (the leftover-test-gateway hazard that once broke prod),
   and never touch the owner's production Gateway. Reserve any owner or real-hardware check for
   something that genuinely needs his eyes or his real phone - not this.

## A subtlety to confirm, not solve blindly

A session that is genuinely still *working* will have cleared its own `OnHold` via #470 the moment it
produced activity, so it will not still be snoozed at the one-hour mark. The only sessions that reach
expiry still snoozed are ones that went quiet - which is exactly the stuck/dead population the
watchdog exists to surface. So "still snoozed at the hour -> raise needs-you" is correct. The Manager
must confirm precisely which activity triggers the #470 clear on the Director, and hook the Gateway's
"clear the snooze-until" mirror to the same observed transition (`OnHold` reported false), so the
early-return clear and the Director's own clear never disagree.

## The work, in phases

Each phase ships alone: implemented, merged to origin/main per the trunk rule, and agent-verified
(in-process end-to-end tests, not owner hand-testing) before the next phase begins.

- **Phase 1 - Gateway snooze registry, timer, and the setting.** Add the persisted
  `sessionId -> SnoozeUntilUtc` registry and the dedicated Gateway `POST /sessions/{sid}/hold`
  handler that records it and forwards. Add the expiry sweep that clears the entry and flips the
  session back into `NeedsYou` on the aggregated roster. Add the early-return clear (registry entry
  cleared when a held session reports `OnHold=false`). Add the `GET/PUT /gateway/snooze-default`
  setting (default 60 minutes) and re-arm from disk on Gateway startup, firing any already-past
  entry immediately.
  Proof: unit tests for the sweep (future/expired/cleared-early), plus an in-process end-to-end
  integration test - boot a `GatewayHost` on an ephemeral port, drive the real
  `POST /sessions/{sid}/hold`, set the default to a few seconds, let the sweep run, assert the
  `/sessions` fold returns the session to `NeedsYou` on its own, then dispose and re-create the host
  to prove the registry re-arms from the on-disk file (the survives-restart property). No live
  Gateway, no owner hand-testing.

- **Phase 2 - The returning-from-snooze marker and the phone push.** Add the display-only
  `SnoozeExpired` DTO field, render it as a distinct "Snooze ended" badge on the cockpit roster and
  the mobile roster, and give `WebPushNeedsYouNotifier` distinct copy for a snooze-expiry rise so the
  phone buzz reads "Snooze ended - still waiting on you." A newly-expired snooze buzzes PROMPTLY with
  that distinct copy even when the needs-you dot is already showing (it is new, actionable news), but
  the push edge is keyed to a NEWLY-expired snooze the notifier has not yet announced - NOT to
  `snoozeExpired > 0`. A dead-Director expired snooze persists in `NeedsYou` indefinitely, so buzzing
  on a count would re-buzz every poll forever; instead announce each newly-expired session ONCE, then
  it folds into the existing silent dot/heartbeat. Extend `DotState` to remember the announced set;
  keep every existing anti-buzz guarantee.
  Proof: an automated test asserting the notifier emits the distinct snooze-expiry copy when an
  expired snooze raises the `NeedsYou` count, and the mobile PWA rendered via the existing Playwright
  harness (mobile-pwa-proof-via-playwright) showing the "Snooze ended" badge. A real-phone push check
  is welcome but is NOT a gate and is NOT the owner's to run.

- **Phase 3 - Desktop routed through the Gateway, the dead-Director case, and the settings UI.**
  Make the desktop Avalonia snooze (`MainWindow.axaml.cs`, `FifoWindow.axaml.cs`) call the Gateway
  `POST /sessions/{sid}/hold` path instead of setting `Session.OnHold` in-process, so a desktop
  snooze gets the same registry timer as phone and cockpit. When the Director has no Gateway
  connection the desktop Snooze must be blocked with a clear "you need to be connected to a Gateway
  to use snooze" message and set NO local hold (decision #7 - no fallback). Verify end to end that a
  snooze on a session whose Director then goes offline still returns to "needs you" from the cached
  roster at expiry (the enrichment-session scenario) and does not present as a live session. Add the
  default-snooze-length control to the Cockpit Settings page (confirm mobile reads the same value).
  Proof: a desktop snooze coming back on its own through the Gateway timer; the no-Gateway Snooze
  showing the connect message and setting no hold; a graceful-stopped Director's snooze still firing
  (the approved graceful path, never a blind force-kill of the user's Directors); screenshots of the
  settings control and every snooze surface.

## Definition of done for the mission

1. All three phases merged to origin/main, each agent-verified by in-process end-to-end tests (no
   owner hand-testing, no production Gateway).
2. Snoozing any session holds it for the user-default length (default one hour) and then returns it
   to "needs you" on its own - proven both for a live session gone quiet AND for a session whose
   Director has died.
3. A session that comes back on its own before the timer clears its snooze immediately (early return
   works); re-snoozing works any number of times with no special behavior.
4. The returned-from-snooze item is visibly distinct ("Snooze ended") on the roster and in the phone
   push, and nothing is written into the session's conversation.
5. The default length is a single per-user setting on the Gateway, editable in Settings, the same
   across every device; the registry survives a Gateway restart.
6. A final verification report (HTML, in docs/reviews/) showing the passing end-to-end tests: a
   snooze returning on its own, an early return clearing the snooze, a dead-Director snooze still
   firing, a snooze surviving a host restart (re-armed from disk), the distinct badge and push copy,
   the no-Gateway "connect to snooze" behavior, and the settings control - with cockpit/PWA
   screenshots from the Playwright harness. No step requires the owner or his production Gateway.
