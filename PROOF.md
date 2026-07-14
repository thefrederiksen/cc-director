# PROOF - Per-repository default browser+profile (GitHub devthrottle #1112)

Base commit: origin/main `6da36415`. Build: single long session, branch `exp/ctxtax-single`.

## What changed (files)

1. `src/CcDirector.Core/Browsers/BrowserDefaultStore.cs`
   - Added a second level of default beside the existing application-wide `browser.default`:
     a per-repository default stored at `browser.repoDefaults[<repoKey>]`, same
     `{exePath, profileFolder}` shape.
   - New public methods:
     - `LoadForRepo(string repoPath)` - the repository's own remembered default, or null.
     - `SaveForRepo(string repoPath, BrowserDefault value)` - persists it via
       `CcDirectorConfigService.MergePatch`, so the global default and other repositories'
       entries are preserved.
     - `Resolve(string? repoPath)` - the single resolve-order authority: repository default,
       then application-wide default, then null (meaning "use the operating-system default").
   - Private helpers: `ReadDefaultObject` (shared parse of an `{exePath, profileFolder}` node,
     factored out of `Load`) and `NormalizeRepoKey` (unify separators to backslash, trim
     trailing separators, lowercase on Windows where paths are case-insensitive).
   - The existing `Load`, `Save`, and `ResolveBrowser` (with its no-fallback throw) are
     unchanged in behaviour.

2. `src/CcDirector.Terminal.Avalonia/LinkContextMenuBuilder.cs`
   - A plain click on "Open in Browser" now calls `BrowserDefaultStore.Resolve(context.RepoPath)`
     instead of `Load()`, so it follows the repo -> global -> OS order. A repo with no
     repository default is unchanged: `Resolve` falls through to the global default, then the OS
     default.
   - The submenu that used to conflate open+save is split so intent is unambiguous. Each profile
     now expands into explicit choices:
     - "Open once" - opens in that browser+profile and remembers nothing.
     - "Set as default for this repository" - opens and saves the repository default (shown only
       when the link belongs to a repository, i.e. `context.RepoPath` is set).
     - "Set as application-wide default" - opens and saves the global default (the old
       profile-click behaviour, now explicit).
   - The single old `OpenInBrowserProfile` (which both opened and silently saved the global
     default) is replaced by `OpenInBrowserProfileOnce`, `OpenInBrowserProfileForRepo`, and
     `OpenInBrowserProfileAppWide`. "System default" is unchanged (one-off, saves nothing).

3. `src/CcDirector.Core.Tests/Browsers/BrowserDefaultStoreTests.cs` (new)
   - Covers the resolve order and the two-level store against real config.json round-trips in an
     isolated `CC_DIRECTOR_ROOT`.

## Design decision on the menu interaction

The spec calls out that today a plain profile click BOTH opens and saves the global default, and
that with a repository level added the user must be able to say WHICH default they are setting.

Chosen design: enumerate installed browsers and their profiles exactly once (as today), but turn
each profile leaf into a tiny action submenu of explicit intents - "Open once", "Set as default
for this repository", "Set as application-wide default". This keeps the browser/profile list
short (listed once, not duplicated per intent), and every click states plainly what it does. The
repository option is hidden when the link has no owning repository, so it is never offered when it
could not work. A plain click on the top-level "Open in Browser" remains the fast path and now
uses the full resolve order. "System default" stays a one-off.

This satisfies the "one-off submenu still works" requirement via "Open once" on every profile and
the unchanged "System default", while making the set-a-default intent explicit and repository-aware.

## How the resolve order was verified (test output)

`dotnet build cc-director.sln -c Debug` -> Build succeeded, 0 Warning(s), 0 Error(s).

Focused run of the new store tests (`--filter FullyQualifiedName~BrowserDefaultStoreTests`):

```
Passed CcDirector.Core.Tests.Browsers.BrowserDefaultStoreTests.Resolve_RepoHasOwnDefault_ReturnsRepoDefault
Passed CcDirector.Core.Tests.Browsers.BrowserDefaultStoreTests.Resolve_RepoHasNoDefault_FallsBackToGlobalDefault
Passed CcDirector.Core.Tests.Browsers.BrowserDefaultStoreTests.Resolve_NeitherRepoNorGlobal_ReturnsNullForOsDefault
Passed CcDirector.Core.Tests.Browsers.BrowserDefaultStoreTests.Resolve_NullRepoPath_UsesGlobalDefaultOnly
Passed CcDirector.Core.Tests.Browsers.BrowserDefaultStoreTests.SaveForRepo_RoundTripsThroughDisk
Passed CcDirector.Core.Tests.Browsers.BrowserDefaultStoreTests.SaveForRepo_DoesNotDisturbGlobalDefault
Passed CcDirector.Core.Tests.Browsers.BrowserDefaultStoreTests.SaveForRepo_TwoRepos_KeepBothIndependently
Passed CcDirector.Core.Tests.Browsers.BrowserDefaultStoreTests.SaveGlobal_DoesNotDisturbExistingRepoDefault
Passed CcDirector.Core.Tests.Browsers.BrowserDefaultStoreTests.SaveForRepo_PreservesUnrelatedConfigSections
Passed CcDirector.Core.Tests.Browsers.BrowserDefaultStoreTests.LoadForRepo_NormalizesSeparatorsSlashAndTrailingSlash
Passed CcDirector.Core.Tests.Browsers.BrowserDefaultStoreTests.LoadForRepo_OnWindowsIsCaseInsensitive
Passed CcDirector.Core.Tests.Browsers.BrowserDefaultStoreTests.LoadForRepo_UnknownRepo_ReturnsNull
Passed CcDirector.Core.Tests.Browsers.BrowserDefaultStoreTests.LoadForRepo_BlankPath_ReturnsNull
Passed CcDirector.Core.Tests.Browsers.BrowserDefaultStoreTests.SaveForRepo_BlankPath_Throws
Passed CcDirector.Core.Tests.Browsers.BrowserDefaultStoreTests.SaveForRepo_StoresUnderBrowserRepoDefaultsSection
```

The three resolve-order cases directly prove the Definition-of-Done order:
- `Resolve_RepoHasOwnDefault_ReturnsRepoDefault` - repo default wins over the global default.
- `Resolve_RepoHasNoDefault_FallsBackToGlobalDefault` - a repo with no default falls to global.
- `Resolve_NeitherRepoNorGlobal_ReturnsNullForOsDefault` - neither set -> null -> OS default.

Full Core test suite (`dotnet test` on `CcDirector.Core.Tests`), confirming nothing regressed:

```
Passed!  - Failed:     0, Passed:  2984, Skipped:     8, Total:  2992, Duration: 3 m 18 s
```

(The 8 skips are pre-existing environment-gated tests unrelated to this change.)

## Definition of done - mapping

1. Repository can have its own remembered browser+profile, durable, keyed by repository path,
   without disturbing `browser.default` - `SaveForRepo`/`LoadForRepo`;
   `SaveForRepo_RoundTripsThroughDisk`, `SaveForRepo_DoesNotDisturbGlobalDefault`.
2. Plain click from a repo that HAS a repo default opens that default - `Resolve` used by
   `OpenInBrowserDefault`; `Resolve_RepoHasOwnDefault_ReturnsRepoDefault`.
3. Repo with NO repo default falls back to global then OS (today's behaviour) -
   `Resolve_RepoHasNoDefault_FallsBackToGlobalDefault`,
   `Resolve_NeitherRepoNorGlobal_ReturnsNullForOsDefault`.
4. User can set a repo default and still set/keep the application-wide default; menu intent is
   unambiguous - the three explicit profile actions.
5. One-off submenu still works - "System default" and "Open once" on every profile.
6. `dotnet build` succeeds, no new warnings - 0 Warning(s).
7. Unit tests cover the resolve order and existing tests pass - see output above.
8. This PROOF.md.

## Repo rules honoured

- No fallback programming: a gone browser still throws via the unchanged `ResolveBrowser`; a
  missing repo/global default returns null meaning "OS default", which is a legitimate state, not
  a hidden failure. `SaveForRepo` throws on a blank repository path rather than silently no-oping.
- ASCII only in all new code, logs, and this document.
- Config writes go through `CcDirectorConfigService.MergePatch` (never hand-written), preserving
  sibling sections - `SaveForRepo_PreservesUnrelatedConfigSections`.
