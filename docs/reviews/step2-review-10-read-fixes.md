# Step 2 Review 10 - read projection fixes

Reviewed target: `36f3361d4..175cccae6`.

Verdict: **BLOCKED - all 7 prior findings are genuinely FIXED, but the fixture fix introduced 1 new Medium regression-test defect.** Production still compares the repository leaf name correctly. The new defect is that all three parity tests now remain green if that comparison is changed to the wrong repository field.

## Scope and moving-ref note

**PROVED BY RUNNING.** At assignment and at the start of this review, `origin/nosqlite-stats-w2-model` resolved to `36f3361d4`; `git merge-base --is-ancestor 36f3361d4 175cccae6` returned success, and `git rev-parse 175cccae6~6` still returns exactly `36f3361d4`. The immutable reviewed range contains six read-port commits and changes only the five expected read-port files.

The remote-tracking ref advanced while this review was running: reflog records pushes of `831a3822e` at 16:28 and `a30ae3c66` at 16:36, after `175cccae6` was committed at 16:13. Therefore the literal moving range `origin/nosqlite-stats-w2-model..175cccae6` no longer describes the assigned change at report time. That later upstream movement is not work lost by either reviewed rebase, but integration must account for those two later worker-2 commits.

## Prior findings

### 1. FIXED - the reviewed branch is based on the assigned worker-2 head

**PROVED BY RUNNING.** `36f3361d4` is the exact sixth parent of `175cccae6`. `git range-diff 96d86e0e5..33d77c393 36f3361d4..175cccae6` matched all four original read-port commits and both review-fix commits. It showed only the deliberately omitted namespace commit `ac28da979`. Worker 2 already contains that correction as `15bda2237`, and `git diff 36f3361d4 --` over the two affected test files returned no difference. The final immutable range touches only the read port, not adoption, migrations, model snapshots, or worker-2 evidence.

The two commits pushed to worker 2 after this review started are called out in the scope note rather than misreported as rebase loss.

### 2. FIXED - interrupted migration history is refused inside containment

**PROVED BY RUNNING.** I directly ran `Adopt_HistoryTableWithoutTheBaseline_IsRefusedRatherThanCertifiedAsTracked` from the current source. It built the real version-5 store, added Entity Framework's empty history table, and passed only after adoption returned `NotAdoptable` / `StoreSchemaIncomplete`, named the interrupted state, and left the store fingerprint unchanged.

### 3. FIXED - a version-5 store missing a required column is refused

**PROVED BY RUNNING.** I directly ran `Adopt_AVersion5StoreMissingAColumn_IsRefused`. The probe removed `stat_delta.chars` from a real version-5 store and passed only after adoption refused the store and named `chars`.

### 4. FIXED - the model retains all version-5 tenant defaults

**PROVED BY RUNNING.** I directly ran `ALaterModelDrivenRebuild_KeepsTheTenantDefaultAndEveryExpectedColumn`. The forced SQLite table rebuild retained `tenant DEFAULT 'local'`, kept it non-null, and preserved every expected column across all sixteen tables.

### 5. FIXED - the fixture now detects the culture-aware comparer mutation

**PROVED BY RUNNING.** On the unmodified head, all three `GatewayStatsReadParityTests` passed. I changed only the repository final tie-break from `CompareOrdinal` to `StringComparison.CurrentCulture`; all three tests failed. After reverting that exact mutation, the same three test bodies all passed again.

I also exercised the boundary of the fix:

- A probe invoked `AssertTheOrdinalTieBreakIsProven` with the degenerate tied pair `aaa` / `zzz`; it rejected the pair separately for `RepoTotals`, `AgentTotals`, `ModelTotals`, and `TokenSpendByModel` with the fixture-refused failure.
- `Zebra` / `apple` has the opposite sign under `InvariantCulture`, `OrdinalIgnoreCase`, and a reversed ordinal comparison, so each listed mutation changes the observed order. A runtime scan of all 915 installed .NET cultures found none whose ordinary culture comparison agreed with ordinal for this pair.
- The four production guard call sites use the actual numeric rank keys and actual displayed tie-break names for all four ranked projections.

The new finding below is a different axis: the fixture proves the comparer family, but the replacement strings accidentally stopped proving which repository field is compared.

### 6. FIXED - the statement counter's narrowed contract is true and has no stale consumers

**PROVED BY RUNNING AND READING.** Repo-wide search found no remaining `StatementsExecuted` reference and no consumer of `RawStatementsExecuted`; its only references are the property and the three raw helpers. A runtime probe observed:

- startup raw count: 9;
- all twelve read projections: delta 0, as the new contract explicitly says;
- one real fold: delta 17;
- the identical idle observation: delta 0.

Within the class, every raw command is created in one of the three incrementing helpers. Entity Framework reads are explicitly excluded in both the property contract and plumbing comment. The counter is no longer presented as total database traffic.

### 7. FIXED - the membership-mirror comment now describes the query truthfully

**INFERRED BY READING.** The replacement says both joins are whole-table reads across every tenant, explains that startup has no request tenant to filter by, and limits the joins' effect to attaching the owning tenant and dropping orphans. That exactly matches the two unfiltered joins. It makes no tenant-scaling claim.

## New finding

### NEW 1. Medium - the comparer-sensitive fixture lost its proof that repositories rank by leaf name

**File/line:** `src/CcDirector.Gateway.Tests/Stats/GatewayStatsReadParityTests.cs:297`; `src/CcDirector.Gateway.Tests/Stats/GatewayStatsReadParityTests.cs:299`; intended production comparison at `src/CcDirector.Gateway/Stats/GatewayInputStatsAggregator.cs:1143`.

**PROVED BY RUNNING (mutation).** I temporarily changed the correct production tie-break from:

```csharp
string.CompareOrdinal(a.RepoName, b.RepoName)
```

to:

```csharp
string.CompareOrdinal(a.Repo, b.Repo)
```

and ran the exact three parity-test bodies. All three passed.

The new rows are `Zulu/Zebra` (leaf `Zebra`) and `alpha/apple` (leaf `apple`). Both the full repository keys and their leaf names order in the same ordinal direction, so comparing the wrong field produces the expected rendered order and also satisfies the new shape guard.

This blind spot was introduced by the fix. Under the same wrong-field mutation, a focused probe using the previous rows `zzz/aaa` and `aaa/zzz` produced different orders: the ported reader returned `zzz, aaa` while the frozen reader returned `aaa, zzz`. The old fixture's inverted prefixes accidentally protected the leaf-name contract; the new comparer-sensitive replacement aligned prefixes and leaves and removed that protection.

Production is currently correct. The defect is in the regression proof: a plausible one-field edit changes the specified ranking semantics while every test named as its guard stays green.

## Standing-question audit

- **Quantity asserted while the breaking quantity is absent:** the new finding is the concrete case. The guard asserts the ordinal order of `RepoName`, but the fixture gives `Repo` the same order, so it cannot tell which field production used.
- **Fixture incapable of exhibiting its named defect:** the four culture-aware comparer mutations are now exhibited. The repository fixture is still incapable of exhibiting a full-key-versus-leaf-key mistake, despite the production contract naming the leaf.
- **Assertion misses a failure the fixture can produce:** I found no additional case. The deliberate one-number damage still fails and names `Turns`; tenant-scope accessors returned no foreign rows in all five direct scope tests; the degenerate comparer guard rejected all four ranked projections.
- **Read-port completeness:** every public projection in scope reads through Entity Framework or an Entity-Framework-populated mirror. Remaining raw `Read` calls are startup state needed by the raw write/fold path, not one of the twelve projections. All five tenant-scope test bodies passed directly.

## Executed evidence

- Clean parity: 3/3 passed.
- `CurrentCulture` repository mutation: 0/3 passed; all three failed on the flipped `apple` / `Zebra` order.
- Reverted parity: 3/3 passed.
- Degenerate `aaa` / `zzz` guard probes: 4/4 rejected, one for each ranked projection.
- Installed-culture scan: 915 checked, 0 agreed with ordinal for `Zebra` / `apple`.
- Wrong repository-field mutation: 3/3 parity tests passed, proving NEW 1.
- Previous-fixture comparison under that same mutation: mismatch reproduced.
- Critical worker-2 regression bodies: 3/3 passed (interrupted history, missing column, tenant-default rebuild).
- Tenant-scope bodies: 5/5 passed.
- Current test-project build after mutation cleanup: succeeded with 0 warnings and 0 errors.
- Final source hash for `GatewayInputStatsAggregator.cs` matched the index; the only worktree change is this report.
