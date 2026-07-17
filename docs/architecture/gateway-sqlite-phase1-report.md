# Phase 1 report: SQLite on the Gateway

Written 2026-07-15 by the Manager ("Gateway SQLite - Manager", session f3599eba, machine SOREN_NORTH).
Written to a file rather than held in the Manager's head, because the Manager is reset before Phase 2.

Status at the time of writing: **the foundation is built and green; the fold is not built.** The
owner is deciding whether Phase 1 finishes or stops here.

## What exists

Two code commits on `feat/gateway-sqlite`, neither pushed and neither merged, per the owner's
standing instruction for this mission.

| Commit | What it is | Tests |
|---|---|---|
| `bad2c3a6` | The statistics database, its schema, and `PRAGMA user_version` | 9 new; all seven projects green (5864) |
| `48aad7ad` | Test isolation: the suite no longer writes into the owner's live storage | 3 new; Gateway suite green (2438) |

**`48aad7ad` should be merged whether or not this mission continues.** It is not SQLite work. It
fixes a live hazard in which 26 of the 48 Gateway test files that construct a `GatewayHost` can write
the owner's real `missions.json`, `cronjobs.json` and `keyvault.json`. See "The incident" below.

## What does NOT exist

**The fold.** `GatewayInputStatsAggregator` is byte-identical to `origin/main` and still writes
`gateway-input-stats.json`. Nothing writes a single statistic to SQLite. The database is real,
correct, versioned and tested - and nothing populates it.

Also not built, and deliberately so after the owner's ruling: the import, the parity check, the
baseline tables, and the legacy reader. See "The premise change".

## The schema, and why each part is shaped the way it is

Eleven tables. The reasoning matters more than the shape, because the shape was wrong three times.

- `stat_delta(id, hour_utc, session_id, modality, surface, is_voice, repo_id, agent_id, wingman,
  turns, chars)` - one row per **changed bucket**, not per fold. Post-cutover only.
- `session_highwater(session_id, modality, surface, turns, chars)` - live operational state. The
  idempotent fold. **Starts empty** under the new premise, which is why day one lands with a lump.
- `agent_driven_delta`, `agent_driven_highwater` - the agent-to-agent lane (#1636), in **their own
  tables**. They must never enter the human totals, and a separate table makes that impossible rather
  than a rule every query must remember.
- `repo_identity(repo_id, repo_display)`, `agent_identity(agent_id, agent_display)` - surrogate
  integer identity, resolved in C#.
- `repo_session`, `agent_session`, `wingman_session` - all-time distinct sets, deliberately never
  pruned.
- `agents_seeded(session_id)` - the #1633 back-fill guard. **Live behaviour, not scaffolding.**
- `meta(name, value)` - runtime scalars. Today only `agents_since_utc`.

### The three decisions worth inheriting

1. **`repo_id` and `agent_id` are surrogate INTEGERS, never a repository or agent string in any form,
   raw or folded.** The dictionaries being replaced group with `StringComparer.OrdinalIgnoreCase`
   while SQLite's default collation is case-sensitive `BINARY`, so a raw string column would split
   what the code merges - silently, and only weeks later, since the live store holds no case collision
   today. The obvious repair - a *folded* string column - **cannot be built**: it needs a normalizer
   exactly equivalent to `OrdinalIgnoreCase`, and no such function exists. A comparer is not a
   normalizer. `ToLowerInvariant` is forbidden and can change a string's length at `U+0130`;
   `ToUpperInvariant` is merely close enough to "almost certainly never" bite. With a surrogate id
   there is no folded string, an in-memory `Dictionary<string, long>` built with that same comparer
   resolves display to id, and parity holds **by construction rather than by care**.
2. **`is_voice` is stored, not derived.** `_totals` is keyed case-sensitively while the voice test is
   case-insensitive. Storing the flag preserves that asymmetry rather than reproducing it in SQL.
3. **`wingman` records `SessionDto.VoiceMode` at fold time and is NOT `modality = 'voice'`.** A turn
   **typed** while voice mode is on is a wingman turn. `VoiceMode` must be read **once** per fold and
   passed as a required argument to the row-emitting code, so no row can derive the flag from its own
   modality.

## The premise change

Mid-mission the owner ruled that **the old numbers are not carried across**. That deleted the
import, the legacy reader, the parity check, the baseline tables, and acceptance criteria 1 and 6 -
the hardest and most dangerous half of Phase 1. New first-run behaviour: **rename
`gateway-input-stats.json` aside, unread. Rename, never delete.**

Two things nearly went with it that must not, and both are recorded here because they are behaviour
wearing migration scaffolding's clothes:

- **`agents_since_utc`** is stamped at **runtime** on first observation. It is not history. Deleting
  it with the baselines would have made the Agents page silently imply its numbers reconcile with the
  totals when they do not.
- **`agents_seeded`** looks like dead weight on a fresh database, because the first-fold back-fill
  contributes nothing when high-water is empty. But `session_highwater` **persists across a
  restart**, so without it the back-fill fires again with real turns and **doubles every agent's
  numbers**.

The filter that catches both: *is this a stored historical number, or something the running product
does?* The owner's ruling is about **data**, not **behaviour**.

**The most dangerous remaining delete is `PRAGMA user_version`.** "The import is gone, so the
migration machinery goes with it" is a sentence someone could say and it would sound reasonable.
Delete the import and the mission gets simpler; delete `user_version` and **the mission has no reason
to exist** - it becomes a lateral move from one store that loses data on a shape change to another
one. The danger zone is anything whose *name* sounds like migration.

## The ruling for whoever builds the fold

**The agent tally needs its OWN `agent_delta` table; `stat_delta` must NOT carry `agent_id`**
(Architect ruling, `f8f1bbd8`).

`AttributeToAgentLocked` has two call sites: the ordinary delta path (`:460`, same delta that feeds
the totals) and the first-fold back-fill (`:395`, which attributes prior high-water with **no totals
counterpart**). So the agent tally is not derivable from the same deltas as the totals. Deriving it
from `stat_delta` has **no correct behaviour** if the back-fill ever fires: emitting a row inflates
the totals (those turns are already in them), and omitting one leaves the agent tally short. Two
wrong answers and no right one is not a trade-off - it is a schema that cannot express the situation.

The cost is real and was accepted: `stat_delta` cannot answer turns-by-agent-by-hour. Carrying
`agent_id` would advertise a cross-product the code does not maintain.

## The incident, recorded in full

A full test-suite run drove the then-current import against the owner's **live**
`gateway-input-stats.json` and **renamed it aside**. Nothing was lost - verified, not assumed: his
store is healthy at 1463 turns / 593,268 characters. But **only by luck**: the running Gateway held
its state in memory and rewrote the file on its next save. A Gateway restart inside that window would
have found no file, started empty, and saved empty over it.

Root cause: `GatewayHost` resolves store paths from `CcStorage.Root()` when given none, and 26 of 48
test files pass none. **The exposure was never about statistics** - that root holds `missions.json`
(live fleet state, including this mission's own record), `cronjobs.json` and `keyvault.json`. The
import did not create the exposure; it added the first operation that **moved** a file rather than
overwriting it with equivalent content.

This was the **second** instance of the class. Issue #322 fixed the same bug - a test wrote into the
real Director instances directory and painted a phantom Director in the owner's live Cockpit - and
scoped the fix to the one directory that had been noticed. **A fix scoped to the instance you noticed
leaves the class alive, and the class comes back worse.**

`48aad7ad` redirects `CcStorage.Root()` for the whole test assembly. The hazard was already known and
already mitigated - by a **convention**, followed by 22 of 48 sites. A convention followed by fewer
than half the sites that need it is not a mitigation, it is a folk memory.

## Open unknown - do not close this

**One test failure in 2435, never identified.** A full Gateway run reported 1 failure; the immediate
rerun was green, and the failing run's results were not captured, so it could not be diagnosed. The
plausible explanation is an orphaned `testhost` that had to be cleared mid-run - and *plausible* is
exactly how a real defect gets waved through, so it is **not** recorded as resolved. It has not
recurred across several full runs since.

The real defect was in the evidence chain: only the green rerun produced a `trx`. Runs now use
`--logger trx` so a failing run's results survive. **A thing that happened once and was never
explained is not the same as a thing that did not happen.**

## What this mission cost, and what it caught

Twelve brief revisions before a line of code. Four defects found in an approved document before any
code was written, all accepted. Three separate occasions where a check could not see the thing it was
looking for and reported that the thing was not there:

- `Assert.DoesNotContain("repo", keys)` is an exact-element check and would have passed against
  `repo_raw`.
- A red-watch that died on a `KeyNotFoundException` before reaching its assertion - it went red for a
  reason that does not generalise.
- A `grep` whose filter excluded comment lines, when every surviving reference was a comment.

All three are one failure. **Empty output is not evidence; it is evidence only once you have shown
the check CAN produce non-empty output for the case you care about.** Red-watch your grep the same
way you red-watch your test: point it at a case you know exists and watch it print. If it cannot
print, it was never searching.
