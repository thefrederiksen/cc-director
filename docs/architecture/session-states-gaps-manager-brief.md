# Manager brief - Session States, the four gaps

**Mission:** Session States. Read [`mission-session-states.md`](mission-session-states.md) for the WHY.
**Architect:** the session named `Session States - Architect`. Report to it, not to the owner.
**How you conduct yourself:** [`.claude/skills/mission/SKILL.md`](../../.claude/skills/mission/SKILL.md). Read it first. It is the only place the rules live and this brief does not restate them.
**Your branch:** `mission/session-states-gaps`. Your worktree: `D:\ReposFred\devthrottle-gaps`.

---

## The WHY, in one line

The owner's session list lied about what his sessions were doing. Six slices fixed eight of those
lies and are merged. **These four are what is left, and until they close, two of his screens can
still disagree about the same session.**

He has answered the question the mission record carried: **the mission does not end until these are
closed.** That is why you are seated.

---

## What is already true - do not re-litigate it

- **The law:** if a session is working, it is BLUE. Always. The Gateway is to own every state and be
  the only thing that picks a colour; the Director reports facts; clients render and decide nothing.
  That is the ruling and the target. It is **not** true today - closing these gaps is what makes it
  true.
- **The shared fold** is `SessionOrdering.EffectiveColor` / `StateLabel`, fed by
  `ControlEndpoints.Map(session)`. The desktop rail, the phone and the Cockpit already run it.
- **`SessionViewModel.RaiseFoldProjection()`** is the one place that re-reads everything the fold
  feeds. Every fold-input handler calls it and keeps no list of its own. **If you add a fold input,
  wire it there and add it to `EveryFoldInput_RaisesTheWholeProjection`.** Six private lists that all
  disagreed is what caused four of the seven defects the inspector found on slice 5.

---

## The four gaps

Each names the file. Each is real, on `main`, and carries a comment saying so.

### Gap 1 - the desktop's role badge still resolves locally

`src/CcDirector.Avalonia/MainWindow.axaml.cs:3225` - `vm.ResolvedRole = _sessionManager.ResolveLocalRole(vm.Session)`.

The colour reads the Gateway's stamp (`Session.GatewayResolvedRole`); the glyph reads the Director's
own guess. **One row can show a Gateway-resolved colour beside a Director-resolved badge, and they
can disagree.** `ResolveLocalRole` only sees this Director's sessions, so a controller on another
machine is invisible to it - the exact blind spot the down-channel exists to fix. `Session.cs:190`
already says in terms: do not assign the stamp from that resolver.

**The blocker was "what shows before the first stamp arrives?" - the Architect has decided it: show
NOTHING.** No badge until the Gateway says. That is what the law points to (the Director resolves
nothing), and "no answer yet" is not a lie, whereas a local guess is. Write the decision down where
you implement it.

If `ResolveLocalRole` has no callers left afterwards, prove that by finding its callers - not by
grepping for the name - and delete it. **A call site is not a caller.**

### Gap 2 - the FIFO queue window bypasses the shared fold

`src/CcDirector.Avalonia/FifoWindow.axaml.cs` - `BuildQueue()` filters on raw `StatusColor == "red"`
and `!OnHold`.

A second place the desktop decides state for itself. It still queues a controlled worker whose red
the fold suppresses to "supporting", and ignores every other overlay the fold applies. **The FIFO
queue can hand the owner a session the rail is not calling red.**

The fold takes a `SessionDto`, so this is a real change to the queue's shape, not a one-line swap -
that is why it was deferred, and it is why it is yours. Route it through the same
`ControlEndpoints.Map` -> `SessionOrdering` path the rail uses.

### Gap 3 - the Director's cooked colour is not deleted

`Session.StatusColor` is the Director deciding a colour, which the law forbids. It survives only
because things still read it. **Eight files do**, including `TurnReviewDialog`,
`WingmanContextBuilder` and `SessionReadExecutor`.

**Scope this honestly and report before you swing.** If closing gap 2 removes the last *presentation*
consumer, say so and say what is left. If the remaining readers are non-presentation (a wingman
prompt, a log line), that is a different question from "a client decided a colour" and may be
legitimate - decide it the way the law points and write down what you decided. **Do not delete a
field with live readers to make a document true.** If it cannot close in this mission, say that
plainly; a named gap is a fine outcome and a false claim is not.

### Gap 4 - the desktop cannot see four things the phone can

Named in the QA report and pinned by the agreement check's own tests
(`AgreementCheckFaultInjectionTests`): **phone dictation**, **server transcription**, **voice being
prepared**, and **a just-expired snooze**. On the first three the desktop reads red "Needs you" while
the phone reads orange or yellow, and **they do not heal on their own**.

The cause is structural: the desktop folds `ControlEndpoints.Map(session)`, which is the DIRECTOR's
view, and these are GATEWAY-side facts. The Director cannot know them.

The design's own recommendation is that the desktop should stop working colours out and **ask**, as
the phone does. **That is a genuine architectural change and it is the owner's call, not yours and
not mine.** Investigate it, size it, and bring the Architect a recommendation with the cost. **Do not
build it without a decision.** Sending the desktop four more facts would treat the symptom and is
exactly the wrong answer - say so if you find yourself reaching for it.

---

## What "done" means for each

- The fix and the thing that stops it regressing land together. Never split them.
- **Prove it can fail**: revert, watch it go red with the reported symptom, watch the controls stay
  green, restore. A test never watched failing is decoration.
- **Check the build error count, not just the test output.** A test run against a stale binary
  reports green from a build that did not compile. It has happened twice in this mission.
- All seven .NET projects, plus the frontend if you touch it (`npm run typecheck`, client-core and
  cockpit tests).
- Say what is NOT proven, in the code, as a gap.

## Out of scope

- The law, or any ruling in the specification's section 7.
- Any new colour, state or surface.
- Building gap 4 without the owner's decision.
