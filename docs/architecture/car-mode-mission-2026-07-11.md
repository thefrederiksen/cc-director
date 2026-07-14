# Mission Brief: Car Mode (hands-free voice control of the whole fleet)

Status: active mission. Written 2026-07-11 by the Architect session ("Car Mode - Architect",
session a240f4bf, machine SOREN_NORTH). This document is the Architect's handover to the Manager
session. The Manager owns execution from here; the Architect settles the design and then lets the
Manager drive.

## The mission

The owner wants to grab his phone, put it in his pocket, and run the entire fleet of agents by
voice while walking or driving - no screen, no typing. He opens one page, called Car Mode, taps
into voice mode once, and from then on it just listens. He speaks a request about the fleet ("how
many sessions need me right now", "show me the latest one", "start a new session in the
devthrottle repo", "message that session and tell it to run the tests") and it answers out loud
and does the work. The bar is a competent human development manager on the other end of a phone
call: he is travelling, his developers are in the office, and this page is the one channel he has
to direct them.

The interaction is deliberately a walkie-talkie, not a chat bot. The owner rejected silence-based
turn detection outright: he pauses to think mid-sentence and will not tolerate the assistant
guessing he is done and cutting him off, nor the pause-then-talk lag. Instead:

- He finishes a turn by saying the phrase **"over and out"**. Nothing the assistant does happens
  until it hears that phrase. He can stop and think for as long as he likes and it stays silent.
- He can cut the assistant off at any time by saying **"stop"** (also "wait", "shut up"). It goes
  silent instantly and returns to listening. The assistant can never talk over him - it only
  begins speaking after "over and out".
- Because the screen is in his pocket, sound is the interface. Every turn boundary has an audible
  cue (see "The audible handshake" below) so he always knows, without looking, whose turn it is
  and that his "over and out" was heard.

Full command-and-control is in scope. Starting a session by voice should feel exactly like tapping
the button himself - the assistant just does it. The assistant only comes back to ask when it is
genuinely unsure. Destructive or irreversible actions (deleting or killing a session) always ask
for a spoken confirmation. There is no per-action nagging beyond that; the assistant decides when
a confirmation is warranted.

## The core finding - most of the plumbing already exists

Verified against the working tree on 2026-07-11. Read this before starting; it is why the new
work is two focused pieces, not a from-scratch voice stack.

1. Microphone capture with echo cancellation already exists and is shared by both apps.
   `MicRecorder` in `packages/client-core/src/dictation/recorder.ts` opens the microphone via
   `getUserMedia` with `{ echoCancellation: true, noiseSuppression: true, channelCount: 1 }`
   already set (line 68), records Opus-in-WebM in 100 ms slices, and latches a "first real audio
   frame" event (lines 87-96). Echo cancellation is the single most important requirement for
   barge-in (so the assistant's own voice does not feed back into the microphone and trigger a
   false "stop") - and it is already configured. Reuse this recorder; do not write a new one.

2. There is one transcription path, through the Gateway, and it already has HTTP front doors.
   `GatewayTranscriptionService.TranscribeAsync(...)`
   (`src/CcDirector.Gateway/Transcription/GatewayTranscriptionService.cs:98`) is the single owner
   of speech-to-text: one provider, one validated dictionary. The simplest front door is
   `POST /wingman/transcribe` (`src/CcDirector.Gateway/Api/GatewayWingmanVoiceEndpoint.cs:339`):
   send a `multipart/form-data` audio file, get back `{ transcript }`. Client-side transcode to
   16 kHz mono WAV already exists in `packages/client-core/src/dictation/wav.ts`. Reuse both.

3. There is one good voice, through the Gateway, and it returns raw audio, never base64.
   `POST /wingman/tts` (`GatewayWingmanVoiceEndpoint.cs:201`) takes `{ text, voice?, model? }`
   and returns raw audio bytes (`audio/mpeg`); the voice defaults to the owner's configured
   text-to-speech voice. The client-core sample player at `packages/client-core/src/api/ai.ts`
   `ttsSample()` (line 102) shows the fetch-to-Blob-to-`<audio>` pattern. Reuse this to speak
   every reply. (Note: the "/api/tts" name in early conversation was a conflation; the real
   Gateway route is `POST /wingman/tts`, which proxies the hosted `.../api/v1/audio/speech`.)

4. The water-drop "ready" cue is already synthesized in the browser and is the seed for the
   audible handshake. `playReadyCue()` in `packages/client-core/src/dictation/readyCue.ts:15`
   synthesizes the ~380 Hz to 1150 Hz water-drop with the Web Audio API (no bundled asset). Car
   Mode reuses it for one of the two turn cues and adds a second, clearly distinguishable tone in
   the same file for the other (see decision 4).

5. The whole fleet roster - names, summaries, and "needs you" - is already one call.
   `GET /sessions` on the Gateway (`src/CcDirector.Gateway/Api/GatewayEndpoints.cs:438`) returns
   `SessionDto[]` (`src/CcDirector.Gateway.Contracts/SessionDto.cs`) already carrying everything
   the assistant needs to talk like a human: `Name` (friendly name), `Number`, `MissionName`,
   `StateLabel` ("Needs you" / "Working" / "Ready"), `LastStatusReason` (short reason),
   `RailLine` (the <=8-word one-line summary of what it is doing), `TriageBucket`
   ("needsYou" / "active" / "onHold"), and `NeedsYouSince` (how long it has been waiting).
   "How many need me" and "show me the latest one" are answerable directly from this.

6. The "now talking about session X" narration - full name plus a short summary, never a number -
   already exists as a pattern to copy. The (now retired) phone app composed it in
   `phone/CcDirectorClient/Voice/VoiceConversation.cs`: `BuildSpokenIntro(session)` (line 375)
   returns "{name}, in the {repo} repo." and it is prefixed to the model's short spoken summary
   (`PrepareExplainAsync`, line 331). Reuse the prose shape, not the code (that app is dead; Car
   Mode is React over client-core).

7. The apps already know how to host a new full-screen page over shared logic. Car Mode ships as a
   standalone, separately-deployable page on the phone (decision 9), served under `/m` behind the
   HTTPS front door. The mobile routing model is in `apps/mobile/src/main.tsx`
   (`createBrowserRouter`, `basename: "/m"`, gated routes under `<RequireDeviceKey>`, lines 77-89);
   `apps/mobile/src/pages/VoiceMode.tsx` is the closest model page. Register Car Mode as its own
   chrome-less full-screen route (for example `/m/car`) under `<RequireDeviceKey>`, not nested in
   any tabbed session view, with a screen wake-lock so the phone does not sleep. Shared logic lives
   in a new `packages/client-core/src/carmode/` folder with a `useCarMode()` hook, mirroring the
   existing `voice/useVoiceMode.ts` turn-taking hook; the page itself only wires the route plus
   JSX. The cockpit uses the same client-core hook if we ever want it there (its routing is in
   `apps/cockpit/src/main.tsx` with the rail in `AppShell.tsx`), but the phone is the target and
   the cockpit is not required for this mission.

## The two things that are genuinely new - build these, everything else is reuse

The research found the two gaps clearly. Neither is small; both are the heart of Car Mode.

### New build A - a tool-calling brain on the Gateway (the fleet manager)

Today's hosted-model helper, `HostedInferenceBrain`
(`src/CcDirector.Gateway/Wingman/HostedInferenceBrain.cs:26`), is single-shot: one user message
in, one assistant text out, over `POST {base}/chat/completions`. It has NO tool use, no
tool-result loop, no multi-step reasoning. Car Mode needs the opposite: a model that can call
fleet tools, read their results, and keep going until it has answered or acted.

- Build a new Gateway component (for example `src/CcDirector.Gateway/CarMode/CarModeBrain.cs`)
  that runs a proper tool-calling loop against the hosted model using the standard
  chat-completions `tools` / `tool_calls` shape: send the user's transcript plus the tool
  catalog, execute any tool the model calls (in-process, by calling the Gateway's own session
  endpoints), feed the results back, and repeat until the model returns a final spoken message.
- The harness is a hand-rolled loop in C#, not an agent framework and not the OpenAI Agents
  Software Development Kit. Reason (settled 2026-07-11): the brain lives inside the .NET Gateway,
  where the model key, the fleet endpoints, and all the plumbing already are, and where a
  latency-sensitive voice loop wants its tools called in-process with no network hop. The OpenAI
  Agents Software Development Kit can drive an open-source, OpenAI-compatible model (via a custom
  base address or its built-in adapter), so the model is not the issue - but it is Python-first
  with no C# version, so adopting it would force a separate non-.NET service that calls back into
  the Gateway over HTTP for every tool. That is the wrong shape for one small in-Gateway agent.
  For v1's small fixed toolset the loop is about a hundred lines and extends the single-shot
  `HostedInferenceBrain` we already have; zero new dependency, full control over latency and the
  enterprise logging the project requires. If the toolset or the reasoning later grows, adopt the
  Microsoft Agent Framework (the .NET-native successor to Semantic Kernel, which does support
  OpenAI-compatible endpoints) in-process - still C#, still in the Gateway. Do not reach for the
  OpenAI Agents Software Development Kit unless we deliberately want a polyglot brain as its own
  service.
- The model is the fast hosted role, which is the "decent but fast" tier the owner asked for
  (currently `Qwen/Qwen2.5-72B-Instruct`, resolved by
  `TranscriptionEndpointResolver.ResolveWingmanFast()`,
  `src/CcDirector.Core/Configuration/TranscriptionEndpoint.cs:134`; base
  `https://devthrottle.com/api/v1`, key `DEVTHROTTLE_API_KEY`). Tool-calling quality on this model
  is an early risk to validate; if it cannot reliably choose tools, escalate to the thinking role
  (`ResolveWingman()`, `GLM-5.2`) and accept the extra latency - decide with evidence, not a guess.
- Conversation context is kept server-side, keyed by the caller's device, so multi-turn works
  ("how many need me" then "show me the latest one" - the second turn knows what "the latest one"
  means).

The fleet tools (v1), each backed by an existing Gateway or Director endpoint - no new fleet
mechanics, just a tool wrapper the model can call:

- Read tools: count/list sessions and their state, focus a session (resolve a fuzzy human
  reference to one session and set it as the current subject), read what a session is doing - all
  from `GET /sessions` and the per-session history endpoints.
- Act tools: start a session in a named repo, send a message to a session, approve a waiting
  session - each calling the same endpoint the buttons already call.
- Destructive tools (delete/kill a session): marked as requiring confirmation. When the model
  invokes one, the loop does not execute it; it returns a spoken confirmation question and waits
  for the next turn to carry a "yes"/"confirm" before acting.

Note: the `cc-devthrottle actions --json` registry (`tools/cc-devthrottle/src/cli.py`, `_ACTIONS`)
is a good human-readable inventory of what an agent can do, but it is command-line only - there is
no HTTP endpoint that lists or invokes "actions". Do not try to route the brain through the
command line. The brain's tools call the Gateway's HTTP endpoints in-process.

### New build B - the client turn-taking machine with reliable end-word and interrupt detection

This is the highest-risk unknown and must be proven first (see Phase 1). The design:

- A two-state machine in `useCarMode()`: Listening (the owner is talking) and Speaking (the
  assistant is talking). Plus a brief Thinking state while the brain works.
- In Listening: `MicRecorder` buffers the full utterance for accurate Gateway transcription, while
  a lightweight, continuous recognizer watches only for the control phrase "over and out". When it
  fires, stop the recording, play the "my turn" cue, and send the buffered audio to be transcribed
  and answered. There is no silence timer anywhere.
- In Speaking: play the reply through `<audio>` from `POST /wingman/tts`, while the same
  lightweight recognizer watches only for "stop"/"wait"/"shut up". On a hit, pause the audio
  instantly, play the "your turn" cue, and return to Listening.
- The lightweight recognizer for the control words: start with the browser's built-in speech
  recognition (available in the Chromium web view Car Mode runs in) because it is zero-dependency.
  The open question - and the reason Phase 1 exists - is whether it stays reliable while the
  assistant's own voice is playing (barge-in) and whether it can run alongside `MicRecorder`
  cleanly. If it cannot, fall back to a small on-device keyword-spotting model running on the
  echo-cancelled microphone stream. Prove this on a real phone with the assistant's voice playing
  before building anything else on top of it. Do not assume it works; measure it.

## The audible handshake (first-class, not an afterthought)

Because Car Mode is used eyes-free, the turn boundaries must be audible. Two short, clearly
distinct tones, both synthesized in `readyCue.ts`:

- "My turn" cue: fires the instant "over and out" is recognized. This is the owner's confirmation
  that his sign-off was heard and the assistant is now taking the turn. Reuse the existing
  water-drop for this.
- "Your turn" cue: fires when the assistant finishes speaking (or is interrupted) and the
  microphone is live again for the owner. A different, unmistakable tone.

The owner must be able to tell the two apart without looking. Keep them short and unambiguous.

## Decisions already made - do not re-litigate

1. The sending phrase is "over and out", required as the complete phrase and only as the last
   thing said; plain "over" or plain "out" alone never triggers. Strip the phrase before the
   command text reaches the brain. Interrupt words are "stop", "wait", "shut up".
2. The brain runs server-side on the Gateway, not in the browser. The browser stays thin: capture
   audio, get a transcript, hand the transcript to the brain, speak the reply. This keeps the
   model key and the fleet tools server-side and matches the rule that clients talk only to the
   Gateway. A latency optimization - folding transcription into the brain endpoint so audio goes
   straight to it - is allowed later, but the v1 shape is three clear steps.
3. Full control, agent-decides-confirmation. The assistant acts without asking for ordinary
   actions (start a session, send a message, approve). It asks only when genuinely unsure.
   Destructive actions (delete/kill) always require a spoken confirmation. No per-action nagging.
4. Two distinct audible cues at the turn boundaries, both from `readyCue.ts` (above).
5. Sessions are referred to by human name and what they are doing, never by number, in every
   spoken line. Reuse the "{name}, in the {repo} repo" plus short-summary prose shape from the old
   phone narration. Resolving a fuzzy reference ("the latest one", "the one that needs me") to a
   specific session is the brain's job, using the `GET /sessions` roster.
6. Reuse, do not rebuild: `MicRecorder` (with its echo cancellation) for capture, `wav.ts` for
   transcode, `POST /wingman/transcribe` for speech-to-text, `POST /wingman/tts` for the voice,
   `GET /sessions` for the roster, `readyCue.ts` for the cues. The shared turn-taking logic lives
   in `packages/client-core/src/carmode/useCarMode()`; each app mounts a thin page.
7. Chromium only. Echo cancellation and the built-in recognizer are solid in the Chromium web view
   both apps use and weak elsewhere; Car Mode does not support Firefox. State this, do not work
   around it.
8. Plain English everywhere, ASCII only in code and output. No fallback programming: a failed
   transcription, an offline Gateway, or a model error is a loud, spoken, specific failure, never
   a silent stall or a guess. Fire-and-forget is banned - every voice action shows and says its
   state.
9. Phone-first, as a standalone separately-deployable page (owner decision 2026-07-11). Car Mode is
   just a web page, so there is no reason to prove it in the cockpit first; build and iterate
   directly against the phone. It is its own page and its own deploy, overriding whatever is on the
   phone when we push. Debugging is not lost: Chrome on Android exposes full developer tools to the
   desktop over `chrome://inspect` with the phone on the wireless debugger (the test Z Flip is
   already paired), so console, network, and breakpoints all work against the page running on the
   phone.
10. The brain harness is a hand-rolled C# tool-calling loop in the Gateway, not the OpenAI Agents
    Software Development Kit and not (for v1) an agent framework. See New build A for the full
    reasoning: the deciding factor is that the brain lives in the .NET Gateway and wants its tools
    in-process, and the OpenAI Agents Software Development Kit has no C# version (it would force a
    separate Python service). Model compatibility is not the constraint - that Software Development
    Kit can drive open-source OpenAI-compatible models fine. If a framework is later wanted, it is
    the .NET-native Microsoft Agent Framework, in-process, not the OpenAI one.

## A responsive interface is everything (load-bearing principle, owner, 2026-07-12)

Car Mode is used eyes-nearly-free and hands-free, so the single most important quality after
"it works" is that the owner is never left staring at a screen that has not changed, wondering
whether his action registered. The owner stated this plainly: waiting for the voice, the model,
or the network is completely acceptable - as long as he can see and hear that he is waiting. A
silent, unchanged screen during that wait is the defect, not the wait itself.

The rule, applied everywhere in Car Mode: the instant anything happens - a tap, a recognized
"over and out", a recognized "stop" - change the visible state first, synchronously, before any
asynchronous work begins. Do not run the transcription, the brain, or the text-to-speech and then
update the screen; flip the screen into its next state (for example Thinking) as the very first
thing, and only then start the asynchronous pipeline. The audible acknowledgement cue must fire on
the same synchronous beat.

While the assistant is thinking or waiting, play a soft, low-volume ambient cue in the background
so the owner hears, without looking, that work is in progress - the pattern other good voice
assistants use (a gentle, quiet, recurring sound, not a loud one-shot). This cue must use the soft
water-droplet sound from dictation (`readyCue.ts`), not a harsh tone; the owner finds the current
tone harsh and wants the whole experience to move toward the gentle water sound. Stop the ambient
cue the moment the reply audio starts.

This principle sits above the individual fixes: every state Car Mode can be in must show and sound
like what it is doing, and every transition must be immediate on the screen even when the real work
behind it takes seconds.

## The work, in phases

Each phase ships alone: implemented, merged to origin/main per the trunk rule, deployed to the
phone, and exercised by the owner on the phone before the next phase begins. Every phase is built
and proven directly on the phone (decision 9) - it is just a web page, deployed as its own
standalone page under `/m`, and debugged over `chrome://inspect`. The standalone page exists from
Phase 1, so there is no separate "ship on the phone" phase; every phase is on the phone.

- Phase 1 - Turn-taking core and the audible handshake, no fleet brain yet. Stand up the standalone
  Car Mode page under `/m` with a screen wake-lock, and the two-state machine in `useCarMode()`:
  "over and out" ends the turn; "stop" interrupts; both audible cues fire. Wire it end to end with
  a stand-in reply - transcribe the command through `POST /wingman/transcribe`, show the text on
  screen, and speak a canned acknowledgement through `POST /wingman/tts` that the owner can
  interrupt. This phase exists to prove new build B - the hardest unknown - before anything is
  built on it. Proof, on the phone: speak a sentence, pause mid-thought without it jumping in, say
  "over and out", hear the "my turn" cue and see the transcript; while it speaks, say "stop" and
  confirm it goes silent and gives the "your turn" cue. A written finding on whether the phone's
  built-in speech recognition survives barge-in (the assistant's own voice playing), or whether we
  moved to an on-device keyword model on the echo-cancelled microphone stream.

- Phase 2 - The fleet brain, read-only. New build A with read-only tools only: `CarModeBrain` and
  `POST /carmode/turn` on the Gateway (a hand-rolled C# tool-calling loop per decision 10,
  authenticated by the caller's device key per the Gateway auth rule), with tools backed by
  `GET /sessions` and the history endpoints. Wire the phone page to it. Proof, on the phone, by
  voice: "how many sessions need me" answered with a count and the human names; "show me the latest
  one" / "what is the devthrottle session doing" answered aloud, naming the session and repo and its
  short summary, never a number.

- Phase 3 - Full control and the confirmation policy. Add the acting tools (start a session in a
  named repo, message a session, approve) and the destructive tools (delete/kill) gated behind a
  spoken confirmation, with the assistant free to ask when unsure. Proof, on the phone, by voice:
  start a session in the devthrottle repo and confirm it appears; message a running session and
  confirm it arrives; ask to delete a session and confirm the assistant requires a spoken "confirm"
  before doing it.

- Phase 4 - The real-world walk. Hardening and polish under real use: barge-in robustness in noise
  and while moving; loud, spoken error and offline states (Gateway unreachable, out of credits,
  model failure); latency tuning (the option to fold transcription into `POST /carmode/turn`); a
  narration-quality pass so the assistant sounds like a competent development manager, not a form
  reader; unit tests for the turn-taking machine and the brain's tool loop. Proof: the owner walks
  outside with the phone in his pocket and runs the fleet by voice, hands-free, for a real stretch -
  who needs him, read me that one, start a session, message an agent - with the audible handshakes
  carrying the whole interaction and nothing failing silently.

## Definition of done for the mission

1. All phases merged to origin/main, each verified by the owner on the real phone against the real
   Gateway (the standalone `/m` Car Mode page), and Phase 4 verified on the phone while walking.
2. From the phone, hands-free, the owner runs the whole fleet by voice: he can ask who needs him
   and get human names, have a session read to him (named, never numbered), start a session in a
   repo, and message an agent - and it feels like directing a competent development manager on a
   phone call.
3. The walkie-talkie discipline holds: it never speaks until "over and out"; he can pause to think
   as long as he likes; "stop" cuts it off instantly; and both turn boundaries are audible so he
   always knows whose turn it is without looking.
4. Full control works with the agreed confirmation policy: ordinary actions just happen; deleting
   or killing a session requires a spoken confirmation.
5. The two new builds are real and tested: the Gateway tool-calling brain (with the read, act, and
   confirmed-destructive tools) and the client turn-taking machine (with proven end-word and
   interrupt detection over the echo-cancelled microphone). Everything else is the reused plumbing
   listed in the core finding.
6. A final verification report (HTML, in docs/reviews/) with screenshots and a phone recording
   showing a full hands-free session: who-needs-me, read-me-that-one, start-a-session,
   message-an-agent, and a destructive action being held for confirmation.
