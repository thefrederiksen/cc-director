# The gate baseline - review findings mission, 1 August 2026

Every run on this mission's branch is judged against the numbers below. A run counts as green only when
BOTH hold, per work item, per project:

1. the TRX `ResultSummary/@outcome` is `Completed`, and
2. the total test count is at or above the baseline recorded here.

**The console summary is not evidence and is never the verdict.** `dotnet test` prints
`Passed! - Failed: 0` for a run that passed everything it managed to START, so a crashed test host
produces a green with a collapsed count that nobody looks at. That shape has already very nearly certified
a change which silently stopped 1,340 tests from running. `scripts\test-local.ps1` now writes a TRX per
project and prints the outcome and the total beside each result, so the pair above is readable without
anyone remembering to go and find it.

## What the baseline was measured on

- **Commit:** `8d92a3958` (`origin/main` at the time the mission worktree was cut), plus `e71f4e99d`, which
  adds the TRX emission to `scripts\test-local.ps1` and changes nothing about which tests run.
- **Tree:** `D:\ReposFred\dt-review-findings`, branch `stats-hosted-serve`, before any product edit.
- **Machine:** SOREN_NORTH, `Debug`, .NET SDK 10.0.302.
- **Run started:** 2026-08-01 10:58 local.
- **Gated live proofs:** NOT configured for this run. `CC_GATEWAY_TEST_PG_CONNECTION`,
  `CC_GATEWAY_TEST_PG_STATS_CONNECTION` and `CC_GATEWAY_DB_CONNECTION` were all unset, so every
  PostgreSQL-gated fact reported SKIPPED. The skipped facts are inside the totals below as
  total-minus-executed, which is why that column is recorded rather than only the passed count: a later run
  WITH the rig up will execute more of the same total, and the count must not read as a regression.

## The numbers

| Project | Outcome | Total | Executed | Passed | Failed | Skipped |
|---|---|---|---|---|---|---|
| CcDirector.Core.Tests | Completed | 4179 | 4171 | 4171 | 0 | 8 |
| CcDirector.Avalonia.Tests | Completed | 353 | 353 | 353 | 0 | 0 |
| CcDirector.Launcher.Tests | Completed | 110 | 110 | 110 | 0 | 0 |
| CcDirector.HostedAgent.Tests | Completed | 88 | 88 | 88 | 0 | 0 |
| CcDirector.Engine.Tests | Completed | 63 | 63 | 63 | 0 | 0 |
| CcDirector.Terminal.Avalonia.Tests | Completed | 24 | 24 | 24 | 0 | 0 |
| **CcDirector.Gateway.Tests** | **Completed** | **5153** | **5113** | **5113** | **0** | **40** |

The six TRX files these rows were read from are kept outside the repository, in this session's scratchpad
at `baseline-trx\`, so the numbers can be re-derived rather than taken on trust.

## How the Gateway row was measured, and why it was measured separately

The first baseline run was STOPPED BY THE HARNESS, not by a failure, while the Gateway suite was still
executing - roughly thirty-two minutes in on a heavily loaded machine, and its test host was killed with
the rest of the process tree. Six projects had already written their TRX; the Gateway suite had not.

It was re-measured against the SAME baseline assemblies, WITHOUT a rebuild, so the row still describes
`8d92a3958`. `CcDirector.Gateway.Tests.dll` was written at 10:58:22 (5,934,592 bytes) and the earliest
product edit of this mission was made at 11:12:12; the whole `bin\Debug\net10.0` directory was verified
to contain nothing newer than 11:00 immediately before the run started, and the directory was copied to
this session's scratchpad first so that a stray rebuild would cost a restore rather than the baseline.
A stale-artifact check was worth doing: five PRE-COMPILE files under `src\CcDirector.Gateway\obj` carried
an 11:30:43 stamp from an editor design-time pass, and no `.dll` or `.pdb` anywhere had moved.

Two operational notes worth keeping, because both cost time:

- **Run it detached.** The re-run went out through `Start-Process`, outside the harness process tree, so a
  harness kill could not take it down the way it took down the first one.
- **Judge liveness by processor time, not by the clock.** The Gateway suite took **1 hour 1 minute** here
  against a typical nine minutes, because several working trees and an unrelated build were competing for
  the machine. Two CPU readings a minute apart showed it working throughout. Elapsed time would have
  called it hung four times over.

The run waited for the machine-wide suite lock rather than queueing behind another working tree's run.

## The gate for W1 is STRONGER than this baseline

An independent inspection made the point that this document's own numbers prove: the run above had
`CC_GATEWAY_TEST_PG_STATS_CONNECTION` unset, so **every hosted statistics acceptance fact reported
SKIPPED**. A green run in that state proves compilation and the self-host controls - it does not prove the
hosted behaviour W1 exists to deliver. Removing the hosted wiring entirely could leave it green.

So the Architect has ruled that W1's gate additionally requires **the six hosted facts to appear in the
TRX as EXECUTED and passed, with the PostgreSQL rig up**, recorded here as evidence. A skipped fact does
not gate this work.

## W1's FINAL gate - GREEN, post-rebase onto b6fbf15c6, with the rig up

Run from a clean tree rebuilt at the committed state, rig `rf` on port 55436, both connection variables
set.

| Project | Outcome | Total | vs baseline | Executed | Failed | Skipped |
|---|---|---|---|---|---|---|
| CcDirector.Gateway.Tests | Completed | 5173 | 5153 (+20) | 5167 | 0 | 6 |
| CcDirector.Core.Tests | Completed | 4196 | 4179 (+17, main added tests) | 4188 | 0 | 8 |
| CcDirector.Avalonia.Tests | Completed | 353 | 353 (=) | 353 | 0 | 0 |
| CcDirector.Launcher.Tests | Completed | 110 | 110 (=) | 110 | 0 | 0 |
| CcDirector.HostedAgent.Tests | Completed | 88 | 88 (=) | 88 | 0 | 0 |
| CcDirector.Engine.Tests | Completed | 63 | 63 (=) | 63 | 0 | 0 |
| CcDirector.Terminal.Avalonia.Tests | Completed | 24 | 24 (=) | 24 | 0 | 0 |

**THE ROWS COME FROM TWO RUNS, AND HERE IS EXACTLY WHICH.** A reader finding two timestamps and no
explanation would reasonably assume something was hidden, so:

- **The six non-Gateway rows** are from the post-rebase run of `cc5bce077`. That run's GATEWAY host
  crashed twenty-one minutes in - 2,671 tests missing under a console line reading `Passed! - Failed: 0`,
  recorded in `evidence-a-false-green-caught-in-the-wild.md` along with the crash fingerprint written down
  BEFORE the re-run and the verdict evaluated against it. The other six were untouched by that crash and
  all reported `Completed` at their expected counts.
- **The Gateway row** is from a later run of `0a4951cc3`, after the round-five fixes.

**Why the split is sound rather than convenient.** `test-local.ps1` starts each project as its OWN
process, writing its own TRX. A Gateway test host cannot corrupt another project's run or its results
file, which is why the crash could not reach the six - and it is the same reasoning that makes it correct
to re-run the Gateway suite ALONE for a change confined to `CcDirector.Gateway.Tests`. The six projects
contain no line that the round-five delta can execute. A single uninterrupted run would buy the
appearance of one artifact, not more evidence, at the cost of an hour on the machine-wide suite lock.

**Why the Gateway suite was re-run in full rather than the changed test alone.** The changed test drops
and rebuilds the `gateway_stats` schema on a rig that every other PostgreSQL fact in that suite shares.
An isolated pass cannot see a break in the other sixty-five facts that use it - the dependency is not in
the code, it is in the database. All 66 PostgreSQL-touching facts are accounted for in this run: 61
passed, and the 5 not executed are the live main-database proofs waiting on a variable this run
deliberately did not set.

**The number that matters is EXECUTED, not total.** The Gateway suite executed 5167 against the
baseline's 5113 - FIFTY-FOUR more tests actually ran - because the baseline run had no rig and skipped
every PostgreSQL-gated fact. Skips fell from 40 to 6. A gate that only compared totals would have shrugged
at +20; the executed count is what shows the hosted work was exercised at all.

**The hosted facts EXECUTED rather than skipped**, which is the whole point of the stronger gate:

- 8 in `HostedStatsServeTests` - serve from PostgreSQL, the production-ingress round trip, a real
  concurrency number, tenant isolation, the two refusals, and the page redirect.
- 8 in `HostedSchemaRefusesAnUnownedRowTests` - omitted refused, empty refused, all-whitespace refused,
  whitespace-inside-a-legal-value refused, a walk over ALL 25 characters `char.IsWhiteSpace` reports
  refusing every one, and three positive controls over the spellings production actually mints (`local`,
  `system`, a GUID).
- 7 in `StatisticsFailureIsContainedOnTheHotPathTests` - three containment facts (roster, hub push, hub
  remove), three uncontained controls, and the log assertion.
- 1 in `ALateStatisticsStoreReachesTheRosterTests` - a store published AFTER route mapping still records
  from the roster.
- 1 in `TheRejectedChainUpgradesToTipTests` - a database at the REJECTED round-three migration state
  upgrades to tip and gains the allowlist.

All twenty-five passed.

**The 6 remaining Gateway skips are unrelated to this work** and are named here so nobody has to guess:
five live main-database proofs waiting on `CC_GATEWAY_DB_CONNECTION` (a different variable from the
statistics one, and not set for this run), and `DT_TEN_3`, which is explicitly a future increment.

### This gate caught a real false green on this mission - see the evidence beside this file

`evidence-a-false-green-caught-in-the-wild.md`, in this directory, records a run that printed
`Passed! - Failed: 0` over a **519-test collapse**, with the TRX `outcome` and `total` the only two
fields in the entire artifact that disagreed. The TRX's own `failed="0"` did not notice, so a gate
checking "no failures" - in the console OR in the TRX - would have called it green.

It is worth reading before anyone proposes simplifying this gate to a failure count. The count comparison
is not bureaucracy; it is the only thing standing between that console line and the hole underneath it.

### Baselines will be EXCEEDED from here, and that is not drift

`origin/main` has since added tests to `CcDirector.Core.Tests` and the setup-engine tests, so those
projects will report totals ABOVE the numbers in the table at the top of this document. **The rule is at
or above, never equal.** A count higher than baseline means main moved; it is only a finding when the
count FALLS, which is what a silently-stopped suite looks like. Do not "fix" a high count by lowering
the baseline.

---

## W2's gate - GREEN, on `9037fc981` plus the prompt-delete erasure

Run detached from a clean tree at `06c79eb5a`, started 2026-08-01 22:22 local, finished 23:08. No
PostgreSQL rig: `CC_GATEWAY_TEST_PG_CONNECTION`, `CC_GATEWAY_TEST_PG_STATS_CONNECTION` and
`CC_GATEWAY_DB_CONNECTION` were all unset, so every PostgreSQL-gated fact reported SKIPPED. **W2's own
acceptance rides on none of them** - `session_history` is the main Gateway database, which the ordinary
suite runs on SQLite - so unlike W1 this item's facts EXECUTE in the ordinary run. That is asserted from
the TRX below, not assumed.

| Project | Outcome | Total | vs baseline | Executed | Failed | Skipped |
|---|---|---|---|---|---|---|
| CcDirector.Gateway.Tests | Completed | 5176 | 5173 (+3, see below) | 5121 | 0 | 55 |
| CcDirector.Core.Tests | Completed | 4196 | 4196 (=) | 4188 | 0 | 8 |
| CcDirector.Avalonia.Tests | Completed | 353 | 353 (=) | 353 | 0 | 0 |
| CcDirector.Launcher.Tests | Completed | 110 | 110 (=) | 110 | 0 | 0 |
| CcDirector.HostedAgent.Tests | Completed | 88 | 88 (=) | 88 | 0 | 0 |
| CcDirector.Engine.Tests | Completed | 63 | 63 (=) | 63 | 0 | 0 |
| CcDirector.Terminal.Avalonia.Tests | Completed | 24 | 24 (=) | 24 | 0 | 0 |

**The five W2 facts show EXECUTED and Passed in the TRX**, which is what the amendment above requires
and what a skip would have hollowed out: `Erasing_clears_all_seven_prompt_derived_columns_resets_the_metadata_and_drops_the_rollups`,
`The_erasure_reaches_only_the_erasing_tenants_rows`, `A_second_erasure_honestly_reports_nothing_to_do`,
`Deleting_the_prompt_history_also_erases_the_copy_the_gateway_derived_from_it`, and
`A_delete_with_nothing_derived_reports_zero_and_still_succeeds`.

### The +3 is +5 minus 2, and the 2 are accounted for exactly

W2 adds FIVE test methods, so a naive reading expects 5178 and gets 5176. Two short is the shape of a
silently-stopped suite, so it was chased rather than shrugged at, and it is fully explained:

- The branch's discovered test set was diffed against `origin/main`'s, from a build of each: **exactly
  the five new methods, and nothing removed.** Discovery, not execution, so it costs a build rather than
  an hour of the machine-wide lock.
- The baseline row of 5173 was measured **with the statistics rig UP**.
  `HostedSchemaRefusesAnUnownedRowTests.A_row_whose_tenant_is_a_spelling_production_mints_is_still_stored`
  is a THEORY with three `InlineData` rows, which expands to three cases when the rig is up and collapses
  to ONE skipped case when it is not. That is the two: 5173 with the rig, 5171 without it, plus five is
  5176. No residue.

**The lesson worth keeping: a theory's row count is part of the total, and gating a theory changes the
total without any test being lost.** Comparing a no-rig run against a rig-up baseline therefore
under-counts by the number of gated theory rows, every time. It is not drift and it is not a collapse -
but it can only be told apart from a collapse by naming which theory and how many rows, which is why the
number above is written out rather than waved at.

### One commit on this branch is later than the gate run

`d73e5d2e3` adds a comment naming the concurrent-ingest window and was written after the run. It is
COMMENT-ONLY - ten added lines, none removed, one file, verified on both sides of the diff - so it
cannot move a test. The gated tree and the landing tree differ by nothing that can execute.

---

## W2's SECOND gate - GREEN, and this is the one that describes what lands

The seal exemption and the generated-SQL assertion are real source changes, so the first gate no longer
described the branch and a second run was owed. Detached, clean tree, started 2026-08-01 23:35,
finished 00:15. Same rig state as the first run: no PostgreSQL connection variables set, so the same 55
PostgreSQL-gated facts skipped, none of them W2's.

| Project | Outcome | Total | vs the first gate | Executed | Failed | Skipped |
|---|---|---|---|---|---|---|
| CcDirector.Gateway.Tests | Completed | 5181 | 5176 (+5, the five new facts) | 5126 | 0 | 55 |
| CcDirector.Core.Tests | Completed | 4196 | 4196 (=) | 4188 | 0 | 8 |
| CcDirector.Avalonia.Tests | Completed | 353 | 353 (=) | 353 | 0 | 0 |
| CcDirector.Launcher.Tests | Completed | 110 | 110 (=) | 110 | 0 | 0 |
| CcDirector.HostedAgent.Tests | Completed | 88 | 88 (=) | 88 | 0 | 0 |
| CcDirector.Engine.Tests | Completed | 63 | 63 (=) | 63 | 0 | 0 |
| CcDirector.Terminal.Avalonia.Tests | Completed | 24 | 24 (=) | 24 | 0 | 0 |

The arithmetic closes exactly this time - +5 for five added facts, no residue - because both runs were
measured in the SAME rig state, which is the whole lesson of the section above.

**All TEN W2 facts EXECUTED and passed**, read from the TRX by name: the five from the first gate plus
`A_sealed_farewell_survives_the_erasure_but_its_prompt_line_does_not`,
`A_row_that_never_got_a_summary_kind_is_still_reset`, `The_reported_count_is_rows_changed_counted_once_each`,
`Every_statement_the_erasure_issues_names_the_tenant_in_its_where_clause`, and its control
`The_same_statement_without_the_filter_has_no_tenant_predicate_which_is_how_we_know_the_check_works`.
Zero non-passed results that were not skips.

### The rebase onto v1.9.6, and why it did NOT earn a third gate

Main moved to `546998b53` (the v1.9.6 release) while this ran. Ruling 7a's refinement governs: rebase to
keep ancestry current, re-gate only on plausible INTERACTION. The release was checked here rather than
taken on trust - two files, a `<Version>1.9.5</Version>` to `1.9.6` line in `Directory.Build.props` and a
new release-notes page, nothing under `src`, `scripts` or `tools`. The props file was read rather than
just listed, because it feeds every project's build and a property like `LangVersion` would NOT be inert.
A grep for any source or test asserting a version string found only generated `obj/` AssemblyInfo files.

So the branch was rebased and NOT re-gated, and the claim is checkable rather than asserted:

```
git diff <gated tip> <rebased tip> -- src/ scripts/ tools/   ->  empty
git diff <gated tip> <rebased tip>                           ->  the release's 2 files, +40 -1
git merge-base --is-ancestor 546998b53 HEAD                  ->  yes
```

**The tree that was measured and the tree that lands are byte-identical everywhere a test can reach.**
The rebased tree was also built once, because `Directory.Build.props` is a build input and a malformed
one would break the build without any test being involved.

---

## W2's THIRD gate - GREEN, after the inspection rejection

The inspection rejected W2 with four findings, one of them a correctness defect (an erasure could be
undone by a writer that started before it). Fixing it added a table, a migration for each provider, a
guard in three writers, and five facts, so the second gate stopped describing the branch and a third run
was owed. Detached, clean tree, 2026-08-02 00:59 to 02:24. Same rig state as the other two runs.

| Project | Outcome | Total | vs the second gate | Executed | Failed | Skipped |
|---|---|---|---|---|---|---|
| CcDirector.Gateway.Tests | Completed | 5186 | 5181 (+5, the five race facts) | 5131 | 0 | 55 |
| CcDirector.Core.Tests | Completed | 4196 | 4196 (=) | 4188 | 0 | 8 |
| CcDirector.Avalonia.Tests | Completed | 353 | 353 (=) | 353 | 0 | 0 |
| CcDirector.Launcher.Tests | Completed | 110 | 110 (=) | 110 | 0 | 0 |
| CcDirector.HostedAgent.Tests | Completed | 88 | 88 (=) | 88 | 0 | 0 |
| CcDirector.Engine.Tests | Completed | 63 | 63 (=) | 63 | 0 | 0 |
| CcDirector.Terminal.Avalonia.Tests | Completed | 24 | 24 (=) | 24 | 0 | 0 |

**All FIFTEEN W2 facts EXECUTED and passed**, read from the TRX by name, and no result in the whole suite
was anything other than Passed or skipped. `origin/main` is an ancestor of the tip, so no rebase was owed
this round.

**The Gateway suite took 1 hour 23 minutes** against a typical nine, because a release worktree and other
work were competing for the machine. Two processor readings a minute apart showed it working throughout.
That is the fourth time this week elapsed time alone would have called a healthy run hung.

### The new facts are CONCURRENT, which is why the previous two gates could be green over a real defect

Every W2 fact before this round was sequential, and all ten stayed green while a delete could be undone
seconds after it succeeded. The defect was not subtle once seen - a summarisation in flight writes the
prompt-derived fields back, and the metadata reset is what re-arms it - but no sequential fact can see it,
because it needs two things happening at once.

The fact that catches it holds a REAL summarisation open across a REAL delete: the model call blocks on a
completion source until the erasure has run, then releases. With the guard removed it fails with the
member's erased summary back in the column. **A test that cannot express the timing cannot fail on it, and
a suite full of such tests reads exactly like a suite that has checked.**

---

## W2's FOURTH gate - GREEN, after the round-two rejection

The round-two inspection rejected W2 again: three of its four findings were ways erased material could
still come back. Fixing them changed the mechanism substantially - the watermark comparison moved INTO
the writes, old material is now refused at ingest, and the sealed-row exemption was reversed - so a fourth
run was owed. Detached, clean tree, 2026-08-02 03:04 to 03:33. Same rig state as the other three.

| Project | Outcome | Total | vs the third gate | Executed | Failed | Skipped |
|---|---|---|---|---|---|---|
| CcDirector.Gateway.Tests | Completed | 5194 | 5186 (+8) | 5139 | 0 | 55 |
| CcDirector.Core.Tests | Completed | 4196 | 4196 (=) | 4188 | 0 | 8 |
| CcDirector.Avalonia.Tests | Completed | 353 | 353 (=) | 353 | 0 | 0 |
| CcDirector.Launcher.Tests | Completed | 110 | 110 (=) | 110 | 0 | 0 |
| CcDirector.HostedAgent.Tests | Completed | 88 | 88 (=) | 88 | 0 | 0 |
| CcDirector.Engine.Tests | Completed | 63 | 63 (=) | 63 | 0 | 0 |
| CcDirector.Terminal.Avalonia.Tests | Completed | 24 | 24 (=) | 24 | 0 | 0 |

**The +8 accounts exactly**: seven facts in the new cross-process and retry file, plus one, because the
seal fact split in two when the exemption was reversed (sealed rows are erased; a seal arriving after a
delete is refused). **All 23 erasure facts EXECUTED and passed**, and no result anywhere in the suite was
anything other than passed or skipped. `origin/main` is an ancestor of both branch tips.

### What the previous three green gates could not see

Every erasure fact through gate three used ONE store instance, so the instance write lock excluded the
very interleaving the mechanism was supposed to survive - and the hosted Gateway is documented to run two
containers at once during a slot swap. The suite was green over a check-then-write race the whole time.

The new facts use **two independent store instances over one database** and drive the interleave
deliberately: process B decides, process A erases and stamps, then B writes. They also drive the REAL
summariser over the REAL prompt log for the retry path, because the earlier "post-delete" control appended
deliberately NEW material and so never exercised a retry at all.

**Two rounds of green over the same defect, from tests that each looked reasonable.** The pattern in both:
the test could not express the condition it was supposed to rule out - one process cannot show a
two-process race, and freshly-appended material cannot show a retry.

### Two commits on this branch post-date their gate, both comment-only

`d73e5d2e3` (the concurrent-ingest window) and `2c751cf77` / `c379c02c8` after the rebase (why the three
statements are not in one transaction). Each is one file, additions only, every added line a comment,
verified on both sides of the diff. Neither can move a test.
