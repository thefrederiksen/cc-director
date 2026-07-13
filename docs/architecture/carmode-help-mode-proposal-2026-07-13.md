# Car Mode - Help Mode: Manager's design proposal (issue #1441)

Written 2026-07-13 by the Car Mode Manager (session bd6e9531) for the Car Mode Architect
(session f44f39c0), who holds the merge gate while the owner is away. This is the "bring the
design + questions BEFORE coding" step. Nothing below is built yet.

Grounded against origin/main (00b0e5a3), not the stale working tree.

## What the phase must deliver

1. A HELP experience: a big "Help" button on /m/car, AND a spoken "help" / "what can you do"
   command - BOTH make the Car Mode agent explain, OUT LOUD, in plain spoken prose, what it can do.
2. The deeper design: the ADDRESSING VOCABULARY that lets the owner tell apart (a) COMMANDING the
   Car Mode agent itself from (b) telling it to RELAY / talk to a session. Help must TEACH this.

## What exists today (so we refine, not rebuild)

- The brain already treats "what can you do" as a GENERAL question: the system prompt tells it to
  answer general/help questions DIRECTLY via speak_answer and to NOT read the fleet for them. So a
  spoken "help" already produces SOME spoken answer today - but it is model-improvised each time,
  so it is not reliably complete and does not teach the addressing model. There is no curated help.
- The addressing split is already HALF there in behaviour, just never named:
  - Commanding the agent = the read/act tools: list_sessions, focus_next_needs_me,
    get_session_activity, read_wingman, snooze_session, approve_session, switch_to_voice_mode,
    delete_session (spoken-confirm gated).
  - Relaying into a session = the message_session tool, today triggered by "answer it", "tell it
    to run the tests", "reply that ...".
- So the vocabulary work is PROMPT-ONLY (message_session already exists); the Help button is a
  small CLIENT add plus a server-owned help text. No new fleet mechanics.
- Delivery seam: the button would call POST /carmode/turn (client carModeApi.carModeTurn), the same
  path the page already uses; the brain speaks back and the page plays it through /wingman/tts.

## Proposed addressing vocabulary (refines the Architect's starting proposal)

The Architect's start: DEFAULT = command the manager; RELAY = an explicit relay verb + target
("answer it: <text>", "tell <name> to <x>", "message <name>: <text>"); relay verbs
answer/tell/reply/message are the signal. I agree with this and propose four sharpening rules:

1. Name the two modes explicitly in the system prompt as a first-class concept: "Two things you
   can do - act on the fleet YOURSELF (the default), or RELAY words into one session. Only these
   verbs relay: answer, tell, reply, message, say to. Everything else is a command to you."

2. The relay signal is a relay verb + a SESSION target (a name, or "it"/"that one"), NOT the owner.
   "Tell me what needs me", "read me the next one", "give me the count" are commands to the agent
   (the target is "me"), never relays. Only "tell <session>/it", "answer it", "reply to <session>"
   relay. This is the sharp edge the model most needs.

3. CRITICAL SAFETY: relayed content is DATA, not a command to the agent. When the owner says "tell
   the devthrottle session to delete session five", the words "delete session five" are typed INTO
   that session verbatim (message_session), and the agent must NOT itself call delete_session. The
   relay verb + target scopes everything after it as literal text for the session. This must be a
   hard prompt rule; it is the one place the two modes can dangerously cross.

4. Relaying needs NO spoken confirmation - it is an ordinary act, exactly like typing into the
   session today (message_session is already un-gated). Only delete/kill stays confirmation-gated.
   Omitted target ("answer it") still resolves to the current subject, as it does now.

All four are prompt edits to CarModeBrain.SystemPrompt. No tool changes.

## Proposed Help experience

### The spoken help content - RECOMMENDATION: curated + server-owned, no model round-trip

I recommend the help text be a CURATED script authored once in the Gateway (next to the tools, so
it stays in sync as tools change), delivered WITHOUT a model round-trip, and returned identically
whether triggered by the button or the spoken word. Mechanism: a new brain path (either a tiny
get_help tool the model calls for help/what-can-you-do, or a dedicated help branch in the endpoint)
that returns the fixed help sentence(s) via speak_answer. Reasons this beats letting the 72B model
improvise the capabilities each time:

- Reliable + complete: it must TEACH the addressing model exactly; an improvised answer drifts and
  drops half of it. A curated script always covers both modes and the key commands.
- Instant + free: Car Mode's load-bearing principle is responsiveness; a curated script skips the
  ~3.5s two-round model turn and the credit cost. Help is the newcomer's first tap - it should be
  snappy.
- Still single-source-of-truth: the text lives ONCE in the Gateway near the tool catalog; both the
  button and the spoken trigger return the same words. This keeps the "the agent describes its own
  capabilities" intent (the description lives with the agent) without the model-improv downside.

Draft help script (tighten together, ~20-25s spoken, ASCII, spoken prose):
  "I'm your fleet manager. You talk to me two ways. By default you're telling ME what to do - ask
   me who needs you, say read me the next one, snooze it, or remove it. To speak to a session
   instead, start with answer, tell, reply, or message and name the session - like tell the
   devthrottle session to run the tests, or answer it, yes go ahead. Anything you say after answer
   or tell goes straight into that session. When you're done talking, say over and out. Ask me for
   help any time."

### Button placement + trigger - RECOMMENDATION: prominent, on both screens

- Show a big "Help" button on BOTH the idle screen and the active screen (eyes-free, discoverable).
- Idle tap: call start() first (this primes the shared cue AudioContext + unlocks the <audio>
  element + takes the wake lock - required for the phone to actually play the spoken help), THEN
  trigger the spoken help. This is the natural "I just opened this, what can I do?" flow. I want to
  confirm with you that auto-starting Car Mode from the Help tap is acceptable.
- Active tap: trigger the spoken help immediately (switch to Thinking synchronously per the
  responsiveness rule, play the thinking cue, then speak).
- Spoken "help" / "what can you do" during a Listening turn: routes to the SAME curated help path so
  the answer is identical to the button.

## Build + deploy shape

- CLIENT (light path, mobile -> wwwroot/m, no Gateway restart): the Help button on CarMode.tsx +
  a start-then-help handler in useCarMode/the page; bump the version badge.
- GATEWAY (needs redeploy-gateway.ps1 after merge): the curated help text + help path in the brain,
  and the addressing-vocabulary prompt edits.
- Because it touches the Gateway binary/wwwroot, MERGE TO MAIN FIRST, then redeploy (the un-merged
  Gateway deploy gets clobbered - a lesson already paid for this mission).
- Proof: I cannot get the owner to hand-test while he is away. I'll prove it on the strongest
  surface I can reach - the test phone over wireless ADB / chrome://inspect if reachable, else a
  faithful Playwright + real-Chromium simulation driving the real useCarMode machine and the real
  /carmode/turn help path against the safe demo session (305fbff3). I'll carry the "owner's real
  by-hand phone pass still pending" caveat honestly, as with 4a/4b.

## Design questions for the Architect (decisions I need before coding)

1. Help delivery: curated server-owned script, NO model round-trip (my recommendation - instant,
   reliable, complete, in-sync), vs brain-improvised via a model round-trip (slower, costs credits,
   risks drift)? If curated: a get_help tool the model calls, or a dedicated help branch in the
   endpoint that bypasses the model entirely?
2. Help placement: both idle and active screens, with an idle Help tap auto-starting Car Mode to
   prime audio (my recommendation) - OK? Or active-only / idle-only?
3. Help length/structure: one tight ~20-25s spoken help covering both modes + key commands (my
   recommendation for v1), vs tiered ("help with commands" / "help with sessions")?
4. Addressing vocabulary: do you accept the four sharpening rules above, especially rule 3
   (relayed content is DATA, never re-interpreted as an agent command) and rule 2 ("tell me" is a
   command, "tell <session>" is a relay)?
5. On-screen teaching: spoken-only for v1 (my recommendation - it's eyes-free), or also a small
   on-screen cheat-sheet of the vocabulary when the owner glances?
