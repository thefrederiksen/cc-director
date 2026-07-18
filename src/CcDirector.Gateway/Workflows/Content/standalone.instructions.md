# How standalone work runs

One agent picks up the work and finishes it. No manager, no review seat.

Use this for work small enough that a second pair of eyes would cost more than it catches - a typo,
a version bump, a one-line fix with a test already around it.

## The conduct

- One agent takes the work from the request to a merged pull request, in its own worktree cut from
  the main branch.
- Merged to origin/main is the only "done". Committed and pushed is still in progress.
- The human is bothered once, when the work is merged.
