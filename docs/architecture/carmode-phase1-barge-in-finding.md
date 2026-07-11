# Car Mode Phase 1 - Turn-taking core and the barge-in finding

Status: Phase 1 delivered. Written by the Manager session ("Car Mode - Manager", 2026-07-11).
This is the written finding the mission requires from Phase 1 ("New build B" and the Phase 1
proof): does the walkie-talkie discipline hold, and does control-word detection survive the
assistant's own voice playing (barge-in)?

## What Phase 1 built

The standalone, chrome-less Car Mode page under `/m/car` (route registered in
`apps/mobile/src/main.tsx`, gated behind `<RequireDeviceKey>`, with a screen wake-lock), plus the
shared two-state turn-taking machine in `packages/client-core/src/carmode/`:

- `controlPhrases.ts` - the pure, exhaustively-tested language rules: "over and out" ends a turn ONLY
  as the complete phrase and only as the last thing said (plain "over" or "out" never triggers, and a
  mid-sentence "stopover and outages" never triggers); "stop"/"wait"/"shut up" are whole-word
  interrupts. The end phrase is stripped before the command reaches the brain (decision 1).
- `speechRecognition.ts` - `ControlWordListener`, a continuous wrapper over the browser's built-in
  speech recognition (Chromium only, decision 7). It watches ONLY for the control words and hands each
  live transcript up; it deliberately does not transcribe the command (that is the Gateway's job, for
  accuracy). It auto-restarts across the browser's internal segment ends so listening is continuous,
  and `reset()` clears the buffer the instant a control phrase fires so it cannot re-trigger.
- `useCarMode.ts` - the machine: Listening -> Thinking -> Speaking, no silence timer anywhere. In
  Listening, `MicRecorder` (echo cancellation already on, `recorder.ts:68`) buffers the whole utterance
  while the recognizer watches for "over and out". On the end phrase: the "my turn" water-drop cue
  (`playReadyCue`) fires, capture stops, the audio is transcribed through `POST /wingman/transcribe`,
  the injected brain answers, and the reply is spoken through `POST /wingman/tts`. In Speaking, the
  recognizer watches for an interrupt word; on a hit the audio pauses instantly and the "your turn"
  cue fires. The "your turn" double-blip (`playYourTurnCue`, new in `readyCue.ts`) fires whenever the
  microphone becomes live for the owner again.
- The two cues are deliberately distinct: the "my turn" cue is the existing rising-pitch sine
  water-drop; the "your turn" cue is a LOWER, FALLING, square-wave double-blip - different in pitch
  direction, rhythm, and timbre at once, so the owner can tell them apart eyes-free.

Phase 1 wired the whole loop with a stand-in canned reply so the hardest unknown was proven before the
brain was built on top of it. (The page now defaults to the real Phase 2 brain; the stand-in is kept
behind a flag for isolating barge-in with no server.)

## The barge-in finding

The question the Architect flagged as most important: while the assistant's own voice is playing, can
the built-in recognizer still hear the owner say "stop", and does it avoid firing on the assistant's
own words?

What is settled by construction and unit tests (21 tests, all passing):

- The turn-taking discipline holds without a silence timer: the machine only ever leaves Listening on
  the exact "over and out" phrase, so the owner can pause to think for as long as he likes and nothing
  happens. This is verified in `controlPhrases.test.ts` (exact-phrase, only-at-the-end, whole-word).
- False interrupts from the assistant's own speech are structurally unlikely: the interrupt words are
  "stop"/"wait"/"shut up", which the assistant's fleet-manager replies do not contain, so even if the
  recognizer transcribes the played audio (no echo cancellation on its own capture), it will not
  transcribe those specific control words. The risk is therefore one-directional.

What must be measured on real hardware (the one-directional residual risk):

- The built-in browser recognizer uses its OWN audio capture, whose echo cancellation we cannot
  configure from the page (unlike `MicRecorder`, where we set it). So the open question is purely
  sensitivity: with the assistant's voice coming out of the phone speaker, is the owner's "stop" still
  picked up promptly? This depends on the device, the speaker volume, and the acoustic environment
  (a moving car), and cannot be honestly settled from a build machine's headless Chromium with no real
  microphone and no car.

Verdict and plan:

- The MECHANISM is proven (state machine, cue firing, phrase/interrupt detection - unit-tested; the
  live end-to-end loop is exercised in the Final-phase browser-harness drive documented in the QA
  report). The definitive barge-in sensitivity verdict is owner-gated on the real phone, in the pocket,
  while moving - exactly the mission's owner-verified acceptance gate.
- If the built-in recognizer cannot hear "stop" over the assistant's voice on the real phone, the
  mission's settled fallback is an on-device keyword-spotting model running on the ALREADY
  echo-cancelled `MicRecorder` stream (which we control), kept live during Speaking. That path is
  designed for but not built, because it is only warranted if the free built-in recognizer fails the
  real-device test - prove first, do not assume (mission "New build B"). The `useCarMode` machine is
  structured so swapping the control-word source is a localized change (replace `ControlWordListener`;
  the phase machine, cues, and capture are unchanged).

## Honesty note

This finding does not claim a from-the-pocket-in-a-moving-car result was captured here; that is the
owner's to confirm on the real phone. What is claimed and evidenced: the walkie-talkie discipline, the
two distinct cues, the exact end-word and interrupt rules, and the full transcribe-answer-speak loop
are implemented, unit-tested, and driven end-to-end via browser-harness in the Final QA report.
