# Session Rules - mission brief

Chartered by the owner 2026-09-02. Branch `mission/session-rules`, worktree
`D:\ReposFred\devthrottle-session-rules`, cut from `origin/main` at `fac79fb56`.

Issue: thefrederiksen/devthrottle#2644. Motivating case: devthrottle_internal#1619.

Conduct is the fleet's `mission` workflow (`cc-devthrottle workflow instructions mission`). This file
describes the WORK. It grants nothing.

---

## Why this exists, in one paragraph

A session that hits a provider usage limit is bricked: still Running, still on the roster, still
reporting itself idle, and every input returns the same notice instead of a turn. One line typed into
the composer - `/model opus` - brings it back. Nothing in DevThrottle sees the notice, so it waits for
a person. Seven of the owner's sessions sat like that overnight on 1-2 September 2026. That injection
is proven to work through `POST /sessions/{sid}/prompt`, in both directions, with a negative control
showing the fleet-message route cannot do it (devthrottle_internal#1619).

The mechanism to ACT exists. What is missing is the ability to SEE and to DECIDE.

## The owner's goal, in his words

> The goal is a QA report that clearly shows this feature working and shows also the rules of how to
> do something. The QA report needs to show that it can accept something into a screen if a rule is
> triggered. You should be able to test that fairly simply by just putting words in a terminal screen
> and then seeing that you can do something when those words happen automatically.

**That is the acceptance test for the whole mission.** Put words on a terminal screen; something
happens automatically because a rule said so; the QA report shows it. Everything else serves that.

---

## WHAT A RULE IS - settled, do not redesign it

These came from the owner directly. They are not open.

**A rule is a standing instruction, given in English, carried out by an agent.** Not a form.

- **You create it by saying it.** Plain English. A model reads it, asks about what is genuinely
  ambiguous, and BUILDS the rule: the screen condition, the cheap trigger words that keep it from
  costing anything, and the plan. The user never writes a matching expression and never picks trigger
  words - those are engineering, done by the model.
- **When it fires, an agent carries out the instruction.** It reads the screen and does what was
  asked, composing the action. **Not selecting from an enum.** An action list was the first design and
  the owner rejected it: "it's way too few things we're allowed to do."
- **The record is the product.** Every firing stores the screen, what the agent understood, what it
  decided, what it did, and what changed.

### The three owner rulings that bind hardest

1. **The terminal screen is the ONLY input.** Waiting time, token spend, conversation text and machine
   state are deferred - his words: *"Let's leave it at just the terminal for now."* Do NOT pre-build
   the wider shape behind a condition abstraction with one implementation.
2. **NO GENERATED CODE RUNS. EVER.** Not user-written, not model-written. The Gateway ships a small
   set of **verified primitives** we wrote and reviewed - `is_path_inside(target, root)`,
   `retry_delay_from(screen_text, now)`, `elapsed_since(first_failure, now)` and similar. The model's
   job is to **choose one and supply its arguments**. The stored rule holds a validated CALL - a name
   plus arguments - never a program, expression, lambda or snippet. There is no interpreter, so there
   is no sandbox to get right. A migration able to store a code string is a mistake even if nothing
   writes one.
   - **Never route around a gap** with a general-purpose primitive taking an arbitrary pattern or
     expression. That is the interpreter under another name. When an instruction needs an exactness no
     primitive provides: build the rule without that part and say so, or decline and say why, or add a
     primitive - which is a product change, written and reviewed by us.
   - The user writes English only. There is no field anywhere that accepts code, and the chosen
     primitive is never presented as an editable artifact. Which primitive ran, with what arguments,
     IS recorded in the firing record.
3. **The action space is not an enum.** See above. What bounds a rule is scope, dry-run-first, a
   ceiling, idle-only, the record, and the instruction being the authority - not a vocabulary.

### The bounds that are real

The first design claimed a six-item action dropdown was the safety boundary. **It never was** - typing
into a coding agent is already unbounded, because the session does whatever the text says. State these
instead, and hold them:

1. **Scope** - a rule only acts on sessions the account chose (agent, repo, machine, mission).
2. **Dry run first, always.** A new rule reports and types nothing until promoted. This is the bound
   that matters most: it puts a human between the instruction and its first real use.
3. **A ceiling** - cooldown and daily cap per rule per session, both required. An agent in a loop is
   the worst tail risk; the cap makes it finite.
4. **Idle only, and re-read the screen immediately before acting** - abandon if it changed.
5. **A full record of every firing.**
6. **The instruction is the authority.** The agent carries out what the owner wrote. It does not
   invent goals, widen its own scope, edit its own rule, promote itself out of dry run, or create
   rules.

**Bound 6 is the one that decays silently** - the others fail loudly. So it is the one to write tests
against: given a screen the instruction does not cover, the agent DECLINES and records why. **A rule
that never declines has not been shown to have a boundary.**

The design is drawn in `devthrottle-terminal-rules/docs/missions/terminal-rules-2026-09-02/mockups/`
(`rules-config.html`, `creating-a-rule.html`, `how-a-rule-runs.html`) and ruled in that mission's
`rulings/r11`, `r14`, `r15`. Read those three rulings; they are short and they are the owner's.

---

## The phases - all of them, this mission

The owner asked for all phases in one mission, with tasks between them.

**Phase 1 - the rule store and the contract.** Storage, CRUD, validation. A stored rule is: the
owner's sentence (the authority), what the model derived from it (condition, trigger words, primitive
calls), scope, limits, and state (dry run / asks first / live). Dry run only; nothing is typed.

**Phase 2 - authoring by conversation.** English in, a built rule out, with the model asking about
genuine ambiguity rather than guessing. Its acceptance includes a REFUSAL: an instruction needing an
unavailable primitive produces a stated refusal, not a quiet approximation.

**Phase 3 - the evaluator.** At turn end: the free checks first (screen changed, session idle, in
scope, under ceiling, trigger words present), then the agent reads screen and instruction together and
decides - including deciding NOT to act. One agent call per screen covering all candidate rules, not
one per rule.

**Phase 4 - acting.** Re-read the screen, then type through `POST /sessions/{sid}/prompt`. Handle a
confirmation picker if one appears (a model switch often opens `Switch model? 1. Yes / 2. No`).
**Known trap:** a keystroke answering a picker returns HTTP 502 from the submit verifier - *"never
started a turn ... parked in the composer unsubmitted"* - while having actually worked. Answering a
picker is not a turn. Do not read that 502 as failure.

**Phase 5 - the record and the surface.** The firing record, the rules list, the editor, the
"make a rule from this screen" entry point. Mockups exist; follow them or improve on them and say why.

**Phase 6 - the QA report.** The owner's goal. See below.

---

## THE QA REPORT - what the mission is FOR

One report, emailed to the owner with `cc-devthrottle email owner`, at the very end. It must show, as
artifacts and not as prose:

1. **A rule created from plain English** - the sentence in, the built rule out, quoted.
2. **The end-to-end demonstration the owner asked for**: words are put on a terminal screen, and
   something happens automatically because a rule said so. Quote the screen before, the rule that
   matched, what it decided, what it typed, and the screen after. This is the headline and it is the
   thing he asked to see.
3. **The real case**: a session blocked on a provider limit notice recovers with no human, verified
   by a COMPLETED TURN - not by an endpoint's own response, and not by `currentModel` alone, which is
   turn-end truth and lags a slash-command switch. A fixture of the real screen is at
   `devthrottle-terminal-rules/docs/missions/terminal-rules-2026-09-02/fixtures/`.
4. **A NEGATIVE CONTROL, and it is not optional**: a session merely DISCUSSING a usage limit is not
   convicted, and a rule declines a screen its instruction does not cover, with the reason recorded.
   A report showing only successes has not shown the feature has a boundary.
5. **How to write a rule** - the owner asked for this explicitly. Short, in his language, from the
   real UI.
6. **What is NOT proven**, stated plainly.

Every number carries its exit code and the commit it ran on, or the word PENDING. A run is only
evidence for the tree it ran on.

---

## What already exists - find it before building it

- **`POST /sessions/{sid}/prompt`** types verbatim with a trailing Enter. This is how a rule acts.
  `POST /sessions/{sid}/message` deliberately frames text as prose from a sender and CANNOT run a
  slash command - proven, with the target session refusing in its own words.
- **The live screen** is readable today over the tunnel: `screen-grid` verb in `SessionReadExecutor`,
  `SessionVerbClient.GetScreenGridAsync`. **This mission needs nothing else to read a screen.**
- **The supervisor funnel** (`CcDirector.Gateway/Supervision/`) already runs at turn end: read screen,
  classify, plan, act, record, with per-tenant settings and an attempt ladder. **This is the skeleton
  to extend, not replace.** `TerminatingFaultClassifier` shows how a cheap pre-check is done - and its
  fixed substring lists are exactly why this feature exists.
- **The menu guard** on the prompt route asks a small model to confirm a screen state before pressing
  Enter. Precedent for the model-in-the-loop and for re-reading before acting.
- **`cc-devthrottle session compact-continue`** is described in its own help as "the rescue for a
  STUCK session" - precedent for automated rescue.

## Known constraints

- **The EF migration slot is contended.** `mission/terminal-rules` holds one (unlanded phase 0) and PR
  #2379 holds three from August. Sweep before assuming it is free, testing whether each migration is
  PRESENT ON MAIN rather than whether it differs from the merge base, and fetch with `--prune` - a
  squash-merged branch otherwise still votes. Coordinate with the Terminal Rules Architect (session
  4a41f009) rather than colliding.
- **A machine-wide test-suite lock** serialises suites across worktrees. A small filtered run takes it
  exactly like a full gate. Production incidents outrank this mission for that lock, without argument.
- **The screen store** (turn-end screens kept 7 days, readable while the machine is offline) is built
  but NOT landed - it is phase 0 of the Terminal Rules mission. This mission does not depend on it and
  must not wait for it. It is what will later let a rule be authored from a past screen.

## Out of scope

- Rules triggered by anything other than the terminal screen (ruling 11).
- Any execution of generated or user-supplied code (ruling 15).
- Landing the Terminal Rules phase 0 screen store - that belongs to session 4a41f009.
