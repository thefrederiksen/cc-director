# Mission Brief: Mobile bad-connection resilience (never clear good data)

Status: active mission. Written 2026-07-11 by the Architect session ("Mobile Resilience -
Architect", session c3095612, machine SOREN_NORTH). This document is the Architect's handover to
the Manager session. The Manager owns execution from here; the Architect settled the design,
stays available for design questions through the mission, and escalates product calls to the
owner.

Verification note: everything below was verified against origin/main at commit e235e76c on
2026-07-11. The SHARED checkout in D:\ReposFred\devthrottle lags origin/main (it was 25 commits
behind when this was written) - always read and branch from origin/main, never trust the shared
working tree to show the latest merged state. In particular, the shipped voice fix (PR #1334) is
visible only on origin/main, not in the stale checkout.

## The mission

The owner travels a lot - the cottage, driving - through areas with bad internet, and the mobile
app at /m is the surface he uses the most on the road. Today, when the connection goes bad, the
app clears things out: the session list (the roster, his most-used screen) disappears, and voice
mode disappears. That makes the app unusable exactly when he depends on it.

The principle he wants applied everywhere: **never clear out good data just because the
connection is bad.** Every mobile surface keeps showing the last-known content, and one small
"bad connection" indicator at the top of the screen tells him why nothing is updating - so he
knows it is the network, not the fleet. The surfaces to cover: the session list (roster), voice
mode, chat, and the terminal. Beyond those four, the app should generally be a lot more
resilient in bad-internet areas.

Two distinct legs can go bad, and both must obey the rule:

- The phone-to-Gateway leg (the cottage / driving case): the phone's own requests fail or hang.
  On this leg a fetch THROWS, so the client knows directly.
- The Gateway-to-Director leg (a machine at home briefly unreachable while he is away): the
  Gateway answers the phone with HTTP 200 but the list it returns is SHRUNKEN, because an owning
  Director did not answer the fan-out. On this leg the failure is invisible in a flat list - the
  client needs the reachability data the Gateway already computes (see finding 4).

## The core finding - most of the keep-last-known machinery already exists

Read this before starting; it is why the new work is small and targeted, not a rewrite.

1. The pattern to generalize has already shipped once, for voice. PR #1334 (issue #1333, merged
   2026-07-11) in `packages/client-core/src/voice/useVoiceMode.ts`: a session merely ABSENT from
   a SUCCESSFUL `/sessions` poll is treated as a transient gap - the screen keeps the last-known
   session and whatever is playing and shows a soft "Reconnecting to this session's computer..."
   note - and only an authoritative `voiceMode=false` (the session IS reported, with voice off)
   turns voice off. That one fix is the whole philosophy of this mission: absence is not
   authority; only an answer is. The open follow-ups #1325 (the Gateway should stamp one
   authoritative voice phase) and #1326 (voice client hardening) are adjacent but SEPARATE - this
   mission builds on #1334 as shipped and does not touch voice-phase derivation.

2. The roster already keeps last-known on a THROWN poll. `apps/mobile/src/pages/Home.tsx`:
   `load()`'s catch keeps the existing `sessions` state and shows "Offline - showing last-known
   roster". So the primary cottage case (the phone cannot reach the Gateway at all) is already
   half-handled on the roster - the content stays; what is missing is the app-wide indicator and
   the same guarantee on every other screen. The remaining roster gap is the degraded 200
   (finding 3 and 4).

3. The Gateway already absorbs BRIEF server-side misses. `FleetRosterCache`
   (`src/CcDirector.Gateway/Discovery/FleetRosterCache.cs`, issue #1215): when a Director's poll
   fails, the Gateway serves that Director's last-known-good snapshot (state "Wobbly") for up to
   3 consecutive failed poll cycles, then declares it "Offline" and drops its sessions from the
   flat `GET /sessions` response. So a one-tick blink no longer shrinks the list - but a longer
   unreachable stretch still makes sessions silently vanish from the flat response, and the flat
   response carries no way to tell "vanished because killed" from "vanished because unreachable".

4. The per-machine reachability truth is already exposed, and already has a typed shared client.
   `GET /sessions?envelope=true` returns `{ sessions, machineErrors, directors }`, where
   `directors` is the per-Director reachability (Online / Wobbly / Offline, last-seen time and
   age, error text) - see `GatewayEndpoints.cs` around line 725 and `DirectorReachabilityDto`.
   Shared client-core already has `getSessionsEnvelope()`
   (`packages/client-core/src/fleet/fleetClient.ts`) and a polling store with a hook
   (`packages/client-core/src/fleet/rosterStore.ts`, `useSharedRoster()`), consumed today by the
   Cockpit fleet views. The MOBILE app does not use any of it - its roster calls the flat
   `listSessions` (`packages/client-core/src/api/client.ts`), which throws on a non-2xx (hard
   failures handled) but cannot see WHY a session is missing from a 200. Reuse the envelope; do
   not invent a second reachability channel.

5. Chat content already keeps last-known. `packages/client-core/src/history/useSessionChat.ts`:
   a failed history poll sets `loadFailed` and KEEPS the rendered bubbles; the mobile Chat page
   (`apps/mobile/src/pages/Chat.tsx`) shows its "Could not read this session's history right now.
   Retrying..." note only when there are no bubbles at all. With bubbles on screen, a failing
   poll is currently SILENT - the reader has no idea the conversation is stale. That silence is
   exactly what the global banner exists to fix; the data handling itself is already right. The
   header name label is a best-effort one-shot that never clears a known name - fine as is.

6. The terminal is a live WebSocket mirror with auto-reconnect ALREADY BUILT - but it clears the
   buffer at the wrong moment. `packages/client-core/src/terminal/stream.ts` (`TerminalMirror`):
   reconnect at ~1200 ms is in (`RECONNECT_DELAY_MS`), and xterm keeps its own buffer (this is a
   byte stream, not polling). BUT `connect()` calls `term.reset()` BEFORE opening the new socket.
   On a bad connection, the retry loop therefore wipes the screen on every FAILED attempt, so
   the terminal sits blank the whole time the network is down - a direct violation of the
   keep-good-data principle. The root cause is the reset's position: it belongs at successful
   socket open (the stream replays full history from byte 0 on each new connection, so resetting
   right before that replay is correct; resetting before a connection that never opens is the
   bug). There is also no visible "reconnecting" state anywhere on the screen.

7. One shared unreachable-classification already exists - reuse it. `GATEWAY_UNREACHABLE_MESSAGE`
   and `gatewayErrorMessage()` in `packages/client-core/src/api/client.ts` (issue #1028) already
   separate "the request never reached a healthy backend" (fetch TypeError, status 0/502/503/504)
   from a reachable Gateway answering with an application error (400/404/409/500). The
   connection-health signal must be fed by THIS classification, not a second taxonomy.

8. There is NO global connection indicator in the mobile app today, and the mount point for one
   already exists: `GatedLayout` in `apps/mobile/src/main.tsx` wraps every gated page, stays
   mounted across navigations, and already owns app-wide concerns (the screen wake lock, the
   dictation resume). That layout is the natural single home for the banner.

9. Dictation is already resilient - verify, do not rebuild. The durable store-and-forward send
   path (issues #1006/#1182, `dictation/backgroundSend.ts` and the chunked upload in
   `api/client.ts`) survives connection loss by design and shows honest held/parked states on
   the roster and the status strip. This mission's Phase 4 exercises it under real flaky
   conditions but must not re-architect it.

## What is genuinely new - build these, everything else is reuse

### New build A - the shared connection-health signal

A small client-core module (for example `packages/client-core/src/connection/health.ts`): one
store that every Gateway contact reports into - success or failure, classified by the existing
unreachable logic (finding 7) - holding a derived state (good / bad) and the time of the last
successful contact. It is fed at the transport CHOKE POINTS, never wired page by page: the fetch
calls in `api/client.ts` (which every poll on every screen already goes through) and the terminal
WebSocket's open/close events. A `useConnectionHealth()` hook exposes it to React. The pattern to
mirror is the dictation status store (`packages/client-core/src/dictation/status.ts`): a
module-level store with a subscriber hook, already proven in this codebase.

### New build B - the one global banner

A mobile component mounted ONCE in `GatedLayout`: hidden while the connection is good; when it
goes bad, a small strip pinned at the top of every screen - plain English, for example "Bad
connection - showing last known information", adding how stale things are ("updated 40s ago")
once the gap is long enough to matter. This banner REPLACES the roster's own per-page offline
strip so the condition has exactly one voice in the whole app.

### New build C - the don't-degrade rule for the roster (removal requires authority)

Move the mobile roster from the flat `listSessions` to the envelope (`getSessionsEnvelope`,
already in client-core). The rule: a session leaves the list ONLY when its owning Director
ANSWERED and no longer reports it. When a Director reads Wobbly or Offline, its last-known
sessions STAY on the roster, visually marked unreachable (grayed dot plus a short plain note
naming the machine), for as long as the machine stays unreachable. When the machine answers
again, the live read replaces them in place. This is the #1334 rule generalized with better
data: unreachable is a state you SHOW, not data you DELETE. The client keeps its own last-known
copy per Director so that even after the Gateway's 3-cycle grace window expires (finding 3) the
phone still shows the cards - the envelope's per-Director state is what lets it do that honestly.

### New build D - the terminal that never blanks

Fix `TerminalMirror`: no reset on a failed reconnect attempt - `term.reset()` moves to the
successful socket open, immediately before the byte-0 history replay. Report the socket's state
into the health signal (so the banner also covers a dead stream when plain polls still succeed),
and show a small reconnecting note on the terminal screen while the stream is down.

## Decisions locked - do not re-litigate

1. One banner, mounted once in `GatedLayout`. No per-page connection banners; the roster's
   existing "Offline - showing last-known roster" strip folds into it. Pages keep their content;
   the banner is the single explanation.
2. The health signal lives in shared client-core and is fed at the transport choke points (the
   `api/client.ts` fetches and the terminal WebSocket), never hand-wired per page. Its
   classification reuses the issue #1028 unreachable logic; inventing a second taxonomy is a
   defect.
3. Removal requires authority. No mobile surface may replace good data with empty or shrunken
   data because of unreachability. A session is removed from the roster only when its owning
   Director answered without it; voice turns off only on an authoritative `voiceMode=false`
   (already shipped, #1334); the terminal buffer is cleared only when a new stream actually
   opens and replays.
4. No shrink heuristics. The envelope's per-Director reachability is the truth - use it. Any
   client-side guess of the form "the list shrank suspiciously, ignore it" is fallback
   programming and banned in this codebase.
5. Sessions on an unreachable machine stay visible and marked, indefinitely, until their machine
   answers again. They gray out with the machine name and "unreachable"; they do not fade out on
   a timer and they do not vanish. (Owner's principle applied directly; if he finds long-dead
   machines cluttering the roster in practice, that is a product adjustment to bring back to
   him, not a design change to make silently.)
6. Scope is the mobile app under /m. The Cockpit already consumes the envelope in its fleet
   views; giving the Cockpit the same banner is a natural follow-up but NOT this mission. The
   desktop app is untouched.
7. Voice-phase authority (#1325) and voice client hardening (#1326) remain separate issues.
   This mission must not re-derive voice state or overlap those changes.
8. Plain English, ASCII only, fail loud. A bad connection is NAMED on screen by the banner -
   never a silent stall, and nothing pretends to be live when it is stale (the banner's staleness
   age is the honesty mechanism).
9. Trunk rules. Each phase is built in its OWN isolated git worktree off origin/main (the shared
   checkout is used by other sessions and lags main - never build there), merged to origin/main
   (the only "done"), deployed with `scripts/redeploy-gateway.ps1` (which self-verifies the
   running Gateway reports the deployed commit), and verified by the owner on the real phone
   before the next phase starts. No commits until the owner explicitly asks.

## The work, in phases

Each phase ships alone: implemented, merged to origin/main, deployed to the phone via
`scripts/redeploy-gateway.ps1`, and verified by the owner on the real phone before the next
phase begins.

- Phase 1 - the connection-health signal and the one banner (the biggest visible win). New
  builds A and B: the client-core health store fed from the `api/client.ts` choke points, the
  `useConnectionHealth()` hook, the banner mounted in `GatedLayout`, and the roster's own offline
  strip retired in its favor. Proof, on the phone: turn on airplane mode while on the roster, in
  chat, in voice, and in the terminal - the content stays put on all four screens, one small
  banner at the top names the bad connection, and it clears by itself within a poll tick of the
  network coming back.

- Phase 2 - the don't-degrade rule for the list surfaces. New build C: the mobile roster moves
  to the envelope; sessions of Wobbly/Offline machines are kept and marked; only an
  authoritative Director answer removes a session. Verify (and leave alone) that the voice, chat,
  and terminal header labels never clear a known name. Proof, on the phone, against a real
  second machine: stop the test machine's Director (or pull its network) - its sessions stay on
  the roster, grayed, named unreachable; kill a session on a reachable Director - it leaves the
  roster promptly; restart the stopped Director - its cards return to live in place.

- Phase 3 - the streams: terminal reconnect without buffer loss. New build D: no reset on a
  failed attempt, reset only at successful open before the replay, socket state feeding the
  health signal, a reconnecting note on the terminal screen. Confirm chat keeps its bubbles
  under a dropped connection and that a failing history poll now surfaces through the banner
  (it is silent today). Proof, on the phone: airplane mode mid-stream - the terminal keeps
  showing the last content (never blank), the banner shows, and on reconnect the terminal
  replays and is live again with no user action; the chat keeps its bubbles throughout.

- Phase 4 - the real-world drive (hardening). The general "a lot more resilient" pass under
  genuinely flaky conditions, not clean on/off toggles: a timeout on the poll fetches so a hung
  request cannot stall a poll cycle; banner debounce so one lost packet does not flash it;
  voice playback and the dictation review under drops (the durable dictation send path already
  covers this by design - exercise it, do not rebuild it); unit tests for the health store and
  the roster keep-and-mark merge rule. Proof: the owner uses the app on a real drive or at the
  cottage and reports that the roster, chat, terminal, and voice all stayed populated and
  honest, with the banner the only sign of trouble.

## Definition of done for the mission

1. All phases merged to origin/main, each deployed via `scripts/redeploy-gateway.ps1` and
   verified by the owner on the real phone before the next began.
2. The rule holds everywhere: no mobile surface ever replaces good data with empty or degraded
   data because of a bad connection - roster, voice, chat, and terminal all keep their
   last-known content through an outage.
3. One small banner at the top of the screen is the single, global bad-connection indicator: it
   appears on trouble, names it plainly (including how stale the data is), and disappears on its
   own when the connection recovers - on every screen.
4. Sessions on an unreachable machine stay visible and marked until their machine answers again,
   while a genuinely removed session still leaves the roster promptly.
5. The terminal survives connection loss without ever blanking, shows that it is reconnecting,
   and recovers by itself.
6. The health store and the roster merge rule have unit tests, and a final verification report
   (HTML, in docs/reviews/) shows phone screenshots of each phase's proof, including the
   airplane-mode walk-through of all four surfaces.
