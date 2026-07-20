# The mutation-proof pin: how to take a baseline and a mutation arm that mean something

## What a mutation proof is, and the one way it lies

We prove a security guard is real by mutating the one primitive it depends on, running the whole suite
again, and reconciling the arithmetic: the tests that pass plus the tests that fail under the mutation must
add up to the tests that passed under the restored run. If they add up, the suite is held to have observed
the primitive.

There is exactly one failure that reconciliation cannot see, and it is the one that matters most.

Suppose the baseline is taken on a working tree where a security guard block has **already been silently
deleted and left uncommitted**. That is not a hypothetical - it is what a killed run leaves behind, because
a process that is killed skips its cleanup. The tree still compiles. The suite still runs. The baseline
still looks green and complete. Then the mutation arm runs on that same contaminated tree, and the two runs
reconcile **perfectly** - because they agree with each other, not because either one measured anything.

The cheapest detector we own passes while measuring nothing, and nothing anywhere in the process says so.

On 2026-07-19 a worker found exactly that on disk in its own worktree. A sweep of the mission's working
trees that night found four dirty trees.

## Why telling people to check their tree does not work

The worker who **inherits** a contaminated tree is, by definition, the one who does not know there is a
mutation in it. It looks clean to them: it compiles, and the tests pass.

This is the same shape as the concurrent-suite problem the per-user test-suite lock was built for. Four
workers were each told not to run two suites at once; each complied with everything it could observe; four
suites ran concurrently anyway. A rule that requires global knowledge cannot be obeyed by an actor holding
only local knowledge. That one was solved by a mechanism rather than a briefing, and so is this.

## How to run a proof

Set the pin once, before the baseline. From that moment until you release it, **every** test run in that
working tree is checked - the baseline, the arm, and any re-run of either.

```
# 1. Before the baseline. Refuses if the tree is already dirty.
./scripts/mutation-proof-pin.ps1 set -Phase baseline -Note "tenant isolation guard, issue 1901"

#    Run the suite. Record the numbers against the pinned head it printed.

# 2. Apply the mutation, then re-declare as the arm, naming what it touches.
./scripts/mutation-proof-pin.ps1 set -Phase arm -Mutates src/CcDirector.Gateway/Api/GatewayEndpoints.cs

#    Run the suite again. Reconcile.

# 3. Restore the mutation and release the pin.
./scripts/mutation-proof-pin.ps1 release
```

`status` shows the current pin and the tree beside it. `ledger` prints every proof run ever recorded on
this machine.

## What the guard refuses, and what it never touches

With a pin active it requires the head to be the pinned head, and the working tree to carry **exactly** the
declared changes:

- **more** than was declared is a contaminated run, and is refused naming each undeclared file;
- **less** than was declared is refused too, and that half matters as much: an arm with no mutation in it
  is a second baseline wearing an arm's name, and it reconciles perfectly against the first while proving
  nothing.

A baseline is simply an arm that declares no mutations, so one rule runs both phases.

**With no pin, the guard has no opinion at all.** A worker mid-rework is legitimately dirty and must be able
to run whatever it likes. This is deliberate and is not an oversight to be tidied up later: a blanket
refusal on any dirty tree would be switched off within a day, and a guard that gets switched off protects
nothing.

## The second job: the record outlives the tree

Refusing a bad run is only half of it.

Somebody was asked whether four **already-merged** security proofs had been taken on contaminated trees. The
answer could not be given - not because the evidence was hard to find, but because it no longer existed.
Those proofs were run in worktrees, and the worktrees were removed after merging. That removal is correct
hygiene, and it destroyed the only artifact that could have answered the question. Those four are recorded
as unknown and will stay unknown.

So every run appends a line to a ledger that lives **outside every working tree**, under the per-user local
application data directory alongside the suite lock's own state. `git worktree remove`, `git clean -xdf`,
and deleting the whole checkout cannot reach it.

The ledger writes on **admission** as well as refusal, and an admission line states which head was verified
and that the tree matched it. That is the point: a log written only when something goes wrong cannot
afterwards distinguish "verified clean" from "the guard never ran". Both are silence, and silence reads as
success.

Unpinned runs are recorded too - never refused. That is what makes a **forgotten** pin recoverable: when a
proof's numbers are questioned later, the record still says whether the tree was dirty at the moment the
baseline ran.

## Where it lives

- `src/TestInfrastructure/MutationProofPinGuard.cs` - the mechanism. Read the comment at the top before
  changing it.
- `src/TestInfrastructure/MutationProofPinGuardTests.cs` - the proof that it fires, with both controls.
- `Directory.Build.props` - links both into every project whose name ends in `.Tests`, so a test project
  added next year is covered without anybody remembering. It keys on the project name rather than the
  `IsTestProject` flag because one of the seven existing test projects does not set that flag.
- `scripts/mutation-proof-pin.ps1` - set, status, release, ledger.

## The one thing that is not automatic

A worker can still forget to set the pin at all, and then nothing is checked. That limit is the price of
the scope gate, and it is stated here rather than papered over.

Two things narrow it. The pin is the only place the pinned head is written down, so a proof that skipped it
has no pinned head to cite and is not a proof anybody can write up. And the ledger records unpinned runs
anyway, so a forgotten pin leaves evidence instead of a hole.
