# Car Mode - Bad-connection / offline resilience: research findings + plan

Status: research handoff from the Car Mode - Manager (session 965d43f8, machine SOREN_NORTH) to the
Car Mode - Architect (session f44f39c0). Written 2026-07-13. Issue #1427, expanded by the owner.
NOTHING built yet - this is the "research first, send the Architect findings + plan + design
questions before any code" step the phase mandates.

## The goal (owner, restated)

Driving in and out of bad signal, Car Mode must:
- (a) NEVER lose the owner's speech - capture every utterance locally, always.
- (b) durably BUFFER a turn finished in a dead zone and AUTO-RETRY it when the connection returns
  (in-and-out must never drop a request).
- (c) degrade GRACEFULLY with a clear AUDIBLE offline / holding state - never a silent stall.

Reality to design around: transcription, the brain, and text-to-speech ALL run on the Gateway, so
fully offline Car Mode cannot answer or act. The deliverable is "never lose speech + graceful
degradation + retry on reconnect", NOT running the model offline.

## How one Car Mode turn talks to the network today

Three network stages, plus two best-effort side calls. Every one is a RAW `fetch` in
`packages/client-core/src/carmode/carModeApi.ts` - NOT the shared `gatewayFetch`:

1. Transcription - `POST /wingman/transcribe` (multipart WAV -> `{ transcript }`).
   ALSO runs every 800 ms during Listening as the rolling "over and out" end-phrase watch.
2. Brain - `POST /carmode/turn` (`{ text }` -> spoken reply + actions + pendingConfirmation).
3. Text-to-speech - `POST /wingman/tts` (`{ text }` -> raw audio bytes).
4. Warmup - `POST /carmode/warmup` (best-effort, already swallows errors).
5. Telemetry - `POST /carmode/telemetry` (best-effort, already swallows errors).

Because these use raw `fetch`, they DO NOT feed the connection-health store
(`connection/health.ts` `reportGatewayReachable/Unreachable`), which is fed only through
`gatewayFetch`. So the app's single "bad connection" signal is BLIND to all Car Mode traffic today.

## Failure-mode map - what happens TODAY when the connection drops at each stage

### (A) Rolling end-phrase watch during Listening (`endPhraseTick`, every 800 ms)
- Each tick: `MicRecorder.snapshot()` -> transcode WAV -> `transcribeCarModeAudio` (raw fetch).
- On failure the `catch` just logs "end-phrase watch skipped a tick" and tries again next second.
- Consequence: in a dead zone the hands-free "over and out" NEVER fires - the transcript is never
  obtained, so the phrase can never be detected. The mic audio KEEPS accumulating (good, not lost),
  but the owner gets NO "my turn" cue and NO feedback. This is a SILENT STALL of the hands-free path
  (violates (c)). He can still tap the "Over and out" button - which lands in (B).

### (B) The "Over and out" button / end-of-turn transcription (`transcribeAndTake`)
- `snapshot()` -> transcode -> `transcribeCarModeAudio` (raw fetch).
- On failure: `catch` -> `announceError` (LOUD, spoken via the browser's LOCAL speechSynthesis) ->
  `enterListening()`.
- BUT `enterListening()` -> `restartCapture()` -> `stop()`+`start()` which THROWS AWAY the recorder
  buffer. The owner's just-spoken utterance is LOST. He is told "the Gateway is unreachable" but his
  words are gone. This directly VIOLATES requirement (a) - never lose speech.

### (C) The brain call (`takeTurn` -> `respond` -> `carModeTurn`, `POST /carmode/turn`)
- Transcription already succeeded (we hold the command text). If the brain call fails (drop between
  transcribe and brain): `catch` -> `announceError` loud -> `enterListening()`.
- The COMMAND TEXT is dropped (it lived only in local `transcript` state). No retry, no buffer.
  VIOLATES (b) - a turn finished in a dead zone is dropped, not held-and-retried.

### (D) The text-to-speech call (`speakAndPlay` -> `speakCarModeText`, `POST /wingman/tts`)
- The brain ALREADY answered, which means for an action turn the fleet action ALREADY HAPPENED
  server-side. If TTS fails: `catch` -> `announceError` loud -> `enterListening()`.
- The action is not lost (server did it) and the reply text is on screen, but the spoken reply is
  never heard through the good voice. Partial (c) gap: he hears the error, not the answer.

Summary: today Car Mode has NO durable buffer, NO retry, NO offline/holding state, and it actively
DESTROYS the captured utterance on a failed end-of-turn. All three requirements are unmet.

## The existing resilience infrastructure we should reuse (do not reinvent)

The mobile dictation store-and-forward is the proven pattern and solves almost exactly this problem
(issues #1006 / #1181 / #1182 / #1184):

- `dictation/pendingStore.ts` - a durable IndexedDB store of recorded audio Blobs. Audio is the
  single source of truth; a record is deleted ONLY when the server confirms it owns the turn, or on
  explicit abandon; undelivered audio is NEVER aged out.
- `dictation/backgroundSend.ts` - the retry driver: persist audio BEFORE any network work, then
  drive delivery; retry HARD for the first hour (exponential 2s->15s), then THROTTLE to every 5 min,
  FOREVER; resume the instant the `online` event fires or the app is foregrounded, and on every app
  load; every attempt idempotent by a client-generated upload id that is ALSO the server
  Idempotency-Key, so a retry can never double-inject.
- `dictation/status.ts` - a module store + `useSyncExternalStore` hook publishing per-item held /
  retrying / done / parked status (honest "saved, still trying" copy, never "lost").
- `connection/health.ts` - the single app-wide good/bad connection signal (fed at the transport
  choke point in `gatewayFetch`).

`MicRecorder` (`dictation/recorder.ts`) already accumulates the full utterance locally and can
`snapshot()` it without stopping - the local capture is already there; nothing is lost AT capture.
The loss happens later, when a failed transcribe restarts the recorder and discards the buffer.

## The one hard blocker for reusing store-and-forward: `/carmode/turn` is NOT idempotent

This is the crux and the main thing I need the Architect to rule on.

Dictation's store-and-forward is safe to retry blindly because the Gateway dedupes by the upload id
(Idempotency-Key) and single-flights. `POST /carmode/turn` has NO such key and is NOT idempotent:

- It APPENDS to the server-side per-device conversation history (`CarModeConversationStore.Append`).
- It EXECUTES fleet actions immediately and non-idempotently inside the tool loop
  (`start_session`, `message_session`, `approve_session` in `CarModeBrain.ExecuteToolAsync`).
- It arms / executes destructive confirmations (`delete_session`).

So blindly auto-retrying a turn we are unsure landed could DOUBLE-start a session, DOUBLE-send a
message, or DOUBLE-approve. The two sub-cases differ sharply:
- Turn NEVER reached the server (fetch threw / connection refused / 502/503/504): nothing happened
  server-side - safe to retry.
- Turn REACHED the server, it acted, and the RESPONSE was lost coming back: retrying would
  double-act.

The dictation feature solves this exact ambiguity with an Idempotency-Key + server single-flight /
cached-result dedupe. To reuse the store-and-forward safely for Car Mode, the Gateway
`/carmode/turn` needs to accept a client-generated turn id and dedupe on it: a repeated turn id
returns the cached result of the first (having acted at most once). That is the "maybe some Gateway
robustness" the phase anticipates.

## The durable unit is AUDIO, not text

Because transcription is server-side, an offline client cannot turn speech into text. So the thing we
buffer for a dead-zone turn is the RAW COMMAND AUDIO (exactly like dictation), not a transcript. On
reconnect the buffered turn is driven through transcribe -> brain -> speak. This keeps requirement
(a) and (b) unified: the local audio Blob is the source of truth for the whole turn.

## Proposed design (for the Architect to shape / approve before any code)

Reuse the dictation store-and-forward shape, adapted to Car Mode's 3-stage pipeline:

1. NEVER lose speech (req a):
   - On a failed end-of-turn transcribe, STOP discarding the buffer. Persist the captured command
     audio Blob to a durable IndexedDB store (a Car Mode analog of `pendingStore`) the instant the
     turn is taken, BEFORE the transcribe call - so even a mid-transcribe drop keeps the audio.
   - Only delete it once the server owns the turn (see idempotency below).

2. Durable buffer + auto-retry (req b):
   - A Car Mode background driver mirroring `backgroundSend.ts`: on failure, hold the audio and
     retry through transcribe -> `/carmode/turn` -> `/wingman/tts`, resuming on `online` /
     foreground / next load, with the same hard-then-throttled-forever cadence.
   - Gate the retry on a NEW Gateway turn-id Idempotency-Key so a re-driven turn acts at most once.
   - On a successful re-driven turn, SPEAK the reply (and announce what was done) so the owner hears
     the outcome of the request he made in the dead zone.

3. Graceful, audible offline / holding state (req c):
   - Route Car Mode's fetches through `gatewayFetch` (or otherwise feed `connection/health.ts`) so
     the app's bad-connection signal finally sees Car Mode traffic.
   - When a turn is held, AUDIBLY say it ("I can't reach the fleet right now - I've saved your
     request and I'll send it the moment we're back online"), show a held state on the orb, and on
     recovery audibly confirm ("Back online - here's that answer: ...").
   - Fix the rolling end-phrase watch's silent stall: after N consecutive failed ticks, audibly tell
     the owner the connection is down instead of silently missing his "over and out" forever.

## Design questions for the Architect (need your call before I build)

1. IDEMPOTENCY (the big one): do you want `/carmode/turn` to take a client turn-id Idempotency-Key
   with server-side single-flight + cached-result dedupe (mirroring dictation), so a buffered turn
   can auto-retry safely? This is Gateway work. Without it, safe auto-retry is impossible for action
   turns - we could only safely retry turns we KNOW never reached the server.

2. RETRY SCOPE: should we auto-retry ALL held turns (reads AND actions) hard-then-throttled-forever
   like dictation, or treat them differently? A read answer arriving 3 min later is odd-but-harmless;
   an ACTION ("start a session", "message X to run tests") almost certainly SHOULD still fire - that
   is the "in-and-out must never drop a request" point. Do you want any staleness cap on reads?

3. STALE-ANSWER POLICY: when a held turn finally lands minutes later, do we always speak the reply
   aloud, or only announce that the action completed (quieter) for action turns and skip speaking a
   now-stale read answer? Owner is driving and context has moved on.

4. CONVERSATION CONTEXT: a buffered turn that lands late may be out of order relative to newer turns
   the owner spoke after reconnect, corrupting the per-device server conversation history. Do we
   serialize per-device (one in-flight turn at a time, queue the rest), or is best-effort fine?

5. SCOPE / PHASING: is this one phase, or split (e.g. 4a "never lose speech + audible offline state"
   client-only, then 4b "durable buffer + idempotent retry" needing the Gateway key)? 4a delivers
   the highest-value safety (no lost speech, no silent stall) with zero Gateway change and could ship
   first.

## Test plan (mobile-only, never declared done on desktop)

- Simulate intermittent + dead connection by blocking the Gateway / offlining the network mid-turn
  (chrome://inspect devtools network offline against the phone, and/or a fetch block).
- Prove: (a) NO speech is lost across a mid-transcribe drop; (b) a turn finished offline is held and
  auto-retries + completes on reconnect with no double-action; (c) the offline/holding state is
  announced audibly, not a silent stall.
- Verify on the real phone (Z Flip, Tailscale 100.86.144.11, wireless ADB) and/or via the
  `/carmode/telemetry` store - never claim a mobile fix from a desktop test.

## Phase 4a - BUILT (2026-07-13), pending owner/Architect sign-off to merge + on-device proof

The Architect approved 4a on 2026-07-13 (all 5 questions answered; the key unlock: a fully-offline turn
fails at transcribe and never reaches the brain, so re-driving it is a first brain call = safe, no
idempotency needed). Built, client-only, zero Gateway change:

New files:
- `packages/client-core/src/carmode/pendingTurnStore.ts` - durable IndexedDB store of command AUDIO
  (mirrors dictation/pendingStore.ts). Record carries `brainSent` (the safety boundary) and a cached
  `transcript`.
- `packages/client-core/src/carmode/turnRetry.ts` - the pure retry policy: `classifyHeldTurn`
  (auto vs ask-owner, on the brainSent boundary + a 30-minute staleness cap), the hard-then-throttled
  cadence, and the audible held / recovery / connection-down lines. Unit-tested (`turnRetry.test.ts`, 9
  tests).

Changed:
- `carModeApi.ts` - the transcribe / brain / speak calls now route through `gatewayFetch`, so
  `connection/health.ts` finally sees Car Mode traffic.
- `useCarMode.ts` - persist the command audio BEFORE transcribe (never lose speech); on a transcribe
  failure enter the calm HOLDING state instead of discarding the recorder buffer; mark `brainSent=true`
  right before the brain call; delete the durable record ONLY after the brain returns a definitive
  success; a brain failure holds (money refusal stays auto-retriable with the shared credits notice; any
  other failure is held for the owner, discard-only); a hook-owned background driver re-drives the oldest
  AUTO held turn on the `online` event / foreground / Start / cadence, serialized (one in flight, never
  preempts a live or in-progress utterance), speaking recovered replies with the "Back online" prefix;
  the end-phrase watch announces the connection is down after four failed ticks (the silent-stall fix).
- `apps/mobile/src/pages/CarMode.tsx` (+ `styles.css`) - surfaces the connection-down, holding, and
  ask-owner (send/discard) states; version bumped v8 -> v9.

Verification so far (pre-merge, off-device): client-core typecheck clean; mobile typecheck clean; mobile
production build clean; full client-core suite 401 tests pass (including the 9 new policy tests). The
required end-to-end proof - offline the network mid-turn on the REAL phone and show (a) no speech lost,
(b) the held turn auto-completes on reconnect, (c) the state is announced audibly - is on-device and
therefore needs merge -> deploy first (un-merged wwwroot deploys get clobbered). Awaiting sign-off to
commit + merge + deploy, then that on-device proof.
</content>
</invoke>
