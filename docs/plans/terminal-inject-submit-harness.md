# Plan: a test harness that proves reliable text injection and submit for every agent

Status: proposed (planning only - no product code changed yet)
Authors: merged from two independent passes (a Claude session and a Codex session working the same
brief in parallel), reconciled into this single plan.

---

## 1. The problem

CC Director runs many coding-agent command-line tools (Claude Code, Codex, Gemini, OpenCode, Grok,
Copilot, Cursor, Pi), each in its own pseudo-terminal (ConPty on Windows). Three code paths inject
text into a running agent's composer and then press Enter to submit it as a turn:

1. The Control Application Programming Interface path, including agent-to-agent "fleet" messages.
2. The on-screen composer text box at the bottom of the terminal view.
3. Transcription / dictation (voice converted to text).

The recurring failure: the text reaches the agent's composer but the Enter is not registered, so
the text sits there unsubmitted and the agent looks idle. It is worst for the fleet/REST path into a
Codex session, and for large pastes into Claude (the composer expands and repaints, and the Enter is
lost to the redraw). The root difficulty is a race against each terminal user interface's own screen
repainting, made worse by different handling for small inline text versus large or multi-line text.

This document plans a **test harness** that makes reliable submit provable, per agent, on the real
machine - and turns the "solve it once for all agents, or per agent" question into an evidence-based
decision instead of guesswork.

## 2. What already exists in the code (the ground we build on)

- `src/CcDirector.Core/Drivers/` - a per-agent driver layer. `IAgentDriver.SubmitAsync` is the
  submit verb. `ClaudeDriver`, and `CodexDriver`/`PiDriver` (through `TerminalSubmit.cs`), do an
  echo-verified submit: type the text, poll the terminal byte stream until the composer echoes it
  back, then press Enter (byte 0x0D) as a separate keystroke. `GenericDriver`, `CursorDriver`, and
  `CopilotDriver` still do a blind submit (type, wait fifty milliseconds, press Enter).
- `src/CcDirector.Core/Drivers/TerminalSubmit.cs` - the shared echo-verified primitive (echo timeout
  four seconds, poll fifty milliseconds, forty-millisecond settle before Enter).
- `src/CcDirector.Core/Backends/ConPtyBackend.cs` - `SendTextAsync` is the blind path (text, fifty
  milliseconds, 0x0D) and also the large-input path: input over one thousand characters, or
  containing any newline, is written to a temporary file and sent as an `@relative/path` reference
  instead of being typed (`Input/LargeInputHandler.cs`), then nudged by `AtReferenceSubmitVerifier`.
- `src/CcDirector.Core/Sessions/Session.cs` `SendTextAsync` (near line 1407) is the chokepoint: for
  ConPty sessions it calls `Driver.SubmitAsync`; otherwise the backend's blind path.
- `src/CcDirector.ControlApi/FleetMessaging.cs` `BuildFramedMessage` deliberately keeps a fleet
  message on a single line so it does not trip the newline rule into the temporary-file path.
- Bracketed paste (the escape sequences `ESC[200~` and `ESC[201~`) is only handled on the render
  side today (the ANSI parser tracks mode 2004); it is not used on the input/send side.

## 3. Goal and the definition of "reliable submit"

One sentence: **for every agent we support, a caller can hand us a string of any size or shape, and
we will get that exact string into the agent's composer and cause the agent to actually start
processing it as one turn - and we can prove all three of those things happened.**

A submit is reliable only when all of these are true, each proven independently:

1. **Delivered** - the bytes reached the composer.
2. **Intact** - what the composer holds equals what we sent (no dropped characters, no stray leading
   slash, no half-pasted block; newlines handled per the agent's rules).
3. **Submitted** - the Enter registered and the agent left the idle/compose state and began a turn.
   This is the claim that silently fails today.
4. **Recovered** - after the turn the session returns to an idle or waiting state, or the harness
   records a bounded, explainable failure.

A test that only asserts "we wrote the bytes" is worthless here - that always succeeds. The entire
value of the harness is claim 3, proven by the agent's own behavior, not by our write returning.

### Acceptance criteria

- One command runs the full matrix and emits a machine-readable pass/fail table (agent by case by
  route) plus a human-readable report, with, per case: the sent text, the observed composer echo,
  and the evidence of submission (a token the agent echoed back, or a transcript delta).
- Green means delivered AND intact AND submitted, observed live against the real command-line tool,
  and passing three consecutive runs (see the flake rate below).
- **Zero parked-composer passes.** After a timeout, the harness must distinguish "the agent did work
  but answered wrong" from "the composer still contains our text and no turn started."
- Large and multi-line cases must prove the actual payload was consumed, not merely that an
  `@.temp/...` reference was typed.
- Every failure produces enough artifacts to debug: raw terminal bytes, ANSI-stripped screen text,
  the sent bytes, the route used, timestamps, buffer byte deltas, session id, agent kind, executable
  path and version, and the transcript locator when one exists.

## 4. Architecture: two layers, and why we need both

**Layer A - deterministic, no real command-line tool (unit / component).**
Drive `IAgentDriver.SubmitAsync`, `TerminalSubmit`, `AtReferenceSubmitVerifier`, and
`ConPtyBackend.SendTextAsync` against a recording fake backend that (a) records every write, and (b)
can be scripted to simulate a repainting composer - it feeds bytes back into a
`CircularTerminalBuffer` on a delay, withholds the echo, or injects a cycling-placeholder repaint -
so we reproduce the lost-Enter race deterministically. This proves the logic: "if the composer never
echoes, retry then throw"; "if a slash corrupts the echo, keep polling"; "large input takes the
temporary-file path"; "the byte order is text first, no Enter before echo, Enter only after echo
plus settle." Build on the existing `RecordingSessionBackend` rather than inventing a new fake.

Layer A cannot prove the timings are right for a real agent - it only proves our code does what we
think. That is what Layer B is for.

**Layer B - live, against the real installed command-line tools (integration; the acceptance gate).**
Spawn a real ConPty running the real agent, inject each case, and read the agent's real reaction.
This is the part the request is really about ("test each agent we have on this machine"). It is the
only thing that catches "Codex changed its composer repaint and now our forty-millisecond settle is
too short." Layer B is environment-dependent (which tools are installed, and their versions), so it
self-discovers what to run (section 6) and records versions in the report.

## 5. The test matrix

Rows are the agents actually installed on this machine, discovered from the same detection code the
app uses (the agent library / tool detection), not hard-coded. A missing tool is reported as
**skipped with a reason**, never silently dropped and never counted as failed.

Columns are input shapes chosen to hit every branch in the send code:

| Case | Input shape | Why it is in the matrix |
|------|-------------|-------------------------|
| tiny | one word | smallest possible submit; catches the Codex dropped-Enter case on the shortest path |
| sentence | a normal sentence with spaces | the common REST and user-interface usage |
| medium-line | about five hundred characters, single line, token at start/middle/end | inline typing at size: wrapping, repaint, echo normalization |
| large-line | over one thousand characters, single line | trips `LargeInputHandler` on length into the temporary-file path; also the canonical-mode line-length danger zone (see prior art) |
| multiline | three to eight lines | trips `LargeInputHandler` on newline even when short - the fleet-message gotcha |
| leading-slash | starts with `/not-a-real-command` then asks for a token | the slash-command corruption case, plus a real slash-command submit |
| special-ascii | slashes, quotes, backticks, brackets, percent, ampersand, `@` | quoting, shell-like punctuation, code-fence effects; `@` looks like a file reference |
| unicode-data | non-ASCII in the sent payload; expected output uses an ASCII token | proves byte-accurate delivery of UTF-8 input without putting non-ASCII in harness source (repo output rule) |
| fleet-framed | wrapped by `FleetMessaging.BuildFramedMessage` | the fleet path adds sender framing and a reply hint |

Each case is defined once as data (a case record: id, text, expectation) and run against every
agent and every route, so adding an agent, a case, or a route is a one-line change. Cells that are
not meaningful for a given agent (for example, a real slash command on an agent with no slash
commands) are marked not-applicable with a recorded reason.

Priority: ClaudeCode and Codex are mandatory on every case (the two named failures). The remaining
agents are mandatory on every inline case; their large and multi-line behavior is proven, not
assumed (some may not expand an `@file` reference at all - the harness reports that rather than
guessing).

## 6. Injection entry points (routes)

The harness drives every surface named in the bug, because the failure is route-dependent:

1. **Direct** - create a ConPty `Session` and call `session.SendTextAsync(text)`. This is the
   chokepoint and exercises `Driver.SubmitAsync` for ConPty sessions.
2. **REST prompt** - `POST /sessions/{sid}/prompt` with `{ "text": "..." }`. The route phones,
   scripts, and external automation use.
3. **Fleet** - `POST /fleet/send` (or `cc-devthrottle message send`) to a local target. Exercises
   `FleetMessaging.BuildFramedMessage` plus REST delivery - the worst real-world case.
4. **Voice-turn** - `POST /sessions/{sid}/voice-turn` with `{ "text": "..." }`. This proves the
   transcription branch converges on `session.SendTextAsync` **without needing a microphone**. Audio
   quality and transcription accuracy are a different test problem; the submit harness owns only
   "given final text, does it submit."

Discovery for "each agent we have on this machine": the harness asks the agent library which tools
resolve (each driver has `ResolveExecutable`; the agents endpoint reports availability), runs Layer B
only for those, and records each executable path and version in the report.

## 7. Verification: how we prove each claim (the hard part)

For each case the harness records the target buffer's `TotalBytesWritten` cursor **before**
injecting, then:

**Delivered and intact (claims 1 and 2)** - reuse the exact normalization the drivers already use
(`TerminalSubmit.StripAnsi` plus `NormalizeForEcho`) to read the composer echo out of the byte
stream since the cursor, and assert it contains the normalized sent text and is not slash-corrupted.
For the temporary-file cases, "intact" instead means the `@relative/path` reference appears AND the
temporary file's contents equal the sent text.

**Submitted (claim 3)** - this must come from the agent. Several independent signals, strongest
first; green requires at least one of the top three:

1. **Transcript delta (best, where supported).** For agents with a readable transcript
   (`SessionHistoryReader.IsSupported`), snapshot the user/assistant message count before, then wait
   for a NEW user message matching our text. Ground truth. Claude (JSON-lines transcript, located by
   the preassigned session id) and Codex (rollout, via `CodexRolloutLocator`) support this; Pi via
   its session locator where available.
2. **Sentinel echo-back.** Make the injected text an instruction the agent obeys verbatim, for
   example: `reply with exactly the token INJECT_OK_<agent>_<case>_<run> and nothing else`. Then
   watch the output for that token. Proves submission end-to-end even for agents with no readable
   transcript (Gemini, OpenCode, Grok, Copilot). It costs a real model turn, so it is the primary
   proof for the no-transcript agents and a deep-verify option for the others. For payload-shape
   cases, make the token depend on content inside the body (for example, "if the payload contains
   MIDDLE and END markers, reply with the token") so a truncated large paste cannot pass.
3. **Turn-complete notification, where the agent emits one.** Some agents emit an operating-system
   command notification sequence (OSC 9 / 99 / 777) or a bell on turn end (this is how cmux lights
   its "agent needs you" ring). Where present it is a clean, scrape-free signal.
4. **Activity transition (weakest; fail-fast only).** The session leaves idle and goes Working
   within a timeout. Necessary but not sufficient (a spinner can flip state without our turn being
   the cause), so it is used only to fail fast, never as the sole proof of green.

**Parked-composer detection (the today-bug, made impossible to miss).** On any timeout: dump the
last screen and check whether the normalized sent text is still visible in the composer; compare
buffer byte growth since injection; record whether an Enter byte was actually written by the
driver/backend. If growth stays below a threshold and the text remains visible, classify the failure
as `parked_composer` - the exact signature we are chasing.

**The print/stdin oracle (how we know the expected answer).** Most agents have a non-interactive
path: `claude -p` (reads standard input, up to ten megabytes, with a structured stream output),
`codex exec`, `aider -m`. For each case the harness can run the same input through that path to get a
deterministic reference result, then assert the interactive composer path produced the equivalent
received turn. The batch path has no composer, no paste, no Enter race, so it is a trustworthy
oracle - but it is NOT a substitute for the live composer test (it does not exercise the thing that
is broken); it just gives every case a known-good expected value instead of hand-written
expectations.

This is the expect/pexpect discipline applied twice: wait for the echo before pressing Enter
(already in the drivers), and wait for the agent to receive the turn before declaring success (new,
and the missing half today). We poll with a timeout; we never fixed-sleep-and-assume.

## 8. "Once for all" versus "per agent": making it evidence-based

The harness does not assume one strategy fits all - it measures it, and produces two decision
artifacts:

**A strategy classification, per agent and case, after live runs:**

- `echo_verified_ok` - inline echo-wait plus a separate Enter works.
- `blind_ok` - the blind backend path passed, but no echo strategy has been proven.
- `needs_echo_verification` - the blind route parks or flakes; the echo route passes in an
  experiment.
- `needs_bracketed_paste` - a large or multi-line direct paste fails, but a bracketed-paste
  experiment passes.
- `needs_file_reference` - only the `@file` style works.
- `unsupported_large_input` - the current shared `@file` path does not make sense for that agent.

**A flake rate.** Every case runs N times (for example twenty) and submit-success is reported as a
percentage, not a single pass/fail, because the failure is intermittent - a single green run is
exactly how this bug hides today. "Reliable" is defined as a threshold (for example one hundred
percent over twenty runs). This number is the regression signal: it must not drop when the submit
code changes.

Candidate strategies are put under test on the SAME matrix, behind harness flags, so they are
compared head-to-head rather than guessed - and only the current product behavior is an acceptance
gate; the others collect evidence for the later change:

- `--submit-strategy current` (the shipped behavior; the gate)
- `--submit-strategy echo-verified`
- `--submit-strategy bracketed-paste`
- `--submit-strategy blind`

If one strategy makes the flake rate zero for every agent, that is the evidence to collapse them
onto one shared base class. If Codex needs a settle and Claude does not, or if bracketed paste fixes
large input for some agents and not others, the tables show exactly that, and any divergence is
justified by data rather than folklore.

## 9. Prior art, and exactly what we take from it

Marked ADOPT (do it), TEST (put under experiment on the matrix), or AVOID.

- **tmux `send-keys` versus `load-buffer` + `paste-buffer`.** `send-keys` simulates individual
  keypresses, so a multi-line payload sends raw newlines and a terminal user interface not in
  bracketed-paste mode treats each as Enter and submits on the first line, losing the rest
  (documented live in the pi project). The community fix is to load the bytes as one block and paste
  them through the internal buffer (`paste-buffer -p` adds bracketed-paste markers only if the app
  requested them), then send a separate Enter. Same shape as our `@file` trick. _ADOPT the
  "separate Enter" rule (already true); TEST block-paste versus the `@file` path head-to-head._
- **Bracketed paste (the app writes `ESC[?2004h`; the terminal wraps a paste as
  `ESC[200~ ... ESC[201~`).** Lets the agent receive a whole multi-line block atomically and decide
  itself when to submit - it does not submit on interior newlines. To submit, send a lone Enter
  AFTER `ESC[201~` (outside the brackets). This directly targets both named failures (the
  large-Claude-expand race and the multi-line fleet message). It only works if the agent turned
  bracketed paste on, so the harness must track each session's mode-2004 state (our ANSI parser
  already sees it on the render side) and bracket only when it is on; bracketing an app that did not
  request it injects literal garbage. _TEST as the primary large/multi-line strategy, gated on the
  observed mode-2004 state._
- **Byte re-encoding can eat newlines.** With some multiplexer key-encoding modes, the carriage
  return inside the paste brackets is re-encoded and apps that do not decode it lose every newline.
  Lesson: **verify the exact bytes arriving at the pseudo-terminal, not just what we sent.** _ADOPT
  as a harness assertion._
- **Canonical-mode line-length truncation.** A pseudo-terminal in canonical mode caps a single
  input line (roughly one to four kilobytes) and truncates longer lines with a bell. Our
  over-one-thousand-character single-line case is in this danger zone. _ADOPT as an explicit check
  on the large-line case; chunking is the fallback if the full line did not arrive._
- **expect / pexpect "send then expect."** Never fire input blindly - block until an observable
  ready marker, then send; pexpect also sleeps about fifty milliseconds before every write to dodge
  the echo/mode race. This is exactly our two-stage verify. _ADOPT; it justifies keeping a small
  settle and formalizing the ready-gate._
- **cmux (a multi-agent runner).** Does nothing clever for paste - it leans on a real terminal
  emulator's native bracketed paste and a socket command that writes into the target pane's
  pseudo-terminal, and uses the operating-system-command notifications for turn-complete. Takeaways:
  bracketed paste is the industry-standard atomic-paste answer, and those notifications are a cleaner
  "turn done" signal than scraping the scrollback. The non-applicable part is platform: cmux is
  macOS/Ghostty; we must prove behavior against Windows ConPty input semantics. _ADOPT the
  notification as verify signal 3; TEST bracketed paste per above._
- **Print / standard-input path.** `claude -p`, `codex exec`, `aider -m` bypass the composer
  entirely. _ADOPT as the expected-value oracle (section 7), not as the thing under test._

Net: the research points at **bracketed paste as the leading candidate to make large and multi-line
submit reliable in one place**, with the harness proving it per agent (gated on each agent's
paste-mode state) rather than assuming it. That is exactly the "once versus per agent" decision
section 8 makes evidence-based.

Sources: tmux(1) manual; the xterm bracketed-paste specification; the pi, claude-code, and tmux
issue trackers on multi-line paste and lost newlines; the pexpect documentation (pre-send delay and
the canonical-mode bell); the cmux project readme; the Claude Code headless documentation.

## 10. Deliverables and where they live

**Layer A (fast, runs in continuous integration / `dotnet test`):**

- `src/CcDirector.Core.Tests/Drivers/TerminalSubmitTests.cs` - fake backend; echo timing; retry;
  slash-corruption where applicable; large-input fallback; byte-order assertions.
- `src/CcDirector.Core.Tests/Input/AtReferenceSubmitVerifierTests.cs` - deterministic tests for dead
  windows, settling windows, streaming proof, and the maximum-attempt warning.
- A fleet-framing test (in the matching Control Application Programming Interface test project) that
  `BuildFramedMessage` stays single-line under normal input, so fleet messages avoid accidental
  temporary-file routing.

**Layer B (live, opt-in, needs the real tools and a machine that can spawn them):**

- `tools/harnesses/terminal-inject-harness/` containing `TerminalInjectHarness.csproj`, `Program.cs`,
  a `README.md`, a `cases.json` data file, and (if the console grows) helper types
  `AgentProbe`, `LiveSessionRunner`, `SubmitVerifier`, `ResultWriter`.
- It references `CcDirector.Core` (and the Control Application Programming Interface project if it
  hosts the in-process API for the REST and fleet routes). It launches each agent through the agent
  plugin registry and a real `ConPtyBackend`, waits for the terminal user interface to become ready,
  runs the matrix across the four routes and the selected strategy, writes a report, and exits
  non-zero if any agent falls below the reliability threshold.
- Flags: `--agent`, `--case`, `--route`, `--runs`, `--repo`, `--timeout`, `--submit-strategy`,
  `--keep-sessions`, `--out`.

**Result artifacts (per run and summary):**

- `summary.json` (machine-readable pass/fail matrix) and `summary.md` (human-readable table grouped
  by agent and route).
- `runs/<timestamp>-<agent>-<case>-<route>/` with `raw-terminal.bin`, `screen.txt`, `events.jsonl`,
  `sent-payload.txt`, `temp-file.txt` (when temporary-file routing is used), and
  `transcript-proof.json` (when transcript proof exists).

Per-result schema fields: agent, case, route, strategy, run, status, failureClass, expectedToken,
tokenObserved, turnStarted, returnedIdle, echoLatencyMs, enterWrittenAtMs, bytesAfterEnter,
transcriptProof, durationMs.

Failure classes: `tool_missing`, `startup_timeout`, `not_ready`, `echo_missing`,
`echo_slash_corrupted`, `parked_composer`, `wrong_output`, `turn_timeout`, `permission_blocked`,
`large_input_unsupported`, `harness_error`.

Example runs:

```
dotnet run --project tools/harnesses/terminal-inject-harness -- --all-installed --runs 3 --route direct --out artifacts/terminal-inject
dotnet run --project tools/harnesses/terminal-inject-harness -- --agent Codex --case tiny --route fleet --runs 20 --keep-sessions
dotnet run --project tools/harnesses/terminal-inject-harness -- --agent ClaudeCode --case multiline --submit-strategy bracketed-paste
```

## 11. Safety and operational notes for the live harness

- Run the live agents in a **disposable throwaway repository** under the output directory by default,
  NOT the devthrottle working tree, so agents cannot touch real files. Prompts must instruct "do not
  modify files" and must not require tool use.
- Use one fresh session per agent for the matrix where possible, and restart the session after any
  failure so a bad state does not contaminate later cases.
- Do NOT run all agents in parallel at first. Terminal races and model latency are hard enough; run
  sequentially, and add parallelism only after the harness is stable.
- Record version probes using the same validation arguments the tool-detection code uses.
- Keep bracketed paste and the other alternate strategies behind explicit experimental flags until
  the current-product matrix has a baseline. Only `--submit-strategy current` gates the product.
- Never kill the user's running Director or sessions to run the harness; spawn and clean up only the
  harness's own sessions.

## 12. Phasing (each phase ends in something runnable and provable)

Big plans here are phased, and each phase must end deployed on the real machine and demonstrable
before the next begins - no build-for-hours-then-test.

- **Phase 1 - characterize today, deterministically.** Build Layer A on the scriptable-echo fake and
  lock in the current drivers' behavior under test (capture today's behavior before changing
  anything). Ends: `dotnet test` green, reproducing the lost-Enter race deterministically.
- **Phase 2 - live proof for the two named agents.** Build the Layer B console harness with the tiny
  and sentence cases and the transcript and sentinel verifiers, for Claude and Codex, across the
  direct, REST, and fleet routes. Ends: a real report on this machine showing pass/fail and any
  `parked_composer` failure for Claude and Codex.
- **Phase 3 - the hard cases.** Add the large-line and multi-line cases and the voice-turn route, and
  put bracketed paste head-to-head against the `@file` path (gated on mode-2004 state). Ends: a
  report that says which strategy wins the large/multi-line cases per agent.
- **Phase 4 - fan out.** Add the remaining installed agents and produce the strategy-classification
  and flake-rate tables across the full matrix. Ends: the two decision artifacts, on this machine.
- **Phase 5 - decide and gate.** From the tables, decide what becomes the shared base strategy and
  what stays per agent, make that submit change, and use the harness as the regression gate (the
  flake rate must not drop). Ends: the submit change shipped with the harness proving it.

## 13. Bottom line

Prove reliability at the turn level, not the byte-write level. The product acceptance gate is: each
installed agent, through the same public routes users and other agents use, receives a sentinel
prompt, submits it as a real turn, answers with the expected token, and leaves artifacts that make a
dropped Enter or a parked composer impossible to miss. Only once the harness measures the flake rate
to zero do we change the submit code - and then the harness is what keeps it zero.
