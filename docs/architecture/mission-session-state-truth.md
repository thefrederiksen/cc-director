# Mission: Session State Truth

**Status:** COMPLETE. Ran 14-15 July 2026. **This is a historical record, not a live brief.**
The mission's work is landing on main in six reviewed slices; its unsliced original is preserved on the
`backup/session-state-truth-2026-07-15` branch. The mission worktree and its branch are gone - do not look
for them, and do not treat anything below as a live instruction.
**Architect:** owned this document, sequenced the phases, killed each Manager.
**The specification:** [`docs/new_architecture/session-state.html`](../new_architecture/session-state.html) - **read it before writing a single line.** It is the only document that defines session state.

---

## THE WHY

**DevThrottle lies about what your sessions are doing, and every agent sent to fix it reads a document that tells it to lie differently.**

The owner watched a session render grey "Sub-agent" while it was 23 minutes and 56,000 tokens into real work. That was not carelessness - it was the written specification, faithfully implemented. The spec ranked ownership above activity, so six different facts could erase the one fact that matters. Worse, **the tests asserted the defect**: an agent that fixed the code watched them fail and reverted. The bug had a written constitution and an army defending it.

The spec is now fixed and merged. The law is one sentence: **if a session is working, it is BLUE - always, nothing outranks it.**

This mission makes the *rest* of the code tell the truth too. Every item below is a place where DevThrottle currently says something false about a session: a snooze that never expires, an orange dot about a dictation that is no longer making progress, a dead session hiding behind "Snoozed", a crashed session that looks like a clean exit.

**When this mission is done, every screen tells you the truth about every session, and no future agent can quietly restore a lie - because the spec and the tests both defend the law.**

---

## THE RULES OF THIS MISSION

> **RULES 1 TO 4 ARE EXPIRED. They are recorded here only so this mission's conduct can be audited.**
> They were granted to THIS mission, on ITS branch, for ITS duration, and that duration ended on
> 15 July 2026. **They grant you nothing.** If you are an agent reading this: you do NOT have standing
> commit authority, and the repository's ordinary rule is unchanged and in force - *never commit without
> being asked, and being granted it once does not grant it again.* Rules 5 to 9 below are the durable
> ones; they are how this mission was expected to think, and they still read true.

1. ~~**The owner is NOT interrupted until the final QA report.**~~ **EXPIRED.** Not per phase. Not per pull request. Not to confirm the plan. He said it plainly: *"I am not bothered until we have the final QA report that's the outcome for the mission... otherwise these missions are stupid."* This applied to this mission only. It does NOT license a future agent to work unsupervised.
2. ~~**You have standing commit authority for the whole mission.**~~ **EXPIRED - THIS GRANT IS DEAD.** It permitted committing freely on `mission/session-state-truth`, a branch that no longer exists. It was an explicit, deliberate, *time-boxed* override of the usual "never commit without being asked" rule, and the box has closed. Do not read this as permission.
3. ~~**The owner approves the FINAL PUSH only.**~~ **EXPIRED.** One approval, at the end, on the QA report - for that mission. The slices landing this work on main are each approved individually.
4. ~~**ONE worktree for the whole mission.**~~ **EXPIRED.** That worktree is gone.
5. **Every product decision is already made.** They are recorded in section 7 of the spec ("Answered - these are rulings, not proposals"). **Do not re-open them. Do not ask about them.** If you hit a question the spec does not answer, decide it the way the rulings point and write down what you decided.
6. **Token cost is not a constraint.** The owner: *"I don't care about the tokens, I care about doing it right."* Do not cut scope to save tokens.
7. **A green test is not proof.** Every fold in this codebase passes its own tests today and agrees with nothing else. The proof that matters is in the spec's section 6: read the live fleet and assert every session's desktop answer equals its phone answer equals its Cockpit answer.
8. **NEVER state a cause you have not observed.** A mechanism may be stated from reading the code, cited by file and line. A **cause** may only be stated from evidence you actually looked at - a log line, a record on disk, a reproduction. If you have not looked, write "this is undiagnosed; here is how to find out." The spec's own defect 19 was caught fabricating a cause that sounded reasonable and was never checked. A fabricated cause reads exactly like a finding, survives review, and sends the next agent to fix a world that does not exist. It is worse than the bug.
9. **Fix the lying comments in the same pass as the code.** They are not cosmetic - the spec's section 4 shows they are the delivery mechanism for the next regression.

---

## THE LAW (never to be re-litigated)

> **If a session is working, it is BLUE. Always. Nothing outranks working. No exceptions.**

The Gateway owns every state and is the ONLY thing that picks a colour. The Director reports facts. Clients render and decide nothing.

**Before you change any rule, grep the tests for the OLD rule.** Three tests defended the last defect. If a test asserts the thing you are fixing, the test is the bug - rewrite it and say so in the comment.

---

## THE OWNER'S RULINGS (already decided - implement these, do not ask)

| Question | THE RULING |
|---|---|
| Snooze a working session for 12 hours - when does the clock start? | **When the work ENDS.** Not when you ask. |
| A snoozed session exits - "Snoozed" or "Exited"? | **Exited.** A dead session never hides behind a Snoozed label. |
| Pending deletion - colour or badge? | **A badge, never a colour.** If it is still working it is blue, with a badge. |
| A dictation that never reaches a terminal state - stay orange, or bound the colour? | **Bound the colour, keep the record.** Nothing is lost except the lie on the dot. |
| A Director dies while a snooze is deferred? | **Persist the hold state; land the deferral on restart if the session is not working.** *(INFERRED from the rulings above, not answered word-for-word by the owner. Treat it as the way the rulings point, not as a quote.)* |

Plus the snooze rules themselves: working ALWAYS clears a snooze; an expired snooze is gone (Gateway-owned, not the Director); snooze-while-working means "snooze me when the work ends"; **nothing else** clears a snooze. The Director implements the parts it owns, with two known exceptions this mission fixes: a landed hold is not cleared on exit, and hold state does not survive a restart.

---

## THE PHASES

Run in order. Each phase gets a **fresh Manager** (reset per phase). The Architect kills each Manager when its phase lands.

### Phase 1 - The wire tells the truth about hold
**Defects 12, 20, 21, 22. These are ONE change and land together.**

The root cause is a lossy wire: three hold states (`None` / `Held` / `DeferredHold`) are squeezed into one boolean, so nothing downstream can tell "not held" from "about to be held".

- Carry the hold state on the wire instead of the boolean. `HoldResponse.Pending` already carries the missing bit and nothing reads it.
- **Defect 20 - the headline bug:** an agent-requested snooze never expires. A deferral reports `OnHold=false`; the sweep runs every 15 seconds, reads that, concludes the snooze is over, and deletes the 12-hour timer. Start the Gateway clock when the deferral **lands**; never clear a timer merely because `OnHold` reads false.
- **Defect 21:** clear a landed `Held` on exit, so a dead session reads Exited.
- **Defect 22:** persist hold state so a restart does not forget every snooze.

*This phase is intricate and cross-cutting. Prefer ONE strong worker over a fan-out.*

### Phase 2 - The orange tells the truth
**Defect 19 - the wedged orange.**

**FIRST: DIAGNOSE IT. Do not reason about the cause - go and look.** The durable records are on disk on
the Gateway. Enumerate the `Pending` ones, read their age and session id, and check the Gateway log for the
outcome returned when each completed. Report what you actually find.

An earlier draft of the spec asserted this was "almost certainly" caused by running out of credits. **That
was fabricated** - nobody had run out of credits, and no record or log had been read. It has been removed.
Do not restore it, and do not replace it with a different guess. **The mechanism is known and cited; the
cause is UNDIAGNOSED until you have looked at a real wedged record.**

Then fix it:

- Bound the colour without touching the durable record. Paint "Uploading from phone" only while the upload is actually making progress (the same idle rule `TranscribingSessions` already uses). **This fix stands regardless of which path turns out to cause it** - it bounds the colour for every cause at once.
- Park every non-terminal path in a terminal state (`GatewayDictationEndpoint.cs:277-290` - today `OutOfCredits` and retryable errors return without marking the record, so it stays `Pending` and paints forever; only `PermanentError` parks it).
- Fix the two comments claiming the pending-derived status "never wedges". They are why nobody believed this bug was real.

### Phase 3 - Deletion is a badge
**Defect 23.** Make `PendingDeletion` a badge, delete the Director's `SetStatusColor` call in `MarkForDeletion`. The Director does not write colours.

### Phase 4 - One fold, everywhere
**Defects 2, 3, 4, 5, 6, 7, 8, 9, 11, 13, 14, 15, 16, 17, 18, plus the traps.**

The broad cleanup: delete the six other desktop folds and the client label folds, restore the crashed colour, push `SessionRole` to the desktop, separate the greys, fix every lying comment listed in the spec's section 4.

*This phase is broad and genuinely parallel - this is where a Manager and workers earn their keep.*

### Phase 5 - QA, and the proof that matters
- The cross-surface agreement check from the spec's section 6: read the live fleet, assert desktop == phone == Cockpit for every session. It reported six disagreements out of thirteen when it was written. **It must report zero.**
- Walk every scenario in the spec's "Every scenario, walked" table and confirm the "Now" column is true.
- Produce the **final QA report** - this is the mission's outcome and the ONLY thing that goes back to the owner.

---

## DONE MEANS

- Every defect above is fixed, or explicitly recorded in the spec as deliberately deferred with a reason.
- The cross-surface agreement check reports **zero** disagreements.
- **The spec is updated in the same pull request as the code.** A change that lands in one and not the other is the whole failure mode this mission exists to end.
- The final QA report is written, with proof - not assertions.
- The owner is asked, **once**, to approve the final push.
