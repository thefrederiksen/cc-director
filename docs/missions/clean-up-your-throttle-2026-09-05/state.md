# Clean up Your Throttle - running state

This is the compact handoff note. A fresh Manager needs THIS FILE, the brief beside it, and
`cc-devthrottle workflow instructions mission`. Nothing else. Keep it current and short.

**Updated:** 2026-09-05, by the phase one Manager, when reconciliation.md was written and pushed.

## Where the work lives

- Product repository worktree: `D:/ReposFred/devthrottle-throttle`, branch
  `mission/clean-up-your-throttle`.
- Internal repository worktree: `D:/ReposFred/devthrottle_internal-throttle`, branch
  `mission/clean-up-your-throttle`. Not touched until phase five.

## Current phase

**Phase one - MEASURE. DONE.** `reconciliation.md` is written and pushed, with the scripts behind
every number in `evidence/` beside it.

Phase two has NOT started and is the Architect's to open.

## What is done and pushed

- The brief.
- `reconciliation.md` and `evidence/` - phase one's account, over 2026-W35 (Monday 24 August to
  Sunday 30 August, America/Toronto), for `soren@centerconsulting.com`.

## What phase one found, in one place

- **The window explains almost nothing.** Over the same week, same zone, same person: the report's
  figure is 58.76 per cent spoken and the Your Throttle store's is 91.46 per cent.
- **The Gateway's own submission ledger says 56.83 per cent spoken that week.** The report is close
  to it; Your Throttle is the figure that is wrong.
- **Three defects, none of them the two the brief names**, produce the whole gap:
  1. `Session.SendInput` stamps a submission event and never calls `RecordTurn`, so a turn typed at
     the desktop terminal is counted as characters only. 594 turns that week, 77 per cent of his
     typing. Worth 28.3 points on its own.
  2. 2,061 of the 3,279 stored turns for the week are restated cumulatives or duplicated rows, 96
     per cent of it on voice. Worth a further 8.2 points. The MECHANISM is not proven.
  3. `GET /stats/data` has no window at all: it serves every turn since 2026-08-02, unlabelled.
- **Both holes named in R2 are at zero in his real week.** The chat relay is dead code - nothing
  constructs `ChatService` and no route maps it. No transcription in the week came from the phone
  voice endpoints; the ceiling on that hole is 60 turns, worth 3.4 points.
- **The owner's question:** no session-to-session traffic is counted as his on either side. But 292
  of the week's 296 fleet messages were recorded as ordinary `UserInput` with no origin, not as
  agent traffic - so both sides exclude them by accident rather than by record, and the
  agent-driven lane under-reports by the same 292.
- **One Gateway, two machines.** The self-hosted Gateway has recorded nothing since 2026-07-21.

## Open, for the Architect only

- **R2 needs revisiting on this evidence.** Fixing the two named holes would move his number by at
  most 3.4 points and probably by nothing. The three defects above are what makes the shared number
  right. This is a scope decision and it is the Architect's.
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
