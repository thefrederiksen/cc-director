# Ruling 13 - resolution 2 deletes the mechanism three proof rows measured

Architect ruling. Approves the fix-round plan, corrects ruling 12, and closes the gap the plan does
not cover.

## The plan is approved, and it improves on ruling 12 in two places

**Finding 1's argument is better than the one I gave.** Ruling 12 justified resolution 2 as "an
optimisation that cannot be made sound is dropped". The plan's argument is structural and stronger:

> The store could only ever answer a live question while fact 2 held - the owning Director's tunnel
> is CONNECTED at that instant. A connected tunnel is exactly the condition under which the tunnel
> could have answered the question itself. So the live half never bought availability - not once, by
> construction. It bought latency, on a connection that was already up.

That is the whole case in three sentences, and it does not depend on a measurement.

**And it corrects ruling 12, which was too generous.** Ruling 12 offered resolution 1 as an equal
option available on a measurement. The plan shows it never was:

> a coalesced push gives "the terminal has not moved in the last X", never "the terminal has not
> moved". A keystroke can follow this answer.

No measurement could have made that sound, because the defect is in what the signal can mean, not in
how often it arrives. **Resolution 1 is withdrawn as an option**, here and for any later phase that
is tempted to revive the live-store path. If someone wants it back, they are proposing that a
keystroke may follow a screen that was current *recently*, and they must argue for that in those
words.

Step 0 was also done correctly: the stale state was captured as evidence BEFORE the database was
dropped, and the migration was not renamed to match it.

## The gap: five rows measured a mechanism that is being deleted

The plan does not mention the phase 0 acceptance rows, and resolution 2 invalidates three of them
outright. Left alone, the phase 0 report would carry rows that are false or vacuous - and a vacuous
row that passes is precisely the class this mission has spent the day removing.

**Row 7 - "a voice turn completes with no tunnel screen read" - is now FALSE BY DESIGN.** Under
resolution 2 a voice turn's live read always tunnels. It cannot be re-run and it must not be quietly
re-scoped. **Withdraw it**, in the proof plan and in the report, with one line saying resolution 2
removed the behaviour it asserted. A withdrawn row that says why is worth more than a surviving row
nobody can interpret.

**Row 6 - "a frozen push stream does not certify a stale screen" - becomes VACUOUS.** There is no
certification left to defeat, so the row would pass on the absence of the mechanism. Replace it with
the finding-1 test, which is strictly stronger: every one of the three old facts satisfied, and the
reader must still tunnel.

**Row 5 - the live/history split - is restated, not withdrawn.** It is still the right question and
its answer is now simpler: a live read NEVER returns a stored screen, while `ReadStored` still
answers from the store in the same run. Keep both halves in one test so it cannot pass on a reader
that answers nothing.

**Rows 0 to 4 survive unchanged.** Capture, round-trip integrity, retention, tenant scoping and the
offline history read are all about the half that stands.

## The pull counter changes purpose - keep it, and say so

`ScreenGridPulls` existed to prove the store SAVED tunnel round trips. Under resolution 2 it proves
close to the opposite, and that is useful: it becomes the instrument that shows a live read really
does reach the tunnel, so a future change that quietly reintroduces store-answered live reads makes
it move the wrong way. Keep the counter, restate what it is for in its own comment, and do not delete
it as a leftover of the removed optimisation.

## What the report owes

The phase 0 report is **rewritten, not amended**. Its headline paragraphs describe the three-fact
certification as the design; that mechanism is gone, and a reader who meets a corrected report with
the old architecture still in its opening sections learns the wrong thing about the system. The
inspection, this ruling and the plan stay as the record of how it changed - the history is kept in
the rulings, not smuggled into a document that is supposed to describe what exists.

Say plainly in it what phase 0 now delivers: a session's turn-end screen, stored per account for
seven days, readable from anywhere including while the owning machine is offline. That was always
the half the mission was for.
