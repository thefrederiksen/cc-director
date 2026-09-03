# Phases 3 and 4 - the conversation loop, and Pro gating

Both are OFF the critical path to the deliverable (see the critical path table in `handoff.md`). They
are real work and they are last. Do not start either until fix round D is closed and phases 0, 1 and 2
have landed.

---

# Phase 3 - the page agent

**Fix round D shrank this phase, and the report must say so.** Ruling D2 moved the screen read onto the
Gateway: the draft route now takes a session id and reads that session's terminal itself, because
leaving the screen in the caller's hands made the grounding check unenforceable. That was the larger
half of Phase 3 as the plan scoped it.

**What is left is the conversation loop**: on the page, the authoring call becomes a tool-use loop that
can LIST the account's sessions and pick one, so a person can say "set up a rule for conductor.build"
and the agent works out which session that is, reads it, shows the screen, and proposes - rather than
the person choosing a session from a control first.

The acceptance row from the plan still applies, minus the part D2 already delivered:

| Row | What it takes to pass |
| --- | --- |
| From "set up a rule for conductor.build", the agent finds that session and reads it | On the page, against a real Gateway |
| The screen it read is SHOWN before the proposal | The person must see what the words were taken from |
| The proposed rule's words are on that screen | Already structural after D2 - show it holding, do not re-implement it |
| A question back is a first-class outcome | Already built; prove it survives the loop |
| Nothing stored without confirmation, nothing armed without promotion | Already built; prove both still hold through the loop |

**The trap in a tool-use loop**, and it is the reason this phase is last: a loop that can call tools can
call them repeatedly, and an authoring call that lists sessions in a cycle is a bill with no ceiling.
Bound the number of tool calls per authoring turn, refuse past it with a stated reason, and test the
bound. A loop without a bound is not a feature that occasionally costs a lot; it is a defect that has
not happened yet.

**Session listing is scoped to the account.** The loop may only ever see sessions inside the caller's
own tenant, and that must be proven by a test that passes two tenants and asserts at the far side - the
same standard ruling D9 set for the draft route, for the same reason.

---

# Phase 4 - Pro gating

**Gate rule CREATION. Do NOT gate running.**

| Row | What it takes to pass |
| --- | --- |
| A free account cannot draft or store a rule, and is TOLD WHY | Not a bare refusal - the sentence has to say it is a Pro capability |
| A free account's already-live rules still fire and still record | This is the row that matters |
| The boundary is enforced on the Gateway, not the client | A client-side gate is a suggestion |

**Why running is not gated, in the owner's own terms: a rule that silently stopped is a trust
failure.** Somebody who wrote a rule while on Pro and then let it lapse has a standing instruction they
believe is watching their sessions. Turning it off without saying so is worse than never having offered
it. Running one costs 0.4 seconds of a cheap model after phase 1 - the cost was never the reason.

**Test the lapse explicitly**, not just the two steady states: an account that HAD Pro, wrote a live
rule, and no longer has Pro. That account can no longer create, and its existing rule still fires. An
absence-shaped check will not do here - prove the firing by the PRESENCE of its record.

The free tier already exists on main (pull request 2664, "a free tier, so an expiring trial downgrades
instead of cutting off"), so read how that change decides what a free account may do and follow it
rather than inventing a second way of asking the same question. Two independent answers to "is this
account Pro" is the shape of defect this codebase repeatedly names.

---

## The gate for both

- `.\scripts\test-local.ps1` green, and `-Gateway` if you touch the routes. The Postgres proof rig must
  be up or the run is red for reasons that have nothing to do with you - container `cc-pg-test` on port
  55432 was up on 2026-09-03.
- Every web workspace and `npm run typecheck` for phase 3, which is a page change.
- Watch every new test fail first, with the command output and the broken commit quoted.
- ASCII only. No mention of any assistant, model, vendor or AI tool in a commit message, a document or
  a comment.

## How to finish

Push on your phase branch and report to the Architect in ONE SINGLE LINE - fleet messages truncate at
the first newline. Write the detail to a phase report in this folder and name it in your one line. Do
not open a pull request and do not merge; only the Architect lands work on main.
