# PROOF - Per-repository default browser+profile (devthrottle #1112)

Branch: `exp/ctxtax-multi`  (base commit origin/main `6da36415`)
Built by the DevThrottle multi-agent method: one Architect, one Manager, three Workers on
DISJOINT files sharing a single checkout.

## Files changed

1. `src/CcDirector.Core/Browsers/BrowserDefaultStore.cs`
   Added the store + resolve layer. New public methods, existing members untouched:
   - `BrowserDefault? LoadForRepo(string repoPath)` - reads
     `browser.repoDefaults.<canonical repo path>`.
   - `void SaveForRepo(string repoPath, BrowserDefault value)` - writes ONLY under
     `browser.repoDefaults.<key>` via `CcDirectorConfigService.MergePatch`, so the global
     `browser.default` and every other config section deep-merge intact.
   - `BrowserDefault? Resolve(string? repoPath)` - resolve order: (1) repository default,
     (2) global default, (3) null (caller uses the OS default). A null/empty repoPath resolves
     straight to the global `Load()`.
   - private `NormalizeRepoKey` - the one canonical path key: backslash -> forward slash, trim
     trailing slash, lower-invariant. Used by BOTH save and load so the rule lives in one place.

2. `src/CcDirector.Terminal.Avalonia/LinkContextMenuBuilder.cs`
   Menu interaction layer. Plain click on "Open in Browser" now calls
   `BrowserDefaultStore.Resolve(context.RepoPath)`; a null result opens the OS/system default
   exactly as before the feature existed. The existing no-fallback throw (a remembered browser
   that is gone surfaces a clear error, never silently opens a different browser) is preserved.

3. `src/CcDirector.Core.Tests/Browsers/RepoBrowserDefaultStoreTests.cs`  (new)
   16 tests covering the resolve order, disk durability, global/unrelated-key isolation, and
   path-key normalization. Follows the existing `SidebarConfigTests` pattern - isolates
   `CC_DIRECTOR_ROOT` to a temp dir per test and runs in the `CcStorageRoot` collection. Depends
   on no installed browser (never calls `ResolveBrowser`/`DetectBrowsers`), so it is
   deterministic on any machine.

## Menu-interaction design decision (Task B)

The user-visible intent problem: before this change, clicking a specific profile in the submenu
BOTH opened the link AND silently saved that profile as THE global default. With a repository
level added, a single click could no longer express whether the user meant "this repository" or
"application-wide".

Decision: each profile leaf expands into three explicit actions instead of acting on a single
click:

- "Open once" - opens in that browser+profile and remembers NOTHING (keeps the one-off submarine
  choices of Definition of Done #5 working - System default and each browser/profile still open).
- "Set as default for this repository" - opens AND `SaveForRepo(context.RepoPath, ...)`. This
  entry is OMITTED when `context.RepoPath` is null/empty (a session with no repository has no
  repository to set a default for).
- "Set as application-wide default" - opens AND saves the existing global `browser.default`.

"System default" and the per-browser / per-profile structure are kept. The result: a profile
pick can never silently overwrite the global default; the user always states which default (if
any) a pick should set. The parent-header plain click continues to reopen the resolved default
(repository -> global -> OS) via a tunnel-phase press handler on the parent header rectangle.

## Verification

### Full solution build - no new warnings (Definition of Done #6)

```
dotnet build cc-director.sln
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### Full Core.Tests suite - nothing regressed (Definition of Done #7)

```
dotnet test src/CcDirector.Core.Tests/CcDirector.Core.Tests.csproj
Passed!  - Failed:     0, Passed:  2985, Skipped:     8, Total:  2993, Duration: 3 m 15 s
```

(The 8 skips are pre-existing environment-gated tests unrelated to this change.)

### Resolve order proven against the store (Definition of Done #7)

```
dotnet test src/CcDirector.Core.Tests/CcDirector.Core.Tests.csproj --filter "FullyQualifiedName~RepoBrowserDefaultStore"

Passed  Resolve_RepoDefaultPresent_ReturnsRepoDefaultNotGlobal          -> repo default wins over global
Passed  Resolve_NoRepoDefaultButGlobalPresent_ReturnsGlobalDefault      -> falls through to global
Passed  Resolve_NeitherRepoNorGlobalPresent_ReturnsNull                 -> null => caller uses OS default
Passed  Resolve_NullRepoPath_ReturnsGlobalDefault                       -> no repo resolves to global
Passed  SaveForRepo_RoundTripsThroughDisk                               -> survives a re-read (app restart)
Passed  SaveForRepo_DoesNotDisturbGlobalDefault                         -> global browser.default untouched
Passed  SaveForRepo_PreservesUnrelatedConfigKeys                        -> other config keys untouched
Passed  SaveForRepo_TwoRepos_KeepIndependentDefaults                    -> per-repo entries independent
Passed  LoadForRepo_PathVariantOfSameRepo_HitsSameEntry (x4)            -> slash/case/trailing all one key
Passed  Resolve_PathVariantOfSameRepo_HitsSameRepoDefault (x3)          -> variant still hits the repo entry
Passed  SaveForRepo_PathVariant_OverwritesSameEntryNotAddsSecond        -> variant updates, never duplicates

16 passed, 0 failed.
```

The three lines proving the exact Definition-of-Done resolve order (repository default -> global
default -> OS default) are the first three above.

## Definition of Done - all met

1. Per-repository remembered browser+profile, durable (disk round-trip test), keyed by repository
   path, without disturbing the global `browser.default`. YES
2. Plain click from a repo that HAS a repository default opens that repository default. YES
   (`OpenInBrowserDefault` -> `Resolve(RepoPath)`).
3. Repo with NO repository default falls back to global, then OS default; today's behaviour
   unchanged. YES (Resolve returns global, then null -> `OpenSystemDefault`).
4. User can set a repository default AND still set/keep the application-wide default; intent is
   unambiguous. YES (three explicit per-profile actions).
5. One-off submenu (System default, each browser, each profile via "Open once") still works. YES
6. Solution build succeeds with no new warnings. YES
7. Unit tests cover the new resolve order and the existing tests still pass. YES
8. This PROOF.md. YES
