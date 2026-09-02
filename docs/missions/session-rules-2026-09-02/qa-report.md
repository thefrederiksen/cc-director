# Session Rules - the QA report

**Status: IN PROGRESS. Every row below is either evidence with its exit code and the commit it ran
on, or the word PENDING. Nothing here is written ahead of the run that proves it.**

This report accumulates from the first proof onward rather than being written at the end, so that a
run which stops early still leaves an honest account rather than nothing. It is emailed to the owner
once, when the mission finishes or when it stops.

Mission: Session Rules. Branch `mission/session-rules`, cut from `origin/main` at `fac79fb56`.
Issue thefrederiksen/devthrottle#2644.

---

## What was asked for

> The goal is a QA report that clearly shows this feature working and shows also the rules of how to
> do something. The QA report needs to show that it can accept something into a screen if a rule is
> triggered. You should be able to test that fairly simply by just putting words in a terminal screen
> and then seeing that you can do something when those words happen automatically.

---

## 1. A rule created from plain English

The sentence in, the built rule out, quoted.

**PENDING.**

## 2. The headline - words on a screen, and something happens because a rule said so

**PROVED, on a real session, on commit `73273a457`.** Words were put on a real terminal screen; the
session went idle on its own; a rule stored in the real store fired on its own; `/usage-credits` was
typed into the session by nobody; and the screen afterwards shows it there.

### The rig - what was real, and what was crude

Everything in the chain is production code and real machinery:

| Part | What it was |
| --- | --- |
| The Gateway | The real Gateway built from this branch, `/healthz` reporting `2.0.4+73273a4570da4d54e3972de45bbb1a1ebca9236b`, on an isolated data root and port so it never touched the owner's own Gateway. |
| The Director | A real Director (slot 6, built from this branch), connected to that Gateway over the ordinary Director stream. |
| The session | A real `RawCli` session running `cmd` - `0234084a-2e6a-42af-b794-ca982f867266` - deliberately a plain shell, so a command typed into it either ran or it did not, and the screen says which. |
| The screen read | The real `screen-grid` verb over the tunnel. |
| The rule | Written into the real phase 1 store through `POST /gateway/rules`, which is the store's own gate. |
| The trigger | The real Working-to-idle transition. Nothing was nudged: the terminal went byte-silent for its 10-second quiet threshold, the Director flipped the session to `WaitingForInput`, and the turn-end watcher woke the evaluator. |
| The decision | One real model call - `chat/completions model=devthrottle/wingman OK: 571 chars in 18.4s`. |
| The keystroke | The real prompt verb, the same route everything else in the product types through. |
| The record | Real rows in the real firing store, read back over `GET /gateway/rules/{id}/firings`. |

What was crude, stated plainly: there is **no user interface**, and there is **no authoring
conversation** - the rule's derived parts (the plain-English screen description, the trigger words,
the stored check) were written by hand into the create route rather than derived from the sentence by
a model. Deriving them is phase 3, and row 1 of this report stays PENDING until it is built.

### The rule

Stored in dry run - the store takes no state parameter, so no caller can create a live rule.

```
id:                  e34f821a-d59d-4940-b7d1-9c5c8938faf0
the account said:    If a session's screen says it has run out of its model allowance, type the
                     command that shows me what is left.
watching for:        A session that has stopped on a notice from its model provider saying its
                     allowance for the current model is used up.
trigger words:       limit, usage-credits, allowance, out of credits
stored check:        matches_any(text=<screen_text>, terms=limit,allowance)
scope:               agent RawCli, repository C:\Users\soren\AppData\Local\Temp\ccrules\scratch
cooldown:            20 seconds       daily cap: 5
state:               dry_run
```

### The dry run first, and it typed nothing

The words were put on the screen. The session went idle. The rule fired, decided to act, and typed
**nothing**, because it was in dry run - and the record says what it WOULD have typed:

```
decision:       act
occurred:       2026-09-02T20:16:13.4132778Z
understanding:  The session has stopped on a notice from the model provider saying the Fable 5 model
                limit has been reached, which is an allowance exhaustion message.
reason:         The screen reports the model allowance for the current model is used up, and the
                instruction says to type the command that shows what is left. The screen itself
                suggests /usage-credits, which serves that purpose.
checks run:     (none - the agent named no check on this screen)
typed:          ""
outcome:        dry run: nothing was typed. It would have typed: /usage-credits
```

The Gateway's own log for that pass:

```
16:16:01.910 [RuleEvaluator] sid=0234084a-...: 1 rule(s) worth asking about
16:16:13.412 [RuleEvaluator] firing: rule=e34f821a-... sid=0234084a-... decision=act typed=no
16:16:13.481 [GatewayHost] turn-end rules: sid=0234084a-... outcome=dry-run
```

### Then a person promoted it

```
POST /gateway/rules/e34f821a-d59d-4940-b7d1-9c5c8938faf0/promote
-> state=live  updatedUtc=2026-09-02T20:16:35.2494581Z
```

A rule cannot promote itself; there is no route by which it could.

### The free checks stopping the pass, on the way - unplanned, and worth keeping

The next turn end produced the same screen byte for byte, and the pass stopped before it reached a
model at all:

```
16:17:07.477 [RuleEvaluator] stopped-before-any-rule: the screen has not changed since this session
             was last looked at
16:17:07.477 [GatewayHost] turn-end rules: sid=0234084a-... outcome=stopped-before-any-rule
```

That was not staged. It is the free-check layer doing exactly its job on a real screen: a turn end
that changes nothing costs one screen read and no model call.

### The screen BEFORE

```
[16:17:23.65] provider notice follows
You've reached your Fable 5 limit. Run /usage-credits to continue or switch models with /model.


C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
```

That is the screen the decision was made on, quoted from the firing record rather than from a
separate read - it is the text the agent actually saw.

### What the agent understood and decided

```
occurred:       2026-09-02T20:18:11.521661Z
decision:       act
understanding:  The screen shows a provider notice stating the session has reached its Fable 5 model
                limit, and the session is idle at the command prompt. The notice suggests running
                /usage-credits to see credit status.
reason:         The screen explicitly reports a provider notice that the model allowance for Fable 5
                is used up, and the session has stopped at the prompt. This matches the instruction's
                trigger condition exactly.
checks run:     (none - the agent named no check on this screen)
typed:          /usage-credits
```

### The screen was read AGAIN immediately before the keystroke

From the Gateway log, in order, within four milliseconds:

```
16:18:01.738 [HostedInferenceBrain] chat/completions model=devthrottle/wingman OK: 571 chars in 18.4s
16:18:01.738 [GatewayHost] SendCommandAsync: verb=screen-grid, sid=0234084a-...
16:18:01.739 [DirectorCommandRouter] screen-grid sid=0234084a-... : stream status=Ok
16:18:01.741 [GatewayHost] SendCommandAsync: verb=prompt, sid=0234084a-...
```

The model answers, the screen is re-read, the re-read matches, and only then is anything typed. A
screen that had moved on in those milliseconds would have been abandoned instead - see row 4.

### The screen AFTER

```
[16:17:23.65] provider notice follows
You've reached your Fable 5 limit. Run /usage-credits to continue or switch models with /model.C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
C:\Users\soren\AppData\Local\Temp\ccrules\scratch>/usage-credits
'/usage-credits' is not recognized as an internal or external command,
operable program or batch file.C:\Users\soren\AppData\Local\Temp\ccrules\scratch>
```

**`/usage-credits` is on that screen and nobody typed it.** The shell does not know the command, and
says so - which is the correct behaviour of a plain `cmd` and is exactly why a plain shell was chosen:
the shell's answer is unambiguous evidence that the text arrived and was submitted.

### A DEFECT this run found, recorded rather than tidied away

The firing's `outcome` field says:

```
outcome:  the send did not land, so the session was never reached.
```

**That sentence is wrong, and the screen above is the proof.** The prompt verb returned HTTP 502 from
the submit verifier - `never started a turn ... the agent produced under 2048 bytes, so the prompt is
parked in the composer unsubmitted` - which is the trap the mission brief warns about in those words,
and the evaluator read that 502 as a failed send. The keystroke had in fact landed. The evaluator
must not treat that 502 as a failure; that belongs to phase 4, which already owns this trap, and it is
carried there rather than patched here. Nothing else in the chain is affected: the decision, the
re-read, the keystroke and the record all happened, and only the last sentence of the record is wrong.

### What this row does NOT prove

The session was a plain shell that had been made to PRINT an allowance notice. It was not a coding
agent that had genuinely exhausted a model allowance. This row proves the MECHANISM end to end - a
screen, a rule, a decision, a keystroke, a record - and it does not prove the recovery of a real
provider limit. That is row 3, and it is not to be faked.

## 3. The real case - a session blocked on a provider limit recovers with nobody watching

Verified by a COMPLETED TURN, not by an endpoint's own response and not by the reported current model
alone, which is turn-end truth and lags a slash-command switch.

**PENDING.**

## 4. The negative control - where the boundary is

Two things, and neither is optional. A report showing only successes has not shown the feature has a
boundary.

- A session merely DISCUSSING a usage limit is not convicted.
- A rule DECLINES a screen its instruction does not cover, and the reason is recorded.

**PENDING.**

## 5. How to write a rule

Short, plain, from the real screen.

**PENDING.**

## 6. What is NOT proven

To be filled in honestly at the end, and never left empty.

As of the end of phase 1: rows 1 to 5 above are all still PENDING. Nothing has fired, nothing has
been typed, no model has built a rule from English, and there is no user interface. Phase 1's own
list of what it did not prove is in `phase-1-report.md` and is carried forward here rather than
restated.

---

## Phase 1 - the rule store, the contract, the primitives (done)

Full account, with every red quoted before its green: `phase-1-report.md`. Branch
`mission/session-rules-p1`, head `48eeb1e83`.

What phase 1 proved, all on `48eeb1e83` unless a red is named:

- A rule ROUND-TRIPS through the store - the account's sentence, the derived screen description, the
  trigger words, the derived checks, scope, cooldown, daily cap and state - read back through a
  second store over the same database.
- A rule naming a check that does not exist is REFUSED at write time with a stated reason, proved by
  writing a refused rule and reading the reason, and nothing is stored.
- A rule handing the wrong arguments to a real check is REFUSED with a stated reason - thirteen
  distinct wrong-argument cases.
- The check registry is DERIVED by reflection, non-empty, and complete: the test finds the attributed
  methods with its own independent scan and requires every one to be reachable through the registry.
- The five checks have their own tests, including `is_path_inside` against `..`, a real link, and the
  prefix collision `repo-other` beside `repo`.
- A new rule is ALWAYS in dry run - `Create` has no state parameter - and a dry-run rule cannot record
  having typed anything.
- Nothing in the rules code can type into a session, proved by a reference scan of the built assembly
  whose scanner was first run against a known-BAD input and watched to fail by name.

What phase 1 did NOT prove: the parked `CcDirector.Gateway.Tests` suite did not run (the machine-wide
lock was held, and that suite measures 48.88 minutes against a 45-minute maximum wait, so a queued run
cannot acquire it); nothing ran against a live Postgres; no rule has fired; no model has built a rule
from English; and the decline is stored but not yet earned by an agent.

---

## Runs

Every number carries its exit code and the commit it ran on. A run is only evidence for the tree it
ran on.

| What ran | Commit | Exit code | Result |
| --- | --- | --- | --- |
| Rules tests, first run against unwritten code | `a8259bcbb` | 1 | 33 failed, 1 passed (the one pass is the instrument check) |
| Rules tests, after the checks and registry | `84c25911e` | 0 | 34 passed, 0 failed |
| Validator tests, first run against unwritten code | `84c25911e` | 1 | 18 failed, 0 passed |
| All rules tests, after the validator | `5523025ec` | 0 | 52 passed, 0 failed |
| Store tests, first run against unwritten code | `522b1cee5` | 1 | 21 failed, 0 passed |
| Store tests, after the store | `515759985` | 0 | 21 passed, 0 failed |
| Types-nothing guard against a known-BAD input | `c991921d2` | 1 | 1 failed, 2 passed - named the offending type |
| All rules tests, probe removed | `7a7422119` | 0 | 76 passed, 0 failed |
| Local gate, which caught the tenant-key defect | `7a7422119` | 1 | 1 failed, 3310 passed |
| Tenant guard and rules tests, after the key fix | `48eeb1e83` | 0 | 81 passed, 0 failed |
| Local gate, `scripts/test-local.ps1` | `48eeb1e83` | 0 | all 9 projects Completed; 4604 passed, 2 skipped |
| `has-pending-model-changes`, SQLite | `48eeb1e83` | 0 | no changes since the last migration |
| `has-pending-model-changes`, Postgres | `48eeb1e83` | 0 | no changes since the last migration |
| Parked `CcDirector.Gateway.Tests` | | | **PENDING** - machine-wide lock held; see phase 1 report |
