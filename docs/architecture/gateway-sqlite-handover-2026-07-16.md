# Gateway SQLite - handover to a clean Architect

Written 2026-07-16 by the outgoing Architect (session 8c17dc1c) at the owner's instruction. He
judged this session had made a lot of mistakes and wanted the work continued from a clean context on
a newer Director. He is right. This is short on purpose.

## What shipped, and it is live

**The Gateway's input statistics are in SQLite. Done, merged, deployed, and growing.**

- `#1694` merged the fold. The owner's Gateway runs `1.3.0+01a248b4`, which is that exact merge
  commit on `origin/main` - not a hand-placed build.
- `gateway-stats.db` at `%LOCALAPPDATA%\cc-director`, `user_version=1`, rows landing from his real
  fleet and rising on their own (43 turns -> 67 while I watched).
- `gateway-input-stats.json` is renamed aside UNREAD to `.retired-*` and never read. Proven, not
  asserted: a store seeded with 999 turns was retired beside a database reporting zero.
- Cockpit and mobile both read it - `apps/mobile/src/pages/YourThrottle.tsx` and the Cockpit both go
  through `packages/client-core/src/stats/statsClient.ts`, which fetches `GET /stats/data`. One
  feed, both surfaces.
- Fresh installs self-create: directory, file, tables, `user_version` - all on first run, and the
  retire step is guarded by a file-exists check so a machine with no JSON skips it cleanly.

Also merged tonight and worth knowing: `#1658` stops the Gateway test suite writing into the owner's
live `cc-director` folder (a test run had renamed his live stats file aside; only timing prevented
total loss), and `#1696` puts the no-agent-attribution rule into `FleetPreamble` so every agent on
every machine is told the code is his and does not get signed.

## What is NOT built - this is your work

The owner asked for **far more logging than exists**, for governance: *"I want to know everything I
do, whatever I've done in a day or a week or a month and what I spend my time on. If in doubt, log
more."* Gigabytes are fine; a rolling retention is acceptable to him.

Today's rows carry: session id, hour, modality, surface, `is_voice`, repo, wingman, turns,
characters, plus separate agent and agent-driven lanes.

The gaps, in the order I would do them:

1. **Model.** `SessionDto.CurrentModel` merged tonight (#1651), so the producer is live and this is a
   column plus a migration. Do this first: it is cheap AND it is the first real proof the
   `user_version` migration path works, which is the whole justification for this mission.
2. **Tokens.** The real gap. `SessionTokenUsage`, `ClaudeResponseParser` and `CodexContextUsage`
   parse them in Core, but **nothing carries them to the Gateway** - `SessionDto` has no token field.
   Build the wire before the column. Do not add a column nothing emits.
3. **Prompts / governance views.** The day/week/month "what did I spend my time on" screens do not
   exist. `/stats` is a self-contained embedded HTML page, not a Cockpit route.

**Read `docs/architecture/gateway-sqlite-mission-2026-07-15.md` on branch `feat/gateway-sqlite`
before designing any of it.** It is long and it is the one genuinely valuable artifact of this
mission - it records the decisions and, more usefully, why several obvious-looking designs are
wrong.

## The five things most likely to bite you

1. **The model dimension is a phase, not a column.** `CurrentModel` is free text with unbounded
   cardinality; a Claude session reports its launch alias (`opus[1m]`) before its first turn and the
   concrete id after, which is two names for one model - the same shape as the repository separator
   split below. It also needs a since-stamp in the `_agentsSinceUtc` mould, and three display states
   (predates-the-dimension / unknown-at-fold-time / concrete), because records-only means every
   session's FIRST turn folds with no model, forever. Session `60ffd96b` owns the producer and has
   the context.
2. **Do not normalize repository path separators.** Three repositories are stored twice on his
   machine - `D:\ReposFred\devthrottle` (324 turns) and `D:/ReposFred/devthrottle` (21).
   `OrdinalIgnoreCase` folds case but not slashes. It looks like a bug. "Fixing" it changes his
   numbers.
3. **`agent_id` is deliberately NOT on `stat_delta`.** `AttributeToAgentLocked` has two call sites
   and the back-fill one has no totals counterpart, so deriving agents from `stat_delta` has *no*
   correct behaviour. The agent lane has its own table. Do not merge it back.
4. **The migration machinery is not scaffolding.** `PRAGMA user_version` is the point of the
   mission. "The import is gone so the migration can go too" is a sentence that sounds reasonable
   and would gut the whole thing.
5. **A check that cannot find a thing will report the thing is absent.** This happened three times in
   one day here: an assertion that could not match the defect, a red-watch that failed for the wrong
   reason, and a grep whose filter excluded the only lines that could match. Empty output is not
   evidence until you have shown the check can produce output.

## How this mission runs

- **Worktree:** `D:\ReposFred\devthrottle-gateway-sqlite`, branch `feat/gateway-sqlite`. Never the
  shared checkout.
- **A Codex reviewer gates every commit**, as a REAL DevThrottle session (`--agent Codex`), never a
  background process - the owner has rejected that explicitly. Reviews go to FILES under
  `docs/architecture/`, never a terminal: the buffer captures only spinner redraw.
- **Merged to `origin/main` is the only done.** This mission learned that the hard way - the fold was
  deployed by hand, and another agent shipped from main 17 minutes later and wiped it. The owner's
  stats silently fell back to JSON.
- **No agent attribution** in any commit, pull request, issue or document. Ever.
- Plain ASCII, plain English, no abbreviations.

## What the outgoing session got wrong - learn from it rather than repeat it

- **I spent a full day producing an excellent design document and almost no code.** Fifteen revisions
  of a brief before one line of product code existed. Ship increments.
- **I never checked the premise.** I designed a careful migration to preserve his old statistics for
  most of a day. He then said, unprompted, that he did not want them kept - which deleted the hardest
  half of the design. **Ask.**
- **I reported "deployed" when I had not merged**, which made it temporary and it was gone in 17
  minutes.
- **I over-reported.** He told me twice to stop narrating every finding. He wants the decision, not
  the process.

## Cleanup outstanding

- Sessions to reap: the Manager `f3599eba` and the Codex reviewer `7068ec90`, both idle.
- Dead worktrees: `devthrottle-testroot-fix`, `devthrottle-sqlite-land`, `devthrottle-no-attrib`,
  `devthrottle-gw-deploy` - all merged, all removable.
- `gateway-stats.db.stale-reverted-deploy-*` in his storage root - dead weight from the reverted
  deploy, safe to delete.
- **One unexplained test failure**, one in 2435, never diagnosed and never reproduced. It is an OPEN
  unknown in the record, not a resolved one. Do not close it because it did not recur.
- `feat/gateway-sqlite` carries the brief and the review records but must NOT merge to main as-is -
  Codex flagged that its review files describe an import and legacy reader that were later deleted,
  which would land as contradictory architecture.
