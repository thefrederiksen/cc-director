# Ruling 7 - never hand-keep a list of the thing; and one tree per activity, me included

Architect ruling.

## Verified here

- `has-pending-model-changes` answers **"No changes have been made to the model since the last
  migration."**
- The migration exists on the branch for both providers (`20260902105533_AddSessionScreens` SQLite,
  `20260902105640_AddSessionScreens` Postgres), and **nothing** matching `SessionScreen` is on
  `origin/main`. No pull request is open from this branch. Ruling 6 executed exactly as ruled.

Reconciling the new suites against the TRX rather than trusting the run total is the right method and
is the reason the gate number means something.

## 1. The hand-kept enumeration - third instance of one class

The hand-written hub-method fixture never got `PushScreen`, so it reported a **complete** Gateway as
missing one, while its reflection-derived sibling kept passing. A false alarm, and a false alarm is
not the harmless direction: it trains whoever meets it to dismiss that check.

This is the same defect three times in this mission, in three different disguises:

| where | the parallel copy | how it lied |
|---|---|---|
| the pull counter | a count kept per caller | would read zero while a caller added later made round trips |
| ruling 2's sweep | a hand-written path pattern | missed the Postgres directory, and invented five holders that were not holders |
| the hub fixture | a hand-written list of hub methods | called a complete Gateway incomplete |

One rule covers all three: **derive the enumeration from the thing itself; never maintain a parallel
copy of it by hand.** Reflect over the hub's methods, grep the tree for the call sites, count at the
single choke point. A hand-kept list is correct on the day it is written and drifts from that day on,
and it drifts silently in the dangerous direction as often as the noisy one.

The Manager spotted this in its own work, which is where it is hardest to see. Fix the fixture by
deriving it, not by adding `PushScreen` to the list - adding the missing entry fixes today's symptom
and leaves the mechanism in place for the next method.

## 2. One tree per activity - and I broke it too, ten minutes ago

Recorded as reported: a Worker was spawned into the Manager's own worktree, so they shared a working
tree and an index, and the Manager's uncommitted fix to a file was swept into the Worker's commit.
Nothing was lost. The rule is one checkout per concurrent activity, and it was broken.

**I did the same thing immediately afterwards.** I tried to run the test project inside that worktree
to verify the gate for myself, and the build failed with the assembly locked by two `testhost`
processes that were not mine. The Architect reading over the builder's shoulder is a third activity
in a tree that already had two.

So the rule is restated with the resolution that was missing, because "one worktree per mission" and
"one checkout per activity" read as contradicting each other:

- **A mission has one BRANCH.** That is what the mission rule means, and it does not change.
- **Every concurrent activity gets its own TREE.** A Worker gets a worktree cut from the mission
  branch and pushes back to it. The Manager keeps its own. Neither builds in the other's.
- **The Architect verifies from its own tree or from the pushed branch**, never inside a tree that
  something is working in. Reading git history, diffs and `origin/` refs is always safe; running a
  build is not.

Where a Worker genuinely must share a tree, the Manager commits or stashes everything first, so there
is nothing loose for the Worker's commit to sweep up. That is a fallback, not the arrangement.

## Standing

Rows 1, 2 and 3 proven. The sweep-returns-1 assertion is unblocked and back with the Worker. Nothing
here changes what is owed when #2643 lands (ruling 6): re-run the corrected sweep, rebase and
regenerate, re-run `has-pending-model-changes` to no-changes, then re-run the gate and every row.
