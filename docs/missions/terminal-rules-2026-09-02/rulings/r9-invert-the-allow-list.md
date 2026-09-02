# Ruling 9 - fix the collation, and invert the list that failed to catch it

Architect ruling. Fourth instance of ruling 7's class, and the first one with a documented history.

## Verified here

- `SessionScreenEntity` has no `UseCollation("C")`. Every sibling of the same shape does -
  `SessionSpendEntity`, `SessionHistoryEntity`, `SessionTurnEntity`, `SessionTurnHeadEntity`, all on
  `SessionId`.
- `Collation_ExplicitC_OnExactlyTheDeclaredNaturalKeys_OnRealPostgres` compares the live catalog
  against a hand-written `expected` array.

The defect is real and the diagnosis is right. Postgres collates by locale and SQLite by raw bytes,
so without explicit `C` the two providers disagree on uniqueness for a caller-supplied key - which
means the store's idempotency guarantee behaves one way on the hosted Gateway and another on a local
install. Fix the model.

## The check's blind spot is where its success condition is met

Set equality between a hand-kept list and the catalog gives two behaviours, not one:

- A collation **added** and not listed - the sets differ, the test goes **red**. Loud.
- A collation **missing** - the column is not in the catalog with `C`, and not in `expected` either.
  The sets still match. **Green.** Silent.

So the one failure it exists to prevent is the one it cannot see. And this is not theory: the test's
own comment records the list going stale twice, and that *"the suite was red on main from 2026-08-05
and stayed red through the v2.0.0 and v2.0.1 tags"*. A check that is loud in the harmless direction
and silent in the dangerous one gets ignored in the harmless direction - and then it is just silent.

## Do not add the entry. Invert the list.

Adding `("session_screens", "SessionId")` to `expected` fixes today and leaves the mechanism for the
next entity - the same call ruling 7 made about the hub fixture, and the reason that fixture is being
derived rather than patched.

**Derive the population from the model:** enumerate the model's string properties that participate in
a primary key or a unique index - that is what "natural key" means here, and it is a property of the
model, not of a list - and assert each one carries explicit `C` in the live catalog.

**Then invert what stays hand-written.** Today the array is an allow-list meaning *"these are checked"*,
so anything absent is unchecked. It must become a short **exception list** meaning *"every string key
column must be `C`, EXCEPT these, each with a written reason"*. A new key column with no collation and
no exception entry is then **red**, which is the direction that matters.

If deriving the set proves genuinely hard, say so and add the entry as a stopgap - but file it as a
stopgap with the derived version named as the fix, not as the fix itself.

## Consequences for the rest of the mission

- The Postgres migration is regenerated for the collation. That is fine and expected: ruling 8 already
  treats this migration as provisional. It also **strengthens** ruling 8 - the migration has now
  changed twice before landing, so a rig pointed at any surviving database would be stamped with an id
  that is already dead. Throwaway database, throwaway Director, teardown inside the row.
- Re-run `has-pending-model-changes` to no-changes after the model edit, both providers.
- Row 1 asserts field-by-field equality on read-back; add the idempotency property this collation
  exists to protect - the same session id written twice produces one row, on both providers.

## Noted, not chased

Two things recorded here because they are true and are not this mission's to fix:

1. **The parked suite was red on `main` through two release tags.** That is a release-integrity
   question about whether the parked gate is actually run, and it belongs to whoever owns the release
   gate. Raise it once, in the phase 0 report, in one line.
2. **Two missions share a machine-wide test lock.** Reading another mission's failures out of your own
   run - and checking the stack traces named the turn-push worktree rather than assuming they were
   yours - was exactly right. Report your own numbers only. If the contention makes the parked gate
   unrunnable, say so rather than reporting a run you could not isolate.
