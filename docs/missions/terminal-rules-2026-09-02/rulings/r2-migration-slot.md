# Ruling 2 - do not take the migration slot; it is held

Architect ruling. Binding.

## The request

> The EF migration slot is one-unmerged-migration fleet-wide, the turn-push migration is merged and
> no turn-push seat is currently on the roster, so unless you know otherwise I will take the slot now
> and merge promptly.

**No. The slot is held, and the reasoning that concluded it was free does not support that
conclusion.**

## What is actually true

The turn-push half is right and checks out. `20260902003414_AddSessionTurns.cs` is on `origin/main`;
that migration is merged and is not holding anything.

But it is not the only claim on the slot. Sweeping every remote branch for migrations not on main:

```
git fetch origin
for b in $(git branch -r --format='%(refname:short)' | grep -v HEAD | grep -v 'origin/main$'); do
  m=$(git diff --name-only origin/main..."$b" | grep -iE "Data/Migrations/.*\.cs$" \
      | grep -v ModelSnapshot | grep -v Designer)
  [ -n "$m" ] && { echo "BRANCH $b"; echo "$m" | sed 's/^/    /'; }
done
```

turns up the live holder:

```
BRANCH origin/feat/mobile-session-picker-repository-search
    src/CcDirector.Gateway/Data/Migrations/20260902063350_AddKnownRepositories.cs
```

`AddKnownRepositories` was created **today**, 2026-09-02 at 06:33, on the **same `GatewayDbContext`**
this phase would add `session_screens` to. It is carried by **PR #2643** ("Add searchable mobile
session picker"), which is open, marked ready, and in review right now - there is a review worktree
cut for it and live seats on that work.

Two migrations cut from the same model snapshot is precisely the collision the one-slot rule exists
to prevent: whichever merges second has a snapshot chain that no longer describes the database it
claims to migrate.

There is a second, older claim to check before anyone takes the slot: **PR #2379**
(`prompt-delete-erases`) carries three `GatewayDbContext` migrations dated 2 August. Whether those
are live or superseded is not established here - establish it before assuming the slot is free after
#2643 lands.

## The method error, which matters more than the fact

The reasoning was: *the turn-push migration is merged, and no turn-push seat is on the roster,
therefore the slot is free.* That reasons from **one workstream's absence** to a **fleet-wide fact**.
Those are different claims, and the second does not follow from the first. The slot is fleet-wide, so
only a fleet-wide sweep can answer it - the loop above, which takes seconds.

An empty roster is also weak evidence on its own. A mission between Managers has no seat and is not
finished; the workflow this mission runs under kills a Manager at every phase boundary on purpose, so
"no seat" is the NORMAL state of a live mission between phases. Do not read it as "that work is
done".

**And the shape of the ask is the same fail-open pattern as ruling 1.** "Unless you know otherwise I
will take it" makes silence into consent for an exclusive shared resource. A slot is requested and
granted, never assumed from an absence of objection. Ruling 1 was about a check that passes when
nothing arrives; this is a decision that proceeds when no one answers. Same defect, different surface.

## What to do instead - do not block on it

Reorder the phase 0 slices so the migration is **last**, not first. Everything else in the slice is
independent of it:

- the push message and the Director-side send off the existing turn-end flip,
- `ReadStored` / `ReadLiveAsync` and the split ruled in r1,
- the placement in front of the six existing tunnel-read sites,
- the 7-day sweep's logic,
- the fixtures and the proofs' scaffolding.

Write the entity and the store against its intended shape and add the migration when the slot is
genuinely free. #2643 is in review now, so the wait should be short.

**When you believe the slot has come free, re-run the sweep above and say what it returned** - do not
report it as free because #2643 merged. Resolve #2379 at the same time.


---

## AMENDMENT - the sweep above is WRONG; use this one

The Manager checked the instrument rather than only the answer, and found two defects in the command
this ruling published. Both are corrected here rather than left in a durable file for the next reader
to inherit.

1. **It missed the Postgres half.** Migrations are written for BOTH providers. `AddKnownRepositories`
   exists twice - `src/CcDirector.Gateway/Data/Migrations/` (SQLite) and
   `src/CcDirector.Gateway.Migrations.Postgres/Migrations/` - and the original pattern saw only the
   first.
2. **It raised false holders.** `Data/Migrations` also matches `Stats/Data/Migrations`, which belongs
   to `GatewayStatsDbContext` - a different context that does not contend for this snapshot at all. It
   flagged five dead `nosqlite` and `fix-stats` branches as holders.

The second defect is the worse one. A sweep that raises holders which are not holders teaches
whoever runs it to wave hits away, and a check people have learned to dismiss is worse than no check.

**The corrected sweep, pinned to the two `GatewayDbContext` directories:**

```
git fetch origin
for b in $(git branch -r --format='%(refname:short)' | grep -v HEAD | grep -v 'origin/main$'); do
  m=$(git diff --name-only origin/main..."$b" 2>/dev/null       | grep -E '^(src/CcDirector\.Gateway/Data/Migrations|src/CcDirector\.Gateway\.Migrations\.Postgres/Migrations)/.*\.cs$'       | grep -v ModelSnapshot | grep -v Designer)
  [ -n "$m" ] && { echo "HOLDER $b"; echo "$m" | sed 's/^/    /'; }
done
```

Run 2026-09-02 over all 44 remote branches, it returns exactly two holders and no others:
`origin/feat/mobile-session-picker-repository-search` (PR #2643, open and active) and
`origin/prompt-delete-erases` (PR #2379, open, untouched since 2026-08-08).

## Ruling on PR #2379

**It counts as a holder.** Not because it is certainly alive, but because "open, and nobody has
touched it for 25 days" is not evidence of abandonment - it is an absence, and taking an exclusive
shared resource on an absence is the defect this whole ruling exists to name.

It is also **not the binding constraint today**: #2643 holds the slot regardless, so #2379 costs this
mission nothing right now and must not consume the owner's attention yet. If #2643 merges and #2379
is then the only thing standing between this mission and its last two proof rows, that is the moment
it becomes one plain question for the owner - close it or land it - and not before.


---

## SECOND AMENDMENT - 2026-09-02, after #2643 landed: the sweep lied a third time

Run the moment #2643 merged, the amended sweep reported
`origin/feat/mobile-session-picker-repository-search` as STILL HOLDING - the branch whose merge had
just freed the slot.

**Why.** `git diff origin/main...branch` compares the branch against the MERGE BASE. After a squash
merge the content is on main but the merge base has not moved, so every file the branch added still
shows as added. The branch had also already been deleted on origin; the local remote-tracking ref was
stale because the fetch had no `--prune`.

Two corrections, both required:

1. **Fetch with `--prune`**, or deleted branches keep voting.
2. **Test whether the file is PRESENT ON MAIN, not whether it differs from the merge base.** Presence
   is the question being asked - "does main already have this migration?" - and the diff was a proxy
   for it that stops being true the moment a branch merges.

```
git fetch origin --prune
for b in $(git branch -r --format='%(refname:short)' | grep -v HEAD | grep -v 'origin/main$'); do
  held=""
  for f in $(git diff --name-only origin/main..."$b"              | grep -E '^(src/CcDirector\.Gateway/Data/Migrations|src/CcDirector\.Gateway\.Migrations\.Postgres/Migrations)/.*\.cs$'              | grep -v ModelSnapshot | grep -v Designer); do
    git cat-file -e "origin/main:$f" 2>/dev/null || held="$held    $f
"
  done
  [ -n "$held" ] && { echo "HOLDER $b"; printf "$held"; }
done
```

**Third defect, third correction, same instrument.** That is the point worth keeping: this sweep has
now been wrong about missing the Postgres directory, wrong about Stats being a different context, and
wrong about a merged branch. Each time it failed in the direction of a FALSE ALARM, and each time the
temptation was to eyeball the output and wave the hit away. Do not. Fix the instrument.

**Result of the corrected run, 2026-09-02 after #2643 landed:**

- `feat/mobile-session-picker-repository-search` - gone from origin, migration on main. **Released.**
- `mission/terminal-rules` - our own provisional migration. Expected.
- `prompt-delete-erases` (PR #2379) - three migrations from 2 August. **Still a holder.**

**#2379 does NOT block this mission and the owner is not to be asked about it yet.** Ruling 2's
amendment set that trigger as "#2379 is the only thing standing between this mission and its last two
proof rows". It is not: ruling 6's rebase-and-regenerate onto the new main can be done now, and the
rows can run. #2379 is a future collision, not a present block. The trigger has not fired.
