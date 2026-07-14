# Research: why does the multi-agent build cost more tokens than a single session?

Status: OPEN research project. First findings below. Raw data preserved in
`raw-sessions/`, reproducible scripts in `analysis/`.

Author: architect session ed11a5cf (ctxtax-multi-architect), SOREN_NORTH.
Date: 2026-07-11.

Round-2 follow-up (a bigger, more complicated build to find the crossover) is planned in
`NEXT-EXPERIMENT.md` and tracked as devthrottle_internal #345.

---

## 0. Where we are

- Repo (git toplevel): `D:/ReposFred/devthrottle-ctxtax-multi`  (a worktree of the
  cc-director / DevThrottle product repo).
- Branch: `exp/ctxtax-multi`. Head: `dc9a2cfd` (the feature this experiment built).
- Sibling worktree: `D:/ReposFred/devthrottle-ctxtax-single` (the single-session run of
  the SAME feature).
- This document + all backups live under `research/multi-vs-single-token-study/` in the
  multi worktree. NOT committed (waiting on explicit ask).

## 1. What we compared

Both runs built the EXACT same feature from the EXACT same instruction file (`SPEC.md`,
per-repository default browser+profile, devthrottle #1112), from the same base commit
`6da36415`. The only difference was the method:

- SINGLE: one Claude session did planning + coding + tests + proof.
- MULTI: an architect spawned a manager, which spawned three workers (store / menu /
  tests) on disjoint files, then integrated. Five sessions total.

Both produced a working, tested, committed feature. This study is ONLY about token cost,
not quality (quality comparison is a separate open question, see section 7).

## 2. Headline numbers

Token totals per session = input + output + cache-create + cache-read (the full billing
footprint). Source: each session's Claude Code transcript `usage` records.

| Run | Sessions | Turns | Output tok | Total tok |
|-----|---------:|------:|-----------:|----------:|
| SINGLE | 1 | 64 | 55,124 | 6,670,262 |
| MULTI - builders only (manager + 3 workers) | 4 | 166 | 123,817 | 14,917,878 |
| MULTI - all 5 (incl. architect) | 5 | 280 | 181,131 | 24,633,777* |

*The architect session is THIS session; it is still running and has been polluted with the
verification and this very token-analysis work, so counting it as "build cost" overstates
the multi total. The fair build-to-build comparison is **builders only**:

**MULTI builders cost ~2.2x the tokens of SINGLE** (14.9M vs 6.7M), for the same feature.
(An earlier off-the-cuff figure of 3.7x wrongly included the contaminated architect
session. 2.2x is the honest number.)

The direction is not a fluke and does not reverse: fanning out costs MORE tokens, never
fewer. Section 3 explains exactly why.

## 3. THE MECHANISM - where the tokens actually go

Per-turn decomposition (`analysis/02-per-turn-decomposition.py`):

| session | turns | start floor | avg context/turn | peak context | output |
|---------|------:|------------:|-----------------:|-------------:|-------:|
| SINGLE (did everything) | 64 | 68,956 | 103,361 | 126,977 | 55,124 |
| multi: MANAGER | 92 | 69,749 | 96,291 | 121,394 | 57,303 |
| multi: worker (store) | 23 | 69,804 | 76,647 | 81,525 | 13,759 |
| multi: worker | 25 | 69,999 | 83,148 | 90,397 | 25,392 |
| multi: worker | 26 | 69,957 | 80,526 | 91,430 | 27,363 |
| multi: ARCHITECT (contaminated) | 114 | 69,208 | 106,034 | 140,998 | 89,293 |

Two facts explain everything:

### Fact A - every session starts with a ~69,000-token fixed floor

Look at the "start floor" column: SINGLE, MANAGER, and every WORKER all begin at
~69K tokens BEFORE doing any work. This floor is the same in every session because it is
the same material loaded fresh into each agent:

- the base Claude Code system prompt + the built-in tool schemas (Bash, Edit, Read,
  Workflow, Monitor, ... - these descriptions are very long),
- the user's global `CLAUDE.md` (~6.5K tok) + this project's `CLAUDE.md` (~2.2K tok),
- the auto-memory `MEMORY.md` index (~4.9K tok),
- the available-skills list (~40 skills, each with a description),
- the deferred MCP tool-name list (242 `mindzie_*` names alone were counted in the
  session-start reminder, plus Google Drive / pencil / localhost-mindzie sets),
- the DevThrottle fleet preamble + MCP server instructions,
- the first user prompt.

Rough decomposition of the ~69K floor (chars/4 estimate; needs a real tokenizer pass,
see section 7): built-in tool schemas + base prompt dominate (~25-35K), then CLAUDE.md +
memory + skills + MCP tool names (~20K), then the seed prompt.

### Fact B - that whole context is re-read on EVERY turn

`cache_read_input_tokens` grows with the conversation: each turn the model re-reads the
entire accumulated context (system prompt + floor + all prior messages + tool outputs).
So a session's cost is approximately:

    session cost  ~=  turns  x  average-context-per-turn

Check: SINGLE 64 x 103K = 6.6M (actual 6.67M). MANAGER 92 x 96K = 8.9M (actual 8.92M).
worker 23 x 77K = 1.77M (actual 1.78M). The model holds.

### Putting A and B together - the floor is 66-77% of ALL cost

The ~69K floor is a SUBSET of the context that is re-read every single turn. Its
contribution alone (`turns x 69K`, from `analysis/03-floor-share.py` output):

| run | turns | total tok | floor-reload (turns x 69K) | floor share |
|-----|------:|----------:|---------------------------:|------------:|
| SINGLE | 64 | 6,670,262 | 4,416,000 | 66% |
| MULTI all 5 | 280 | 27,095,150 | 19,320,000 | 71% |
| MULTI builders | 166 | 14,917,878 | 11,454,000 | 77% |

**The dominant cost in BOTH methods is re-reading the fixed system-prompt/CLAUDE.md/
memory/tools floor on every turn.** The actual work (output tokens) is tiny: 55K-124K,
i.e. under 1% of the footprint.

## 4. So why does MULTI cost ~2.2x?

It is almost entirely turn count, and each turn drags the same 69K floor:

- SINGLE did the whole job in 64 turns.
- MULTI builders took 166 turns = 92 (manager) + 74 (three workers).
- The MANAGER alone spent 92 turns - MORE than the single session's entire build -
  and produced ZERO lines of feature code. Those turns are coordination: spawning
  sessions, sending/asking messages, waiting, integrating, verifying. Every one of them
  re-read a ~96K context including the 69K floor.
- Each of the 3 workers also paid the 69K floor on all 74 of their turns, on top of the
  manager already having paid it.

In one line: **multi-agent multiplies (sessions x turns), and each turn re-pays the fixed
~69K floor. The extra sessions do not share the floor - each carries its own copy.**

This directly answers the hypothesis "is it because sessions start with a lot of memory/
context?" - YES. The ~69K start floor per session is the core of it. But note the floor
also dominates the SINGLE run (66%); the difference is that MULTI pays it across far more
turns.

## 5. When is the 2.2x worth paying?

The premium is roughly `(extra turns from coordination + per-agent floors) x floor`. That
premium is a good deal exactly when the per-agent WORK is large enough that the fixed floor
is a small fraction of each agent's context - i.e. big, long, or genuinely parallel tasks
where a single context would either overflow or rot. For a small, cleanly-scoped feature
like this one (a single session finished in 64 sharp turns), the floor dominates and
multi-agent mostly just multiplies floor-reloading for no benefit.

This is a hypothesis to test properly (section 7), but the emerging guidance:

- Reach for multi-agent when: the task does not fit one context window; OR the work splits
  into long-running parallel streams (wall-clock matters); OR a single context would
  degrade over the length of the job.
- Stay single when: the whole job fits comfortably in one context and finishes in tens of
  turns. You pay ~2.2x for nothing otherwise.

## 6. The biggest lever is the floor itself (helps BOTH methods)

Because the ~69K floor is 66-77% of all cost, shrinking it is the highest-value change and
it improves single AND multi:

- Trim what loads into every session's system prompt: the global `CLAUDE.md` is large;
  the auto-memory index is ~5K; ~40 skills and hundreds of MCP tool names load whether or
  not a session needs them. A store-layer worker did not need 242 mindzie process-mining
  tools or the browser-harness skill in its context.
- Lever idea: per-session-role context profiles - a worker gets a MINIMAL floor (its task
  slice + only the tools it needs), not the full operator floor. If the worker floor drops
  from ~69K to ~25K, worker turns get ~2.6x cheaper and the whole multi premium shrinks.
- Lever idea: fewer coordination turns - the manager's 92 turns are the single most
  expensive part of the multi run. Batching spawns, fewer status round-trips, or letting
  workers report once on completion (not chat back-and-forth) cuts manager turns directly.

## 7. Open questions / next research

1. Real tokenizer pass on the ~69K floor to get an exact component breakdown (this doc
   uses chars/4). Which single ingredient is biggest - tool schemas or CLAUDE.md?
2. QUALITY comparison, not just cost: did the two builds differ in correctness, test
   depth, design? 2.2x is only "bad" if quality is equal. (Both passed DoD here.)
3. Wall-clock comparison: the 3 workers ran concurrently (finished within ~2 min of each
   other). Quantify time-to-done single vs multi - the real multi-agent selling point.
4. Does the premium shrink on a BIGGER feature? Re-run this experiment on a task that is
   ~5x the size and see if the ratio drops toward 1x (floor amortized over more real work).
5. Measure a minimal-floor worker profile: rebuild with workers that load only their
   needed tools; re-measure the ratio.
6. Is there redundant re-caching? Check `cache_creation` churn - are we re-creating cache
   that could have been reused across turns?

## 8. Data provenance (what is backed up here)

- `raw-sessions/single/630fc75c-*.jsonl` - the single run, full transcript (every turn,
  tool call, tool result, usage record).
- `raw-sessions/multi/*.jsonl` - all 5 fleet sessions (architect, manager, 3 workers),
  full transcripts.
- `analysis/01-totals.py` - per-session and grand-total token summation.
- `analysis/02-per-turn-decomposition.py` - start floor, avg/peak context, per-session.
- `analysis/03-floor-share.py` - floor-reload share of total (to be saved).
- `SPEC.md`, `PLAN.md`, `PROOF.md` - the instruction, the architect's decomposition, the
  finished proof, for context on what was actually built.

NOTE: the architect transcript (`420a5f7d`) keeps growing while this session is open and
includes non-build work; treat its numbers as an upper bound, not build cost.
