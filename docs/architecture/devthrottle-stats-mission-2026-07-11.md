# Mission Brief: DevThrottle Stats (prove where the work happens, then gamify it)

Status: active mission. Written 2026-07-11 by the Architect session ("DevThrottle Stats -
Architect", session c991c09e, machine SOREN_NORTH). This document is the Architect's handover to
the Manager session. The Manager owns execution from here; the Architect settles the design and
then lets the Manager drive. It replaces the intake seed of the same name; every claim below was
verified against the working tree on 2026-07-11 and carries file and line citations.

## The mission

The owner wants hard proof of where his development actually happens, and then wants to turn that
proof into a game other people play too. After shipping mobile voice mode, working by voice from
his phone became his preferred way to build; he goes to the computer only when he genuinely needs
to click around a screen and see what the app looks like. He wants to PROVE - to himself and,
publicly, to the market - that most of his real development is done by voice, from his phone.

Two halves, in order, and the order is the whole point:

1. Instrument and measure (the real reason). Track, across the whole fleet, how the owner is
   actually driving development: how much of his input is spoken (voice dictation) versus typed on
   a keyboard, and how much comes from the phone (the mobile `/m` app), the cc-director desktop
   app, or the cockpit. The measurement is always available on the Gateway - a page he can open any
   time and see the breakdown, with no cloud round-trip needed to see his own numbers.

2. Gamify and share (the marketing wrapper). Let a person opt in to publish their stats online,
   turning it into a friendly competition - "what share of your development do you do from your
   phone?" More than one public leaderboard. Real DevThrottle credits are paid to winners. The
   boards are seeded so they look populated at launch.

The instrumentation is the point; the leaderboard is how that proof becomes a marketing story.
Everything below serves "show, with data, where the work happens" - and it must show it
**honestly**, because a phone-share number the owner will publish is worthless if the measurement
quietly flatters the phone.

## The core finding - the honest counting seam is the Director, not the Gateway

Verified against the working tree on 2026-07-11. Read this before starting; it changes the
shape of the whole mission and corrects the intake seed's leading guess.

1. Every input into a session funnels through exactly two methods on the local `Session` object,
   and they are already the enforced, tested choke point. `Session.SendInput(byte[])`
   (`src/CcDirector.Core/Sessions/Session.cs:1532`) takes raw keystrokes and writes them to the
   PTY (`_backend.Write(data)`, line 1536). `Session.SendTextAsync(string, SendSource)`
   (`src/CcDirector.Core/Sessions/Session.cs:1619`) takes a submitted turn (a whole message that
   ends with Enter). There is no third door: a regression test,
   `src/CcDirector.Core.Tests/TerminalPromptInjectionChokepointTests.cs`, already pins these two
   methods as the only way text reaches a session. This pair is where the counting lives.

2. Desktop-local input never touches the Gateway - it calls the local `Session` in-process. When
   the owner types in the desktop terminal, `TerminalControl.OnTextInput`/`OnKeyDown`
   (`src/CcDirector.Terminal.Avalonia/TerminalControl.cs:1305` and `:1288`) call `_session.SendInput(bytes)`
   directly on the live in-process `Session`. The desktop composer calls
   `_activeSession.Session.SendTextAsync(text)` (`src/CcDirector.Avalonia/MainWindow.axaml.cs:4953`
   and `:3080`); desktop dictation lands on the same `SendTextAsync` via `BackgroundDictationSend`
   (`MainWindow.axaml.cs:3001`) and the voice-mode/FIFO controllers
   (`src/CcDirector.Core/Voice/Controllers/VoiceModeController.cs`,
   `src/CcDirector.Avalonia/FifoWindow.axaml.cs:401`). None of these serialize out to the Gateway.
   **This is the finding that decides the architecture: if we counted only at the Gateway, we
   would miss 100% of desktop-local typing, and the owner's published phone/voice share would be
   silently and dishonestly inflated - the one outcome we cannot ship.** The count must happen at
   the Director choke point, which is the only place that sees desktop-local input.

3. Remote input (cockpit and phone) converges onto the same two `Session` methods. Remote
   keystrokes ride the terminal WebSocket and land at
   `TerminalStreamEndpoint.ForwardClientInputAsync` -> `SendInput(bytes)`
   (`src/CcDirector.ControlApi/TerminalStreamEndpoint.cs:217`). Remote composer/prompt submits hit
   the Director's `POST /sessions/{sid}/prompt` (`src/CcDirector.ControlApi/ControlEndpoints.cs:2266-2298`)
   -> `SessionCommandExecutor.SendPromptAsync` (`src/CcDirector.ControlApi/SessionCommandExecutor.cs:112`),
   which branches at lines 122-125: `AppendEnter == true` -> `SendTextAsync` (a submitted turn),
   `AppendEnter == false` -> `SendInput` (raw typing). So the same two methods cover desktop,
   cockpit, and phone.

4. The seed's leading guess - "`POST /wingman/transcribe` is the clean voice-versus-typed seam" -
   is wrong, and this matters. There IS one transcription service, `GatewayTranscriptionService`
   (`src/CcDirector.Gateway/Transcription/GatewayTranscriptionService.cs:33`), reached by several
   HTTP front doors (`POST /wingman/transcribe`,
   `src/CcDirector.Gateway/Api/GatewayWingmanVoiceEndpoint.cs:339`; the chunked
   `/wingman/utterance/.../complete`, `:143`; the durable
   `POST /dictation/{uploadId}/complete`, `src/CcDirector.Gateway/Api/GatewayDictationEndpoint.cs:130`).
   But the one-shot transcribe endpoints return `{ transcript }` to the CLIENT (`:372`), which then
   re-submits it as an ordinary message; by the time it reaches the session it is a plain `{ Text }`
   with no voice marker, and the voice-turn endpoint's own doc says the text tab and the voice tab
   are the identical request (`GatewayWingmanVoiceEndpoint.cs:38-40`). `PromptRequest`
   (`src/CcDirector.Gateway.Contracts/PromptRequest.cs:6-22`) has no origin field. So a transcribed
   input is NOT distinguishable from typed once it reaches the session today. Voice-versus-typed is
   a tag we must ADD, threaded from the entry points into the choke point.

5. `SendSource` exists but is not the tag we need. `SendSource`
   (`src/CcDirector.Core/Sessions/SendSource.cs:18-28`) has `UserInput`, `Delivery`, `Internal`,
   and its documented purpose is the dictation-lock exemption, not statistics; its own doc calls it
   "diagnostic only" (`Session.cs:1612-1617`). It does not encode surface, and "voice" is spread
   across `Internal` (desktop voice-mode/FIFO, Gateway voice-turn) and `UserInput` (re-submitted
   wingman transcripts, desktop dictation composer). The single real origin signal that exists is
   the `X-Dictation-Delivery` header on the Director prompt endpoint
   (`ControlEndpoints.cs:2274-2275`), set only by the Gateway's durable dictation delivery -> mapped
   to `SendSource.Delivery`. That covers exactly one voice path (phone durable dictation) and
   nothing else. We reuse it as one input, but it is not sufficient.

6. Surface (phone vs cockpit vs desktop) is not on requests today, but the device key already
   proves it. Auth is one boolean check: `AuthMiddleware.HasValidToken`
   (`src/CcDirector.Gateway/Util/AuthMiddleware.cs:181`) accepts a valid per-device key via
   `DeviceRegistry.IsValidDeviceKey` (`src/CcDirector.Gateway/Pairing/DeviceRegistry.cs:156`), which
   returns `true`/`false` and throws away WHICH device matched. But each device record already
   carries a recorded `DeviceType` (`DeviceRegistry.cs:248`; constants and mapping at `:29`, and
   `MobileDeviceEnrollmentService.DeviceTypeForPlatform`,
   `src/CcDirector.Gateway/Account/MobileDeviceEnrollmentService.cs:48-58`): a phone enrolls as
   `phone`, the cockpit as `browser`, the local machine as `workstation`/`gateway`. No client sends
   any distinguishing header - `authHeaders()` in `packages/client-core/src/api/client.ts:71-74`
   emits only `Authorization: Bearer <key>`, and the desktop C# client
   (`src/CcDirector.ControlApi/GatewayClient.cs:165`) likewise sends only the Bearer. So the surface
   is knowable from the verified key with no client change; it is simply discarded at the auth check
   today.

7. The Director already reports its roster up to the Gateway on a live channel - stats ride the
   same rails. `GatewayStreamClient` (`src/CcDirector.ControlApi/GatewayStreamClient.cs`) dials the
   Gateway's `director-stream` SignalR hub and pushes `List<SessionDto>` snapshots and per-session
   deltas (`PushSnapshot`/`PushDelta`/`RemoveSession`, lines 206-241), reconciled by a heartbeat
   floor. The Gateway aggregates these into `GET /sessions`
   (`src/CcDirector.Gateway/Api/GatewayEndpoints.cs`), which is the always-available roster today.
   Per-session stat counters flow up most naturally as new fields on the pushed snapshot/delta or a
   sibling hub method - no new transport.

8. The Gateway already has the durable-store pattern and the web surface a private dashboard needs.
   Durable state is one JSON file per store, atomic temp-write + rename, reloaded on boot, corrupt
   files quarantined - the `CronJobStore` precedent
   (`src/CcDirector.Gateway/CronJobStore.cs:315-340` save, `:248-308` load/quarantine), written
   under `CcStorage.Root()` = `%LOCALAPPDATA%\cc-director`
   (`src/CcDirector.Core/Storage/CcStorage.cs:30-38`). The Gateway is a Kestrel host that already
   serves the cockpit React app at `/c` (`src/CcDirector.Gateway/Cockpit/CockpitReactApp.cs`) and
   the mobile PWA at `/m` (`src/CcDirector.Gateway/Mobile/MobileApp.cs`), plus a self-contained
   static login page (`src/CcDirector.Gateway/Web/login.html`). Important nuance: the React apps
   are only built into `wwwroot` in RELEASE builds; a plain dev build 404s those routes. An
   "always available" page must therefore not depend on the React build.

9. The cloud side already exists and is the model for publishing. This repo never touches Supabase
   directly for app data; it calls a cloud REST API at `devthrottle.com` with the account token -
   e.g. `AccountCreditsClient` reads `GET /api/v1/account/credits`
   (`src/CcDirector.Core/Account/AccountCreditsClient.cs:35,65-113`) and the Gateway proxies it
   token-free at `/account/credits` (`src/CcDirector.Gateway/Api/AccountCreditsEndpoint.cs:39`). The
   accounts, members, and credits tables live in the sibling repo `D:\ReposFred\devthrottle_internal`
   on Supabase project `ompujpfrglgqvqprilxa`. A "publish my stats" push mirrors `AccountCreditsClient`:
   a new `POST https://devthrottle.com/api/v1/...` client here plus a new Vercel route and Supabase
   table there. The public leaderboard endpoints do not exist yet in either repo - they must be
   built cloud-side.

10. Credits can only be granted server-side, by the cloud, and only manually today. Balance is read
    here (`AccountCreditsClient`); the 402/insufficient-credits gate lives in the cloud
    (`devthrottle_internal/website/api/v1/chat.js:264`, `_lib/credits.js`). The one "add credits to
    a member" primitive is the Supabase RPC `grant_topup_credit(member, micros, ref, note)`
    (`devthrottle_internal/website/supabase/migrations/20260702150000_stripe_topup_grant.sql:26-88`),
    which is `SECURITY DEFINER` with execute granted ONLY to `service_role`. No app, client, or admin
    UI here can grant credits. A leaderboard payout is a server-side action in the internal repo
    reusing that RPC with a unique idempotency `ref`.

## The two things that are genuinely new - build these; everything else is reuse

### New build A - origin tagging at the Director choke point, and a per-session tally

This is the heart of the mission and the honest measurement. Two dimensions per input:
modality = `voice | typed`, surface = `phone | cockpit | desktop`.

- Thread a small origin descriptor into the two choke-point methods. `SendInput`
  (`Session.cs:1532`) takes only bytes today and must gain an origin argument; `SendTextAsync`
  (`Session.cs:1619`) carries `SendSource`, which is the wrong axis and must be joined by a real
  `(modality, surface)` origin. Set it at each entry point, where the origin is actually known:
  - Desktop typed terminal (`TerminalControl.cs:1288/1305`): surface = `desktop`, modality = `typed`
    - by construction, because this is the local app calling in-process.
  - Desktop composer (`MainWindow.axaml.cs:4953/3080`): surface = `desktop`, modality = `typed`.
  - Desktop dictation (`MainWindow.axaml.cs:3001` / `FifoWindow.axaml.cs:401` /
    `VoiceModeController.cs`): surface = `desktop`, modality = `voice`.
  - Remote keystrokes (`TerminalStreamEndpoint.cs:217`): modality = `typed`; surface from the
    Gateway (below).
  - Remote prompt (`ControlEndpoints.cs:2266` -> `SendPromptAsync`, `SessionCommandExecutor.cs:122-125`):
    surface from the Gateway; modality = `voice` when the durable-dictation marker is present
    (`X-Dictation-Delivery` -> `SendSource.Delivery`, `ControlEndpoints.cs:2274-2275`), else `typed`.
- Carry surface from the Gateway to the Director for remote input. Resolve the surface from the
  verified device key (change `DeviceRegistry.IsValidDeviceKey`, `DeviceRegistry.cs:156`, to return
  the matched record's `DeviceType`, and have `AuthMiddleware.HasValidToken`, `AuthMiddleware.cs:181`,
  stash it on the request) - this is trustworthy and needs no client change (decision 3). The Gateway
  then stamps that surface onto the requests it already proxies to the Director - a new header on
  `POST /sessions/{sid}/prompt` and on the terminal-stream leg, exactly the way `X-Dictation-Delivery`
  is already carried. Desktop-local input needs no header: it is `desktop` by construction.
  - Honest caveat to record: voice from the PHONE that goes through the one-shot
    `/wingman/transcribe` path (rather than durable `/dictation`) re-enters as a normal message and
    currently looks typed. Phase 1 must either route the owner's phone voice through the durable
    dictation delivery (which already carries the marker) or add the same modality marker to the
    wingman voice-turn submit (`GatewayWingmanVoiceEndpoint.cs:249,297`). Do not ship a phone-voice
    count that silently misses the wingman path - state which path is instrumented.
- Tally per session, on the Director. Keep counters keyed by `(modality, surface)`: number of
  submitted turns (a `SendTextAsync` with AppendEnter, or a dictation/voice-turn delivery) and the
  character volume of the text. Do NOT synthesize a "turn" from each `SendInput` keystroke - raw
  terminal keystrokes are counted as typed CHARACTER volume for their surface, never as turns.
  Persist locally with the `CronJobStore` pattern (`CronJobStore.cs:315-340`) so the tally survives
  a Director restart.
- Flow the tally up to the Gateway on the existing `director-stream` channel
  (`GatewayStreamClient.cs:206-241`) - new counter fields on the pushed snapshot/delta, aggregated
  into a Gateway-side `stats.json` store (same `CronJobStore` pattern, `CcStorage.Root()`).

### New build B - the always-available private Gateway dashboard

- One page the owner opens any time on the Gateway showing HIS breakdown: voice vs typed and phone
  vs desktop vs cockpit, with the headline share (see "The unit" below) front and center. It reads
  the Gateway's aggregated `stats.json`; it needs zero cloud dependency.
- Serve it as a self-contained static page (the `Web/login.html` model), NOT as a React route,
  because the React apps only exist in release builds (core finding 8) and this page must be always
  available - including on a plain dev build. Mount it in `GatewayHost.cs` alongside the existing
  `MobileApp.Map`/`CockpitReactApp.Map` registrations.
- Loud empty and not-captured states, per the no-fallback rule: a metric that cannot be captured
  (for example the wingman-path phone-voice caveat above, if that path is not yet instrumented) is
  shown as "not captured," never faked, estimated, or silently zeroed.

## The unit of "how much" - settled

Report both a count-based and a character-based view; make the count-based view the headline.

- Headline (count): share of submitted TURNS that were spoken vs typed, and share of submitted
  turns from phone vs desktop vs cockpit. A turn is one submitted message (`SendTextAsync` with
  AppendEnter, or a dictation/voice-turn delivery). This is the fair, comparable unit: one spoken
  utterance and one typed message each count as one turn, so neither modality is inflated by its
  mechanics.
- Secondary (characters): character volume by modality and surface, as an honest cross-check.
  Dictation tends to produce more characters per turn; typing in bursts produces many turns; showing
  both keeps either from being misread.
- Explicitly NOT time-in-mode. There is no clean per-mode timer at the choke point, and time favors
  slow voice; inventing a time metric would violate the no-fallback rule. If a defensible time
  signal appears later it can be added, but v1 does not fabricate one.
- The one genuinely awkward case, stated not hidden: a person working directly in a terminal TUI
  produces typed characters but few or no "turns." That is why characters are reported alongside
  turns; the dashboard labels raw-keystroke volume as typed character activity, distinct from turns.

## Decisions already made - do not re-litigate

1. Measurement first (private, always on the Gateway), then the public leaderboard/gamification on
   top. The measurement is the point; the game is the wrapper. (Owner.)
2. Three surfaces to distinguish - phone (`/m`), cc-director desktop, cockpit - and one input split
   that matters most: spoken vs typed. (Owner.)
3. Count on the Director at the `SendInput`/`SendTextAsync` choke point, never only at the Gateway,
   because desktop-local input never reaches the Gateway (core findings 1-2). Surface for remote
   input is resolved from the verified device key and forwarded to the Director; desktop is `desktop`
   by construction. (Architect, 2026-07-11.)
4. The private dashboard is always available on the Gateway with zero cloud round-trip, served as a
   self-contained static page so it works even on a non-release build. (Owner + Architect.)
5. Publishing to public leaderboards is strictly opt-in and reversible (stop sharing, remove me).
   Only counts and ratios ever leave the machine - never the text of anything said or typed. This is
   firm. (Owner + Architect.)
6. There are several leaderboard categories. Launch set proposed below (owner confirms the final
   set). (Owner.)
7. Real DevThrottle credits are paid to winners, manual-first: a server-side grant in the internal
   repo via the `grant_topup_credit` service-role RPC with an idempotent ref (core finding 10). Do
   NOT build automated payout in an early phase. (Owner + Architect.)
8. The boards are seeded so they look populated at launch, under the honesty stance below (owner
   rules on it with eyes open). (Owner.)
9. Plain English everywhere, ASCII only in code and output. No fallback programming: a metric that
   cannot be captured is stated as not-captured, never faked or estimated silently. Fire-and-forget
   is banned; every captured signal is real or explicitly absent. (Project rule.)

## Two decisions the owner must make with eyes open (carried, not decided by the Architect)

### Seeded demo users - DEFERRED, do not build yet (owner decision, 2026-07-11)

The owner has decided: build the leaderboard PAGES; do NOT generate any seed/demo data yet, and do
not decide the honesty stance now. Whether the boards are ever populated with invented users is a
later call. So the Throttle Boards ship rendering real opted-in data (which may be just the owner at
first, or empty), with clean, loud empty states - not a wall of fake users. When and if seed data is
revisited, the standing recommendation holds: keep any seed rows CLEARLY SEPARABLE with an `is_seed`
flag and never present fabricated users as verified real customers. But that is out of scope for now
- do not implement seed-data generation in this mission unless the owner asks for it.

### The public name - DECIDED

The public, competitive/leaderboard side is **"Throttle Boards"** (owner decision, 2026-07-11) -
on-brand with DevThrottle. Use this name in shipped, user-visible copy for the leaderboards. The
private per-person view is simply "your stats" or "your throttle." The headline hook that carries
the owner's thesis is a phone/voice share metric - a "Mobile share" or "Voice share" percentage.

## Leaderboard categories - proposed launch set (owner confirms)

- Highest phone share (the headline board - the owner's thesis). Ships first, in Phase 2.
- Highest voice share. Phase 3.
- Most sessions driven (raw volume). Phase 3.
- Longest hands-free streak (stretch, only if the streak signal is cleanly capturable; do not fake
  it). Phase 3 or deferred.

## The work, in phases

Each phase ships alone: implemented, merged to origin/main per the trunk rule, deployed to the real
Gateway, and verified by the owner before the next phase begins. Phase 1 alone delivers the thing
the owner actually wants - the proof.

- Phase 1 - Instrumentation and the private Gateway dashboard. Add the `(modality, surface)` origin
  tag at the Director choke point (`SendInput`/`SendTextAsync`), threaded from every entry point
  (New build A); resolve surface for remote input from the verified device key and forward it; tally
  per session on the Director and persist it (`CronJobStore` pattern); flow the tally up the existing
  `director-stream` channel; aggregate into a Gateway `stats.json`; serve one always-available
  self-contained page showing the owner's breakdown with the headline share (New build B). Instrument
  the phone-voice path explicitly and state which path (durable dictation vs wingman) is counted.
  Proof: the owner drives sessions by voice from the phone and by keyboard from the desktop, opens
  the Gateway page, and watches the voice/typed and phone/desktop/cockpit split move correctly.
  Unit tests for the tally math.

- Phase 2 - Opt-in publish and one public leaderboard. Add the reversible opt-in that pushes a
  counts-only summary to the cloud (a new `devthrottle.com/api/v1/...` client here mirroring
  `AccountCreditsClient`, plus a new Vercel route and Supabase table in `devthrottle_internal`), and
  render one public leaderboard - the phone-share board, the owner's headline thesis. Only counts and
  ratios leave the machine (decision 5). Proof: the owner opts in, appears on a live public board,
  opts back out, and disappears completely.

- Phase 3 - Multiple boards and the name. Add the remaining leaderboard categories and land the
  confirmed public name "Throttle Boards" in the copy. No seed/demo data - the boards render real
  opted-in data with clean empty states (owner deferred seed data, 2026-07-11). Proof: several boards
  render from real data; opting out still works; the name in user-visible copy is "Throttle Boards".

- Phase 4 - Credits payout and hardening. Wire the manual-first credits payout: a server-side action
  in `devthrottle_internal` that grants a winner credits via `grant_topup_credit` with an idempotent
  ref (core finding 10). Add loud/clear empty, not-captured, and error states everywhere; tests for
  the aggregation math and the origin tagging; and the final report. Proof: a winner is granted
  credits and the balance reflects it.

## Definition of done for the mission

1. All phases merged to origin/main, each verified by the owner on the real Gateway and, for the
   phone-share signal, on the real phone.
2. The owner can open one always-available Gateway page and see, from real usage, how much of his
   development is voice vs typing and how much is phone vs desktop vs cockpit - and the numbers move
   correctly when he changes how he works. The count is taken at the Director choke point so
   desktop-local typing is included; the phone/voice share is honest, not inflated by a missing
   surface.
3. A person can opt in to publish, appear on public leaderboards, and opt back out completely; only
   counts and ratios ever leave the machine, never content.
4. The leaderboards render populated (with the owner-approved honesty stance on seed data), the public
   name and copy are the owner's confirmed choice, and manual credits payout to a winner works.
5. A final verification report (HTML, in `docs/reviews/`) with screenshots of the private dashboard
   and the public boards, showing the phone-and-voice share the owner set out to prove, and stating
   plainly which input paths are instrumented and which (if any) are not-captured.
