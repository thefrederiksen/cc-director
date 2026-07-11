# Build spec - Per-repository default browser+profile (GitHub devthrottle #1112)

This is the SHARED spec. Both builds - the single long session and the
architect/manager/workers fleet - get this exact file as their only instruction and
nothing else. It states WHAT to build and the finish line, and gives the factual code
pointers both sides start from. It deliberately does NOT prescribe the step-by-step HOW,
so the two implementations are free to differ (that lets us compare quality honestly).

Base commit (both worktrees): origin/main `6da36415`.

---

## What to build

Add a per-repository "Open in Browser" default that overrides the current application-wide
default. When a link is opened from a session that belongs to a repository, DevThrottle
should prefer that repository's chosen browser+profile.

Resolve a plain click on "Open in Browser" in this order:

1. Repository default - the browser+profile remembered for the repository the session
   belongs to.
2. Application-wide default - the existing global `browser.default`.
3. Operating-system default browser - when neither of the above is set.

If a repository has no repository default, behaviour is exactly what it is today.

## Why (context, not instructions)

The user runs multiple browser profiles mapped to different identities (a personal Chrome
profile, a work Edge profile). Different repositories belong to different identities. Links
from a work repo should open in the work profile automatically. One global default can't do
that; a repository-scoped default makes the correct identity automatic per project.

## The user-visible intent problem to solve

Today a plain click on a specific profile in the submenu BOTH opens the link AND saves that
choice as THE global default (`OpenInBrowserProfile` -> `BrowserDefaultStore.Save`). With a
repository level added, the user must be able to express which one they are setting: the
default for THIS repository (the common case) versus the application-wide default. The exact
menu interaction is yours to design; keep the one-off submenu choices working.

## Factual code pointers (current, verified at the base commit)

- `src/CcDirector.Core/Browsers/BrowserDefaultStore.cs` - static store. `Load()`/`Save()`
  read/write `config.json` at `browser.default` (`{exePath, profileFolder}`) via
  `CcDirectorConfigService.ReadRaw()` / `MergePatch()`. `ResolveBrowser(exePath)` maps a
  stored exe back to an installed `BrowserInfo` and THROWS if it is gone (no silent
  fallback - keep that behaviour).
- `src/CcDirector.Terminal.Avalonia/LinkContextMenuBuilder.cs` - the shared menu.
  `LinkMenuContext.RepoPath` already carries the session's repository path.
  `BuildOpenInBrowserMenuItem` builds the item; a plain click calls `OpenInBrowserDefault`;
  a submenu profile click calls `OpenInBrowserProfile` (which is what saves the global
  default today). This menu is shared by the terminal and the History tab.
- Config plumbing: `CcDirectorConfigService` (`ReadRaw`, `MergePatch`) preserves other
  config sections - use it, do not hand-write config.json.

## Repo rules that apply (from the product repo)

- No fallback programming. If a remembered browser is gone, surface a clear error - do not
  silently open a different one (the existing `ResolveBrowser` throw is the pattern).
- ASCII only in all output and logs. Keep the existing `FileLog.Write` logging style.
- Match the surrounding code's conventions.

## Definition of done (both builds must hit ALL of these)

1. A repository can have its own remembered browser+profile, stored durably (survives an app
   restart) and keyed by repository path, without disturbing the existing global
   `browser.default`.
2. A plain click on "Open in Browser" from a session in a repository that HAS a repository
   default opens the link in that repository default.
3. A repository with NO repository default falls back to the application-wide default, then
   the OS default - i.e. today's behaviour is unchanged for repos that never set one.
4. The user can set a repository default and can still set/keep the application-wide default;
   the intent is unambiguous in the menu.
5. The one-off submenu (System default, each installed browser, each profile) still works.
6. `dotnet build` of the solution succeeds with no new warnings introduced by this change.
7. Unit test(s) cover the new resolve order (repo default -> global -> OS) against the store,
   and the existing tests still pass.
8. A short PROOF.md in the worktree root: what changed (files), how the resolve order was
   verified (test output pasted), and any design decision made on the menu interaction.

## Out of scope

- Any UI beyond the existing context menu.
- Syncing the repository default anywhere off the machine.
- Changing the global-default behaviour for repos that never set a repository default.
