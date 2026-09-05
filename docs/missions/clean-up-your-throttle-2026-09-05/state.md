# Clean up Your Throttle - running state

This is the compact handoff note. A fresh Manager needs THIS FILE, the brief beside it, and
`cc-devthrottle workflow instructions mission`. Nothing else. Keep it current and short.

**Updated:** 2026-09-05, by the Architect, at mission start.

## Where the work lives

- Product repository worktree: `D:/ReposFred/devthrottle-throttle`, branch
  `mission/clean-up-your-throttle`.
- Internal repository worktree: `D:/ReposFred/devthrottle_internal-throttle`, branch
  `mission/clean-up-your-throttle`. Not touched until phase five.

## Current phase

**Phase one - MEASURE.** Not started.

Nothing else in this mission may start until phase one's account exists. This is the one thing the
Architect will not bend on: the library encodes one answer to what counts and over what period, and
that answer is not known yet.

## What is done and pushed

- The brief. Nothing else.

## The next Worker task

Produce `docs/missions/clean-up-your-throttle-2026-09-05/reconciliation.md` to the specification in
the brief's phase one. It changes no product code and no harness code. It reads.

## What it proves

That the two figures' populations and windows are reconciled record by record for one real week and
one real person, with counts on both sides - not a story that happens to fit 59 and 92.

## Open, for the Architect only

- R9: the shape of the shared thing. Settled at the start of phase three, on phase one's evidence.
  Nobody else settles it.

## Known ground already established (do not re-derive)

- The report's headline "59% spoken" is `prompt_shape.modality_share`, a share of human PROMPTS -
  the same unit as Your Throttle's turn share, not a word share. The report's word-based voice
  figure is a different metric and is out of scope.
- The mentor classifier already sorts every user record into human, agent, framework or unresolved,
  and only `human` reaches the ring. So by design it does not count other sessions' messages as his.
  Whether that survives the week's real records is phase one's job.
- Your Throttle already carries agent-driven turns in a separate lane that is excluded from the
  modality shares (`InputStatsDto.AgentDrivenTurns`). Same by-design claim, same need to check it.
- `GET /stats/data` is tenant-scoped: on the hosted Gateway it resolves the caller's tenant from the
  authenticated device key and refuses when none resolves. His 92% is his own data, not the
  Gateway's whole population.
- There are two accounts in the mentor configuration, `soren` and `mario`. The conformance check in
  phase three runs over real weeks for both.
