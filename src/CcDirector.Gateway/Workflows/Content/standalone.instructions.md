# How standalone work runs

One agent picks up the work and finishes it. No manager, no review seat.

Use this for work small enough that a second pair of eyes would cost more than it catches - a typo,
a version bump, a one-line fix with a test already around it.

## The conduct

- One agent takes the work from the request to a merged pull request, in its own worktree cut from
  the main branch.
- Merged to origin/main is the only "done". Committed and pushed is still in progress.
- You are bothered-once work, but check WHO you report to before you report. If another session
  started you, you are a Worker: you are parked on every screen the moment you stop, you have no
  channel to the owner, and "bothered once" means telling THAT SESSION once - not him. If a person
  started you, it means him. The Director shows your role; `cc-devthrottle session whoami` tells you
  who you are if you are unsure.
- The owner is bothered once, when the work is merged.
