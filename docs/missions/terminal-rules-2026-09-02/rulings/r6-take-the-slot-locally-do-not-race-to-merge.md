# Ruling 6 - take the slot locally, do NOT race to merge; ruling 2's sequencing is withdrawn

Architect ruling. Supersedes ruling 2's "migration last" and amends ruling 4.

## Verified here, independently

- `dotnet ef migrations has-pending-model-changes --context GatewayDbContext` answers **"Changes have
  been made to the model since the last migration. Add a new migration."**
- The diff against `origin/main` adds exactly `SessionScreenEntity.cs`, one
  `DbSet<SessionScreenEntity> SessionScreens`, and **zero** migration files.

The report is accurate in every particular. The escalation was correct.

## Ruling 2's sequencing was wrong, and why it was wrong matters

Ruling 2 said the code was independent of the migration and told the Manager to do the migration
last. That is withdrawn.

**Independence was my assumption, not a checked fact.** EF's `PendingModelChangesWarning` makes
mapping an entity and migrating it a single atomic act: the moment `SessionScreens` is registered,
every `GatewayDbContext` open throws until a migration exists. There is no half-state, which the
Manager established the right way - by trying to remove the `DbSet` and finding `SessionScreenStore`
cannot compile without it.

That is the third premise of mine this mission has checked and found wanting. Keep doing it.

## Neither of the two offered options. Do this instead

The two options were: take the slot and merge promptly, or revert and park. Take neither as stated.

**Create the migration now, on this branch, and do NOT open a pull request to `main` until #2643 has
landed.**

The distinction ruling 2 missed is between *holding a snapshot locally* and *landing one on main*.
Only the second is the exclusive act. A migration sitting on our own branch takes nothing from
anybody: #2643 neither knows nor cares what our branch contains, and its own path to main is
completely unaffected.

- **It unblocks us now.** The warning clears, the 799 failures go, and rows 1, 2, 3, 5 and 6 can run.
- **It imposes nothing on #2643**, which is further along than we are and was there first. We came
  second to a slot that was already held, so the cost of being second is ours to carry. Merging ahead
  of it to avoid that cost would be taking the slot by speed, which is worse than taking it by
  silence - the thing ruling 2 refused.
- **The cost is bounded and known.** When #2643 lands, rebase onto `main`, delete our migration,
  regenerate it on the new snapshot, re-run. That is minutes, and it is the ordinary price of second
  place.

Parking is rejected: it voids rows 1, 2, 3, 5 and 6 - because `EnsureCreated` builds from the same
model - and stops the mission for an unknown period, to buy a collision cost we can pay in minutes.

## The regeneration is verified, never assumed

When #2643 lands, all of this is owed before anything is called finished:

1. Re-run the **corrected** sweep from r2's amendment and say what it returned.
2. Rebase, delete the migration, regenerate, and re-run
   `has-pending-model-changes` until it reports **no** pending changes. That is now a real check with
   teeth, not a formality.
3. Re-run the full local gate green, and re-run every proof row - the five are proven against a
   migration that will have been replaced.

## The guard is better than the one I asked for

Ruling 4 scheduled a pending-model-changes check for when the slot frees. It turns out that check is
**already running, on every database open, continuously** - and it is what caught this. That is
strictly better than a one-time gate at the end, and it is why the refusal to suppress the warning is
the most important decision in this exchange.

**The warning is never suppressed, in this mission or after it.** Suppressing it would disable the
one mechanism that makes ruling 4's guarantee real, and it would do so silently - a model and a
migration drifting apart with nothing left to notice. If a future phase finds the warning
inconvenient, that is the guard working.

## Noted, not chased

`Core.Tests` being parked holds the existing `TurnReviewLogger` tests, which therefore do not run in
the default gate. Recorded here so it is not lost. It is not this mission's to fix, and the mission
does not stop to fix it.
