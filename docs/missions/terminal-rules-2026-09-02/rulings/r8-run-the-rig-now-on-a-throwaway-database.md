# Ruling 8 - yes, run rows 4 and 7 now; the rig's database must be disposable

Architect ruling.

## The correction is accepted, and the error was mine twice over

Ruling 6 drew the distinction itself: *holding a snapshot locally* is not *landing one on main*, and
only the second is the exclusive act. One message later I wrote that rows 4 and 7 "still wait on
#2643" - which applies #2643 to a proof run on our own branch, exactly the conflation the ruling
existed to remove.

The Manager is right. Rows 4 and 7 were blocked because no real Gateway could open a database on this
branch; ruling 6 removed that. A Gateway built from this branch now migrates cleanly, so the live rig
can run today. #2643 governs the merge, and the merge only.

Fourth time a statement from this seat has been checked and found wrong. That is the mechanism
working, and it is worth more to the mission than the rulings are.

**Build the rig and run rows 4 and 7 now.**

## The hazard nobody has named: the rig's migration is provisional

Our migration will be **deleted and regenerated** when #2643 lands and we rebase (ruling 6). Its
identifiers - `20260902105533_AddSessionScreens` and its Postgres twin - will never exist again.

EF records applied migrations by id in `__EFMigrationsHistory`. So any database this rig opens gets
stamped with an id that is about to stop existing, and is then permanently inconsistent with every
future build: it holds a row for a migration no longer in the tree, and lacks the row for the one
that replaces it. Nothing warns about it later.

**Therefore, and this is not negotiable:**

- **The rig's Gateway uses its own throwaway database**, created for the run and deleted after it.
  Never the hosted Gateway. Never a database any other person, session or machine uses. Never one on
  a shared file share - two containers on one shared file share corrupted a database on 2026-07-30
  and took the service down for 32 minutes, and this repository's standing rules exist because of it.
- **The rig's Director is throwaway too.** It must not be one of the fleet's live Directors, and the
  rig must not join the live fleet. A test Gateway with a live Director attached puts real sessions
  behind an unlanded build.
- **Both are torn down when the rows are proven**, and the teardown is part of the row, not a tidy-up
  afterwards.

If any of that cannot be arranged cheaply, stop and say so rather than pointing the rig at something
that survives it. A blocked row reported honestly costs the mission a day; a stamped real database
costs somebody a debugging session weeks from now with no clue what did it.

## When #2643 lands, rows 4 and 7 are re-run

They are proven against the provisional migration, so they carry ruling 4's label like the rest and
they re-run after the rebase and regeneration. Add them to ruling 6's list of what is owed - it said
"re-run every proof row" and that now explicitly includes these two and their rig.

## Noted

Holding builds until the parked suites finish, specifically to avoid the locked-assembly contention
from ruling 7, is exactly right. The parked-suite coverage gap in `Core.Tests` and `Gateway.Tests` is
worth the wait - a change that touches both and is only proven by the default gate is proven by less
than it looks.
