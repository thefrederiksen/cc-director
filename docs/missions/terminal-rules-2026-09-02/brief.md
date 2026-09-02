# Terminal Rules - mission brief

Chartered 2026-09-02. Issue: thefrederiksen/devthrottle#2644. Motivating case:
thefrederiksen/devthrottle_internal#1619. Branch: `mission/terminal-rules`.
Worktree: `D:\ReposFred\devthrottle-terminal-rules`.

Conduct is in the fleet's `mission` workflow (`cc-devthrottle workflow instructions mission`).
This file describes the WORK only. It grants nothing.

---

## Why this mission exists

A session that hits a provider usage limit is bricked. It is still Running, still on the roster,
still reporting itself idle, and every input returns the same notice instead of a turn:

```
You've reached your Fable 5 limit. Run /usage-credits to continue or switch models with /model.
```

One line typed into the composer - `/model opus` - brings it back. Nothing in DevThrottle sees the
notice, so it waits for a person. Seven of the owner's sessions sat like that overnight on
1-2 September 2026.

That injection is proven. `POST /sessions/{sid}/prompt` moved a live session's model in both
directions, verified against `currentModel` read from `session list --json`; the same text through
`POST /sessions/{sid}/message` did nothing, because that route deliberately frames a message as
prose and the target read it as a request and declined. Both transcripts are on internal#1619.

So the mechanism to ACT is built and proven. What is missing is the ability to SEE and to DECIDE.

## What the owner asked for, in his words

> We should definitely push the terminal to the Gateway because it would help us in so many ways.
> The wingman transcription, potentially dealing with menu selections in the voice wingman. We
> don't need to keep them very long, but seven days I would say. It should obviously be scoped to
> the tenant so that only the person that has that account can see them.

and, on the feature itself:

> The user should be able to set up rules: when you see this message in the terminal window, do
> this. We need a rule language. And we need a small language model involved in these decisions.
> This would be a massively beneficial new feature for making our agents more autonomous.

## Settled rulings - do not re-litigate these

| ruling | decided by |
|---|---|
| Terminal screens are pushed to the Gateway and stored. Not opt-in. | owner |
| Retention is **7 days**, separate from session history's 90. | owner |
| Storage is **tenant-scoped, always**. Only the owning account can read a screen. No cross-account read - not for support, not for the morning report. | owner |
| The store is a **Gateway-wide** facility, not a private input to the rules engine. The wingman's screen readers are a first-class consumer. | owner |
| Build now, beside the turn-push mission (#2638), as its own store and its own push message. The two missions must not edit the same files. | owner |
| The judge answers with a **rule id from a closed set, or none**. It never supplies text to type. | architect |
| One judge call per screen, covering all enabled rules - not one call per rule. | architect |
| Every action is something a person could have typed into that session. No shell execution. | architect |

## What already exists - find it before you build it

The machinery is largely built. A Worker that rebuilds any of this has failed the task.

- **The capture.** `src/CcDirector.Core/Storage/TurnReviewLogger.cs` already snapshots the resolved
  screen every time a session flips Working -> WaitingForInput, and writes it locally on the
  Director with 7-day retention. The right content at the right moment. It just never leaves the
  machine.
- **The pull path.** `screen-grid` verb in `src/CcDirector.ControlApi/SessionReadExecutor.cs`;
  `SessionVerbClient.GetScreenGridAsync` on the Gateway side. Six call sites today, all pulls over
  the tunnel: `GatewayWingmanVoiceEndpoint`, `WaitingScreenReader` (twice), `WingmanVoiceService`,
  `GatewaySupervisorEnvironment`.
- **The funnel.** `src/CcDirector.Gateway/Supervision/SessionSupervisor.cs` - runs at turn end, reads
  the screen, classifies, plans, acts, records. Per-tenant settings in `SupervisorSettings.cs`, an
  attempt ladder in `SupervisorPlanner.cs`, an episode guard so it cannot re-fire on a session it
  already rescued. **This is the skeleton the rules engine extends. It is not to be replaced.**
- **The classifier.** `TerminatingFaultClassifier.cs` - five hardcoded fault classes from fixed
  substring lists. Its own comments contain the design argument for this mission; read them.
- **The judge precedent.** The `MenuGuard` block on the prompt route in `GatewayEndpoints.cs` asks a
  small model to confirm a screen state before pressing Enter. Same shape, already shipped.
- **The store precedent.** The turn-push mission's phase 0 (#2640) built the conversation store the
  Director pushes turns into. Read it for the shape of a Gateway store, the migration, the tenant
  scoping and the retention job - then build a SEPARATE one. Do not extend theirs.

## Why today's code misses the case

Checked against every signature list in `TerminatingFaultClassifier.cs`, using the real string read
off a live blocked session:

```
NOTICE: You've reached your Fable 5 limit. Run /usage-credits to continue or switch models with /model.
matched signatures: NONE
classification: SessionFaultClass.None
```

`None` means "the turn ended cleanly - nothing to recover", so the funnel stops before its model
step. That step only accepts `Unclassified`, which requires an error banner the notice does not
carry.

Note also that the notice's remembered wording ("You have reached your Fable limit") differs from
its real wording in two places. A longer substring list is not the fix.

## The rule shape

Per account. Where it applies, what to look for, what to do.

```yaml
- id: model-limit-fallback
  name: Switch to the fallback model when the current one is capped
  enabled: true
  scope:
    agents: [all]
    state: idle
  when:
    screen: >
      The agent says it has run out of usage, credits or allowance for the model it is
      currently running, and offers switching models as a remedy.
    hints: ["limit", "usage-credits", "out of credits", "/model"]
  then:
    - action: switch-model
      model: opus
    - action: notify
      channel: owner-email
  cooldown: 15m
  max-per-day: 6
  dry-run: false
```

`when.screen` is the question the judge answers. `when.hints` is a free tripwire: if present and
none appear on the screen, the judge is never asked and the rule costs nothing. A rule with no
hints always asks.

Actions in v1: `prompt` (types text through the prompt route - the general case), `switch-model`
(sugar over `prompt`, picker handled), `notify`, `snooze`, `interrupt`, `escalate`.

## Safety - every action is a keystroke into live work

- Never twice on the same screen. A fire is keyed to the screen it fired on.
- Cooldown and a daily cap, per rule per session. Both required, both defaulted.
- Idle only. A rule never fires while a session is Working.
- Re-read the screen immediately before the keystroke and abandon rather than press into a picker
  that was not expected - the menu guard's rule.
- Every evaluation and every fire is recorded with the screen it saw and the verdict it got.
- New rules start in `dry-run`.

## Two mechanics found in the proof-of-concept

1. `/model <name>` can open `Switch model? 1. Yes / 2. No` and wait, when the conversation is cached
   for the current model. A single injected line does not finish the switch there. `switch-model`
   must read the screen after injecting and answer the picker if it is present.
2. A keystroke that answers a picker returns HTTP 502 -
   `[SubmitVerifier] '1' never started a turn ... parked in the composer unsubmitted` - while having
   in fact worked. Answering a picker is not a turn. The action layer must not read that as failure.

## Phases

Each phase is one or two pull requests, merged before the next begins.

**Phase 0 - the screen store and the push.**
The Director pushes the turn-end screen it already captures; the Gateway stores it per tenant with
7-day retention; the wingman's screen readers and the supervisor read the store, falling back to the
tunnel pull only for a screen the store does not have.
*Proves:* a screen captured on one machine is read back from the Gateway with that machine offline,
and a voice turn completes with no tunnel screen read.

**Phase 1 - the rule store and the contract.**
Rule storage, the rule language, CRUD, validation. Dry-run only. Nothing is typed.
*Proves:* a rule matching the live limit screen is recorded as would-have-fired, and no keystroke
was sent.

**Phase 2 - the judge.**
One small-model call per screen, all enabled rules, answers with a rule id or none.
*Proves:* a NEGATIVE control - a session merely discussing a usage limit is not convicted. This
proof is the phase; a phase that only shows a true positive is not done.

**Phase 3 - the actions.**
`prompt`, `switch-model`, `notify`, `snooze`, guarded as above.
*Proves:* a session driven into a real limit state recovers with no human, verified by a completed
turn - not by the endpoint's own response, and not by `currentModel` alone (it is turn-end truth and
lags a slash-command switch).

**Phase 4 - authoring from a screen.**
"Make a rule from this" on a stored screen in the Cockpit. The model drafts the `screen:` condition
in plain English; the owner edits and saves it in dry-run.
*Proves:* a rule authored from a stored screen fires on a later occurrence.

**Phase 5 - the record and the report.**
The ledger view and the mission record.
*Proves:* every fire in the preceding week is explainable from stored rows alone.

Phase 0 is worth shipping on its own twice over: stuck sessions become visible from the phone, and a
tunnel round trip comes out of every voice turn.

## Out of scope

- Live terminal streaming. This is the turn-end screen, not a stream.
- Rules that run shell commands.
- Rules authored by a model without the owner approving them.
- Any edit to the turn-push mission's files (#2638).

## Test target

Session 101 (`816aa444`, "devthrottle_internal - rm") has been blocked on the real notice since
2026-09-02T09:44:12Z. Use it for phase 1 and 2 matching proofs while it lasts; capture its screen
rows into a fixture before it is rescued, so the proof survives it.
