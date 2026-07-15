# Mission: Session States

**Status:** ACTIVE. Opened 15 July 2026.
**Architect:** the session named `Session States - Architect`.
**How this mission is conducted:** [`.claude/skills/mission/SKILL.md`](../../.claude/skills/mission/SKILL.md) - the roles and the four laws. This document does not restate them and grants nothing.
**The specification:** [`docs/new_architecture/session-state.html`](../new_architecture/session-state.html) - the only document that defines session state. Read it before writing a line.
**The first mission's record:** [`mission-session-state-truth.md`](mission-session-state-truth.md) - the original run, which produced the work this mission is landing. Historical.

---

## THE WHY

**DevThrottle lied about what your sessions were doing, and it lied plausibly.**

The owner watched a session render a calm grey dot - the one that means "parked" - while it was
twenty-three minutes and fifty-six thousand tokens into real work. Nothing looked broken. That is the
whole problem: a wrong answer gets caught in a day, and an answer that looks ordinary does not.

He looks at those dots to decide where to spend his attention. A dot that says "parked" about a
working session is not a cosmetic bug. It is the product giving him bad information about his own
fleet, in the one place he trusts it.

**THE OBJECTIVE - not yet achieved, and stated as an aim rather than an accomplishment:** every
screen tells him the truth about every session, and the next agent cannot quietly restore a lie,
because the tests and the specification both defend the law.

**Today it is partly true.** The eight lies below are fixed and merged. Four named gaps remain open
(see "Still open"), and until they close, there are still places where a screen can disagree with
another. **The owner has ruled that the mission does not end until they close** - so the objective
above is what this mission is measured against, not a description of where it has got to. Do not
quote it as though it were the current state; that is the exact failure this mission exists to end.

---

## THE LAW

> **If a session is working, it is BLUE. Always. Nothing outranks working.**

**The ruling we build toward - not a description of the code today:** the Gateway is to own every
state and be the ONLY thing that picks a colour; the Director is to report facts; clients are to
render and decide nothing.

Every sentence of that carries its own qualifier on purpose. An earlier draft stated it in the
present tense and put the caveat in the paragraph below - and a paragraph below is not where a
retrieved fragment brings its reader.

**It is NOT true today.** Two clients still decide things: `MainWindow.axaml.cs` stamps the role badge from the
Director's own `ResolveLocalRole`, and `FifoWindow.axaml.cs` filters on the raw `StatusColor`. Both
are on `main` right now, both carry a comment saying so, and both are in "Still open" below.

Read it as the rule you build toward and measure against, never as a claim about what is already
true. A law written in the present tense reads like a finished fact, and this whole mission is a
story about true-sounding sentences that were not.

Do not re-litigate the rule itself.

---

## Where this mission came from, and why it exists separately

The first mission - "Session State Truth", 14-15 July - did the work: it found the defects, fixed
them, and wrote a QA report. Then **the machine crashed**, and fourteen finished commits were
stranded on one disk, never pushed. They sat there, complete and passing, one disk failure from
gone.

The owner declined to merge fourteen commits he could not read, and he was right to. **This mission
is landing that work** - in six slices, each small enough to review, each independently inspected -
and then closing what the first mission left open.

The unsliced original is preserved on `backup/session-state-truth-2026-07-15`.

---

## What was actually wrong

Every one of these was a place the product said something false about a session.

| What you saw | What happens now |
|---|---|
| A sub-agent doing real work showed a grey "Sub-agent" dot | **Blue "Working"** - nothing outranks working |
| A snoozed session woke up and stayed grey and buried | **Blue "Working"**, back at the top |
| You snoozed a working session; the snooze **never expired** | The clock starts **when the work ends**, as asked |
| A snoozed session that exited still read "Snoozed" forever | **"Exited"** - a dead session never hides behind a snooze |
| A dictation that never finished wedged the dot orange - one stood **90 minutes** about an upload that was not uploading | Orange requires actual progress. The audio is retained and still delivered |
| A crashed session looked exactly like one that finished cleanly | **Dark red "Crashed"**, distinct from grey "Exited" |
| A session flagged for deletion got painted grey by the wrong component | The dot tells the truth about the **work**; deletion rides as a **badge** |
| The desktop rail and the phone disagreed about the same session | The rail, the phone and the Cockpit now share **one fold**. The FIFO queue window still does not - see "Still open" |

---

## The slices

| # | What it lands | Pull request | Status |
|---|---|---|---|
| 1 | The specification corrections (documentation only) | [#1584](https://github.com/thefrederiksen/devthrottle/pull/1584) | **Merged** |
| 2 | The snooze that never ends; the dead session hiding behind it (defects 12, 20, 21, 22) | [#1585](https://github.com/thefrederiksen/devthrottle/pull/1585) | **Merged** |
| 3 | The orange dot that lied about the phone (defect 19) | [#1588](https://github.com/thefrederiksen/devthrottle/pull/1588) | **Merged** |
| 4 | Deletion is a badge, never a colour (defect 23) | [#1596](https://github.com/thefrederiksen/devthrottle/pull/1596) | **Merged** |
| 5 | One fold everywhere; the desktop stops guessing (defect 5) | [#1598](https://github.com/thefrederiksen/devthrottle/pull/1598) | **Merged** |
| 6 | The agreement check and the QA report | [#1606](https://github.com/thefrederiksen/devthrottle/pull/1606) | **Inspection** |
| 7 | The four gaps below - the mission does not end until they close | - | **Manager seated** |

Slices are cut from `origin/main` in build order and cherry-picked - the fourteen commits are a
stacked chain (three touch `SessionDto.cs`, three touch `GatewayHost.cs`), so they cannot be
reordered without conflicts.

---

## What the inspection found, and why it is the point

Every slice was inspected by an independent **Codex** agent before it merged. It was not decoration.

**On slice 5 alone it found seven real defects across five passes. Not one was a false alarm.** The
review-driven commits on [#1598](https://github.com/thefrederiksen/devthrottle/pull/1598) record
them, roughly one per finding; slices 1 to 4 carry the same shape. The number is checkable from the
branch history rather than something you have to take on trust - which is the point.

The one that matters most: **the mission's headline feature did not work.** The Gateway resolved a
session's role and stamped it, the fold read it correctly and suppressed a controlled worker's red -
and the desktop still showed red, because nothing told the rail to re-read. Every mapper test passed
throughout, because they read the fold, and reading is not rendering. Then the fix for that was
itself half-done - the dot repainted while the row text still said "Needs you" with a live timer -
and then the same fault turned out to exist in five more places, and finally in the gate that guards
two of them.

Smaller, and just as instructive: an old Director's silence being read as "not snoozed" would have
deleted a live snooze fifteen seconds after it was asked for - defect 12, resurrected by a default
value on the receiving DTO after the wire that killed it had been fixed.

**The author had verified the code, revert-tested every fix, and watched the tests fail on purpose.
All of it was green. All of it would have shipped.** That is the argument for the inspector, and it
is why the mission skill now makes one mandatory.

---

## Still open - the four named gaps

Left deliberately, named in the code where they live, and NOT hidden in a backlog.

1. **The desktop's role badge still resolves locally.** The colour reads the Gateway's stamp; the
   glyph reads the Director's own guess. One row can disagree with itself. Moving it needs an answer
   to "what shows before the first stamp arrives", and guessing one is how these defects were built.
2. **The FIFO queue window bypasses the shared fold.** It reads raw red, so it still queues a
   controlled worker the rail is not calling red.
3. **The Director's cooked colour is not deleted** until its last consumer is gone.
4. **The cross-Director role case under a machine filter** - a real cost and product decision.

Plus, from the first mission's own report and worth keeping visible: **the desktop still cannot see
four things the phone can** - phone dictation, server transcription, voice preparation, and a
just-expired snooze. The first three do not heal on their own. The recommendation the design already
implies is that the desktop should stop working colours out and simply **ask**, as the phone does.
That is a real piece of work and it is the owner's call.

**ANSWERED by the owner, 15 July 2026: the mission does NOT end at slice 6. It runs until these four
are closed.** A Manager is seated on them - brief at
[`session-states-gaps-manager-brief.md`](session-states-gaps-manager-brief.md), branch
`mission/session-states-gaps`.

Two of the four are settled and being built:

- **Gap 1's blocker is decided.** "What does the role badge show before the first Gateway stamp
  arrives?" - **nothing**. No badge until the Gateway says. The law is that the Director resolves
  nothing, and "no answer yet" is not a lie, whereas a local guess is.
- **Gap 3 is scoped as a question, not an order.** `Session.StatusColor` has eight live readers. If
  closing gap 2 removes the last *presentation* consumer that is progress, and the remaining
  non-presentation reads are a different question from "a client decided a colour". Deleting a field
  with live readers to make this document true would be the mission's own failure mode. If it cannot
  close here, it stays a named gap - that is a fine outcome; a false claim is not.

**Gap 4 still needs the owner, and will come back to him with a cost.** The desktop cannot see phone
dictation, server transcription, voice preparation, or a just-expired snooze, because it folds the
DIRECTOR's view and those are Gateway-side facts. The design's own recommendation is that the desktop
should stop working colours out and ask, as the phone does. That is an architectural change, not a
bug fix, so it is his call. The Manager investigates and sizes it; it does not build it.

---

## Out of scope

- Re-opening the law, or any ruling in the specification's section 7.
- Any new colour, state, or surface.
- Deleting the Director's cooked `StatusColor` while anything still reads it.
