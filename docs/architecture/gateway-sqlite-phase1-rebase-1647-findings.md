# Phase 1: what the #1647 rebase costs, and what the new shape means

Written 2026-07-15 by the Manager ("Gateway SQLite - Manager", session f3599eba) after the Architect
stopped the fold rewrite. Read against `origin/main` at `6cc49d25`, on the rebased branch.

The Architect was right to stop it. Pull request #1647 rewrote the file Phase 1 ports:
`GatewayInputStatsAggregator.cs` is +193/-18 lines since the base this work started from. Finishing
the port against the old shape would have silently reverted a real bug fix - the worst thing this
mission could do.

## What the rebase cost

Almost nothing, which is the good news:

- The rebase was clean. Nineteen commits replayed with no conflicts.
- **Step 3 survived intact** (now `bad2c3a6`). The database, the schema, and the migration touch
  nothing #1647 touched. Its nine tests pass unchanged on the new base.
- **The fold rewrite is discarded.** It ported the pre-#1647 shape. It is saved at
  `<scratchpad>\phase1-fold-wip-PRE-REBASE.patch` and in `git stash` for reference only. Its
  *structure* still stands - the batch-then-commit design, the membership mirror, the surrogate ids,
  the archive rule - but the fold body and the import must be rewritten against the real shape.

Roughly half a step's work, and it bought correctness. Cheap.

## Correction: the section count is TWELVE, not eleven

The Architect's message says `StoreFile` gained three sections and now has eleven. **It has twelve.**
Derived, not asserted - counting the properties on `origin/main`:

```
git show origin/main:src/CcDirector.Gateway/Stats/GatewayInputStatsAggregator.cs \
  | sed -n '/private sealed class StoreFile/,/^    }/p' \
  | grep -cE '^\s+public .* \{ get; set; \}'
-> 12
```

The twelfth is `AgentsSeeded`, and this matters because of the next finding.

## Correction: `AgentsSeeded` IS persisted, and the code says it must be

The Architect wrote that `_agentsSeeded` "is in-memory only and NOT in StoreFile, so whether you
persist it changes behaviour ... I think there may be a live defect in it".

It is in `StoreFile` (`:577`), it is written by `Save` (`:736`), it is read by `Load` (`:686-687`),
and the field's own comment (`:80-81`) states the reason:

> "MUST be persisted: without it a Gateway restart would back-fill every live session a second time
> and double the agent numbers."

So the decision the Architect asked me not to make by taste has already been made, correctly, by
#1647. There is nothing for Phase 1 to choose here - only something to preserve.

**Is there a live defect?** I looked rather than guessed. There is a latent coupling but not a live
defect:

`Load` (`:677-683`) discards `_agents` entirely when `AgentsSeeded` is null. That clear also zeroes
each agent's `AgentDrivenTurns`/`AgentDrivenCharacters` (#1636), while the global `_agentDrivenTurns`
is loaded from the store and is **not** cleared, and `_agentDrivenHighWater` is preserved - so no
delta would ever re-attribute them. The per-agent and global agent-driven numbers would disagree
permanently.

That state is unreachable today: `AgentsSeeded` (#1633) and the agent-driven lane (#1636) shipped in
the same pull request, so a store with agent-driven data but no `AgentsSeeded` key cannot exist. It
is a real coupling worth knowing about, not a bug to fix, and per the standing rule this mission does
not touch it.

## The finding that actually changes the import

**The owner's live store has `AgentsSeeded` absent** - it is 8 sections, confirmed on disk today, with
no `AgentDriven*` either. Under `Load`, absent means null, and null means:

> "the partial tally is DISCARDED and rebuilt from the high-water" (`:668-683`)

**So the first time the current Gateway loads the owner's real store, the JSON store itself reports
DIFFERENT agent numbers than the document contains.** The `Agents` section on disk is a partial,
hybrid tally that #1647 deliberately throws away and rebuilds from the high-water via the first-fold
back-fill.

This breaks the import as designed, in a way that would have passed straight through review:

- Decision 5 says import each projection **as it stands**. Importing the `Agents` section verbatim
  would import a projection the production code has decided is **wrong and must be discarded**.
- The parity check compares SQLite against "what the JSON store reported". The JSON store reports the
  **rebuilt** agent tally, not the stored one. A verbatim import would therefore *fail* parity - and
  the tempting fix would be to compare against the stored section instead, which would make the
  import faithfully preserve a tally the product has already rejected.

**Proposal:** the import must reproduce `Load`'s decision, because "as it stands" means "as the store
*reports* it", not "as the bytes on disk say". Concretely: when `AgentsSeeded` is null, do **not**
import `baseline_agent` from the `Agents` section - import zero, and let the ordinary first-fold
back-fill rebuild the agent tally from the imported `session_highwater`, exactly as `Load` does. When
`AgentsSeeded` is present, import both it and the `Agents` section verbatim.

That also means **`_agentsSeeded` needs a home in the schema** - it is a membership set ("have I
already attributed this session's prior turns?"), so it is a distinct-id table and a mirror set, and
its null-versus-empty distinction must survive: absent and empty are different states and conflating
them re-runs or skips a back-fill.

## Two more things the new shape needs from the design

1. **The agent-driven lane is a SECOND high-water lane.** `_agentDrivenHighWater` is keyed by session
   id alone (`:106`), not by modality and surface, so `session_highwater` cannot hold it without a
   discriminator. **Recommendation: a separate `agent_driven_highwater` table**, and a separate
   `agent_driven_delta` table rather than a lane flag on `stat_delta`. The reason is the Architect's
   own principle applied where it earns its keep: the comment at `:102-105` says these turns "never
   enter `_totals`, `_hourly` or the buckets, because the human voice-versus-typed numbers must stay
   about the human". If agent-driven rows share `stat_delta`, then every human aggregate query must
   remember to exclude them - a rule to obey, and exactly the archive-marker problem again. In a
   separate table they **cannot** be summed into the human totals by accident. This is a silent
   failure if it goes wrong (the voice-versus-typed share would quietly include agent traffic), so it
   earns structure rather than a test.

2. **`FoldAgentDrivenLocked` runs BEFORE the empty-buckets guard** (`:352-357`), because "a session
   driven only by other agents has no human buckets at all - and those are exactly the sessions this
   tally is about". My design emitted rows only inside the bucket loop, so those sessions would have
   produced no row and their turns would have vanished. The separate agent-driven table fixes this
   too: its rows are emitted from the agent-driven fold, independent of any human bucket.

## Consequence for acceptance criterion 1

The "before" capture must be taken from the **current** build (post-#1647), not from the snapshot
taken earlier today. That snapshot predates the rebase and its agent numbers are the pre-#1647 ones,
which the current code would discard and rebuild on load. Capturing "before" from the old build and
"after" from the new one would compare two different products and prove nothing about this port.

The snapshot remains valid and verified-restorable as a **backup**. It is not valid as a criterion 1
baseline.
