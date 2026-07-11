# Car Mode Phase 4 - Hardening, narration, and latency

Status: Phase 4 delivered. Written by the Manager session ("Car Mode - Manager", 2026-07-11).
This records how Car Mode behaves under failure and real use, the narration approach, and the
settled position on latency - the polish the mission asked for in Phase 4.

## Loud, spoken, specific failures (decision 8, no fallback)

Fire-and-forget is banned; every voice action shows and says its state.

- Client. Every turn that fails routes its error through the shared `gatewayErrorMessage`, so an
  unreachable Gateway, an out-of-credits 402, or a model error each collapses to ONE friendly,
  retry-implying line ("Can't reach the Gateway - retrying.") instead of a raw "Failed to fetch".
  The failure is put on the screen AND SPOKEN. It is spoken through the browser's LOCAL speech
  synthesis, deliberately not the Gateway voice: the most common failure (an offline or
  out-of-credit Gateway) is exactly when `POST /wingman/tts` is also down, so a failure that had to
  be spoken by the Gateway would be silent - the one time it must not be. This local synthesis is
  used ONLY to announce failures; the assistant's normal replies always use the one good Gateway
  voice, so the single-voice rule holds. After any failure the microphone is handed straight back to
  the owner with the "your turn" cue, so he is never left not knowing whose turn it is.
- Server. `POST /carmode/turn` maps a money refusal (402) to the ONE shared hosted-AI state (so the
  phone shows the consistent add-credit / add-key notice), and every other failure to a specific,
  logged 502 the phone speaks. The brain's fleet tools throw (never return an empty list) on a bad
  roster read, and the model loop is round-capped so a model that never settles fails loud instead of
  looping forever.

## Barge-in robustness

Echo cancellation is already configured on the `MicRecorder` capture (`recorder.ts:68`), the single
most important requirement so the assistant's own voice does not feed back and cause a false "stop".
The turn-taking decision is one pure, unit-tested function (`decideControlAction`) gated by phase:
only Listening can END (on "over and out"), only Speaking can INTERRUPT, Thinking ignores control
words. False interrupts from the assistant's own speech are structurally unlikely (its replies do not
contain "stop"/"wait"/"shut up"). The one residual risk - whether the built-in recognizer can HEAR the
owner's "stop" over the assistant's voice on a real phone in a moving car - is owner-gated and carries
the settled fallback (an on-device keyword model on the echo-cancelled stream), documented in
`carmode-phase1-barge-in-finding.md`. The recognizer auto-restarts across the browser's internal
segment ends and resets its buffer the instant a control phrase fires, so a used phrase never
re-triggers.

## Narration quality

The assistant is prompted to sound like a competent, calm development manager on a phone call: it
answers OUT LOUD in one or two short spoken sentences (no lists, no markdown, no emoji), always names a
session by its human name and repository and NEVER by its number (decision 5), gets real facts from the
tools before answering (never guesses a count or a state), asks one short clarifying question when
unsure rather than guessing, and says briefly what it did after an act. This is enforced in the system
prompt, not by post-processing the model's words.

## Latency - the fold is a documented option, intentionally not taken in v1

The v1 shape is three clear steps (decision 2): the browser captures audio, gets a transcript from
`POST /wingman/transcribe`, hands the transcript to the brain at `POST /carmode/turn`, and speaks the
reply from `POST /wingman/tts`. The mission explicitly allows folding transcription into the brain
endpoint later (audio straight to `/carmode/turn`) as a latency optimization. It is deliberately NOT
taken in v1: the three-step shape keeps each surface single-purpose and independently testable, and the
dominant latency is the model tool-loop and the speech synthesis, not the extra request hop (the fleet
tools are in-process loopback calls, sub-millisecond). Folding is a clean future change - `/carmode/turn`
would accept multipart audio and call the same transcription owner in-process before the loop - with no
change to the turn-taking machine or the tools. Recorded here so the option is a decision, not an
oversight.

## Test coverage (the mission's Phase 4 ask)

- The brain's tool loop and confirmation gate: 43 unit tests (drives tools and feeds results back,
  per-device context isolation and the round-cap loud failure, the act tools, the destructive
  arm-then-confirm gate with the deterministic negatives-win confirmation words, the fuzzy resolver,
  the parse, the stores).
- The turn-taking machine's language: 20 unit tests (the exact end-word and interrupt rules, the
  phase-gated `decideControlAction`, the two distinct cues).
- The client Gateway calls: 10 unit tests (transcribe / speak / turn each map 402 to the shared
  credits error and every other non-2xx to a specific GatewayError, and parse the success shapes).
