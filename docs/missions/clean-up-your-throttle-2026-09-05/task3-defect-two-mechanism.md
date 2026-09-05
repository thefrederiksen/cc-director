# Phase two, task 3 - the mechanism behind defect two, proven

**What this is.** Ruling R11 says no fix for defect two lands on a hypothesis: find the cause first. This
is the cause, established from the store's own records and the writer's own source. **It changes no code**,
and it ends with a question that is the Architect's, not this Manager's.

**Written:** 2026-09-05. Scripts: `evidence/mechanism.py`, `mech2.py` to `mech8.py`, all re-runnable.

---

## The finding in one paragraph

A delta appended to `stat_delta` is the difference between what the high-water row held before the write
and what it holds after. There are exactly **two** ways the whole standing cumulative can be appended in
one row, and the store records which one happened. **Route B - the row was LOWERED** and the drop was read
as the session starting over: the `generation` column counts these, and it matches the turn-carrying
restatements one for one on every bucket that has ever taken one. **Route A - the row was ABSENT** and was
inserted fresh, so the whole tally counted as new: 240 buckets restated while still on generation zero,
which Route B cannot produce. Both are the same fault in different clothes: **the aggregator concludes
"this is all new activity" from the ABSENCE of a prior record rather than from anything saying the session
started over.** Route A is the larger of the two.

---

## How a whole cumulative reaches the ledger - the two, and only two, ways

`GatewayStatsWriter.Grow` turns a raise into an appended delta:

```csharp
private static Raised Grow(long stored, long previous) =>
    new(stored, stored >= previous ? stored - previous : stored);
```

and the raise statement sets `previous_<metric>` to the value the row held, on every update - seeding it
to **zero** on an insert. So:

- **Route A - a fresh INSERT.** No row existed. `previous` is zero, so the growth is the whole reported
  cumulative. The row starts at **generation 0**.
- **Route B - the row is LOWERED.** The only branch that lowers a metric is the one the statement calls
  adopting a reset: `excluded.<metric> < stored AND believed >= stored`. Then `stored < previous`, so the
  growth is the whole of the new value - and the same condition raises **generation by one**.

Every other branch leaves the row where it is and appends nothing. **So a restatement at generation 0 is a
fresh insert, and a restatement on a bucket whose generation has moved is an adopted reset.** The store
counts its own resets, and that is what makes this decidable rather than arguable.

---

## Route B, measured: the generation column matches the restatements one for one

Over the 497 buckets of this tenant whose high-water row still exists, counting only restatement rows that
carry at least one TURN:

| generation | turn-carrying restatements | buckets |
|---:|---:|---:|
| 1 | 1 | 34 |
| 2 | 2 | 9 |
| 3 | 3 | 3 |
| 4 | 4 | 5 |
| 5 | 5 | 3 |
| 7 | 7 | 1 |
| 8 | 8 | 1 |
| 10 | 10 | 1 |
| 11 | 11 | 1 |

One adopted reset, one restatement, every time. The remaining generation-bearing buckets (18) show fewer
restatements than resets and never more, which is the expected direction: a reset adopted on the CHARACTER
metric alone raises the generation without restating any turns.

**Route B accounts for 77 buckets.** It is real, it is proven by the store's own counter, and it is the
minority.

## Route A, measured: 240 buckets restated while still on generation 0

| generation | turn-carrying restatements | buckets |
|---:|---:|---:|
| 0 | 0 | 180 |
| 0 | 1 | 143 |
| 0 | 2 | 40 |
| 0 | 3 | 20 |
| 0 | 4 | 10 |
| 0 | 5 | 7 |
| 0 | 6 | 14 |
| 0 | 7 to 26 | 6 |

Generation is raised by exactly one branch and is never lowered, so a bucket that has restated 26 times
while sitting on generation 0 has had its high-water row inserted afresh 26 times. **The row was absent, 26
times, for a session that was still counting.**

Two supporting measurements, both consistent:

- Over the whole history of the week's sessions, **277 of 342 restatement bursts re-add EXACTLY the
  standing cumulative** - no more and no less - which is the arithmetic signature of a baseline of zero.
- The re-add usually arrives as a PAIR: a first row carrying the cumulative as at some earlier point, then
  a second carrying the remainder. That is two folds after the baseline was lost - one from a slightly
  stale roster reading and one from the current one - and it is exactly the shape of phase one's worked
  example (row 8185 restating the total as at 8183, row 8186 repeating 8184).

## What removes a high-water row

One method. `GatewayInputStatsAggregator.Forget` is the only code in the repository that deletes a
`session_highwater` row (`DeleteSessionHighWater` has a single caller), and `Forget` itself has a single
caller: `DirectorHub.RemoveSession`. Its own documentation states the assumption it rests on - that a
session which ends leaves its contribution in the totals and its high-water entry is dropped - and that
assumption is safe only if the session never counts again afterwards.

**A hole found while reading it, real in code and NOT fired on the day examined.** `Forget` runs there even
when the store REJECTED the removal: `ApplyRemove` returns false for a superseded connection or a stale
sequence, and the session stays in the roster and keeps counting, but its counting baseline is deleted
anyway. The `accepted` flag gates the work-history observer two lines below and does not gate this. The
hosted Gateway's log for 2026-09-04 carries 78 dropped pushes and **every one of them is a snapshot; not
one is a remove**, so this hole did not fire that day. It is still wrong, and it is one line.

---

## What this account does NOT establish

- **Why a session that is still counting has its high-water row removed, between one and twenty-six
  times.** `Forget` is the only thing that removes one, and the only path to `Forget` is a removal the
  Director sent for a session it had genuinely dropped. Nothing in the record says how that session then
  came back and kept counting. Answering it needs either a log line that does not exist today or an
  instrumented run; it cannot be read out of the stored rows.
- **Which route each of the week's 2,061 inflated turns took.** The generation column can only be read for
  a bucket whose row still exists, and 182 of the week's 250 buckets have been forgotten since.
- Phase one's walk classified some **character-only rows (zero turns)** as restatements, because the pair
  (0, n) had been reached before. They carry no turns and change none of its turn arithmetic, but they
  inflate the ROW counts it published. The turn figures stand; the row counts are generous.

---

## The question this raises, which is the Architect's

Both routes are one fault: **"I have no prior record of this, therefore all of it is new."** Absence is
being read as a positive statement about the session's history, and the penalty for reading it wrong is
the session's entire tally counted a second time, permanently, because nothing rewrites an appended delta.
The writer's own comment already makes this argument about Route B in the cross-incarnation case and
closed that one with the generation column. Route A is the same argument with the record missing rather
than lower, and it is not closed.

A sound fix means the Director stating positively WHICH incarnation of a tally it is reporting, so that
neither a missing row nor a lower number is ever enough on its own to conclude a reset. That is a change to
the wire contract between the Director and the Gateway.

**And that is the shape question R9 holds open.** Phase one's carried-forward finding is that Your Throttle
keeps a SECOND cumulative tally beside the submission ledger, and defect two is the cost of reconciling
that second tally by inference. The ledger has none of this: it is append-only, idempotent on replay, and
phase one reconciled it against the reconstructed store to within one turn. **If phase three derives Your
Throttle's figure from the submission ledger, defect two does not need fixing - it stops existing.**
Building an incarnation token now would be repairing a counter the design may be about to delete.

**Recommendation to the Architect:** land the one-line containment now - do not forget a high-water row
when the store rejected the removal - and hold the real fix until R9 is settled, because the two answers
are the same decision. This Manager is not settling it.
