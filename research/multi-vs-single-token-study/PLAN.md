# Architect plan - Per-repository default browser+profile (devthrottle #1112)

Read `SPEC.md` at the repo root first - it is the source of truth for WHAT to build and
the Definition of Done. This plan decomposes that work into three INDEPENDENT tasks on
DISJOINT files so multiple workers share one checkout without conflicting.

Base commit: origin/main `6da36415`. Branch to commit on: `exp/ctxtax-multi` (already
checked out). Repo path: `D:/ReposFred/devthrottle-ctxtax-multi`.

Repo rules that apply to EVERY task:
- No fallback programming. If a remembered browser is gone, surface the error - the
  existing `BrowserDefaultStore.ResolveBrowser` throw is the pattern. Do NOT silently open
  a different browser.
- ASCII only in all code, comments, logs, and output. Keep the `FileLog.Write` style.
- Match the surrounding code's conventions. Enterprise logging on public methods.

## Frozen API contract (all three tasks build against THIS)

Task A implements it; Tasks B and C consume it. Do not change these signatures without
telling the Manager, who tells the Architect.

In `src/CcDirector.Core/Browsers/BrowserDefaultStore.cs`, ADD (keep every existing member -
`Load`, `Save`, `ResolveBrowser`, the `BrowserDefault` record - untouched):

```csharp
// Per-repository remembered default. Stored in config.json under
//   browser.repoDefaults : { "<canonical repo path>": { exePath, profileFolder }, ... }
// keyed by the session's repository path. Must survive an app restart and must NOT touch
// the existing browser.default (global) section.

public static BrowserDefault? LoadForRepo(string repoPath);
public static void SaveForRepo(string repoPath, BrowserDefault value);

// Effective remembered default for a session, applying the resolve order:
//   1. repo default (LoadForRepo)  2. global default (Load)  3. null.
// A null return legitimately means "no remembered default - caller uses the OS default".
// repoPath may be null/empty (a session with no repo) - then this is just Load().
public static BrowserDefault? Resolve(string? repoPath);
```

Path-key normalization requirement (Task A owns the exact implementation): the same
repository must map to the same key regardless of trailing slash, `/` vs `\`, and (on
Windows) letter case. Pick one canonical form, use it for BOTH save and load, and keep a
private helper so the rule lives in one place. Persist via
`CcDirectorConfigService.MergePatch` / read via `ReadRaw` - never hand-write config.json,
so unrelated sections are preserved.

## Task A - Store + resolve layer  (Worker: "ctxtax-multi-worker-store")

Files (DISJOINT, yours alone):
- `src/CcDirector.Core/Browsers/BrowserDefaultStore.cs`

Do:
1. Add `LoadForRepo`, `SaveForRepo`, `Resolve` exactly as the contract above, plus the
   private path-key normalizer.
2. Reuse the existing `BrowserDefault` record and the existing global `Load`/`Save` - do
   not duplicate them. `Resolve` calls `LoadForRepo` then falls through to `Load`.
3. Log entry/exit/error on the new public methods in the existing style, ASCII only.
4. `SaveForRepo` writes ONLY under `browser.repoDefaults.<key>`; confirm by reasoning that
   `browser.default` and other config sections are untouched (MergePatch deep-merges).
5. Build just to be sure your file compiles:
   `dotnet build src/CcDirector.Core/CcDirector.Core.csproj`.

Done when: the three methods exist, compile, and follow the no-fallback + ASCII rules.
Report back to the Manager the moment the file is written so B and C can rely on real
symbols.

## Task B - Menu interaction layer  (Worker: "ctxtax-multi-worker-menu")

Files (DISJOINT, yours alone):
- `src/CcDirector.Terminal.Avalonia/LinkContextMenuBuilder.cs`

Depends on Task A's symbols (`Resolve`, `SaveForRepo`). Start after the Manager says
Task A's file is written.

Do:
1. Change the plain-click behavior: the "Open in Browser" parent header click currently
   calls `OpenInBrowserDefault`, which loads only the GLOBAL default. Make it resolve via
   the new order instead - `BrowserDefaultStore.Resolve(context.RepoPath)`, and when that
   returns null, open the OS/system default (today's null behavior is unchanged). Preserve
   the no-fallback throw when a remembered browser is gone.
2. Solve the intent problem (SPEC "user-visible intent problem"): today a profile click
   BOTH opens AND saves the GLOBAL default. Make the user's intent unambiguous - a profile
   pick must let the user say WHICH default they are setting: this repository (the common
   case) vs application-wide, and still allow a plain one-off open.
   RECOMMENDED design (you may refine, but record what you chose in your report for
   PROOF.md): make each profile leaf expand into three explicit actions -
     - "Open once" (open only, save nothing) - this keeps DoD #5 one-off working.
     - "Set as default for this repository" (open + `SaveForRepo(context.RepoPath, ...)`)-
       OMIT this entry when `context.RepoPath` is null/empty (no repo to set for).
     - "Set as application-wide default" (open + the existing global `Save`).
   Keep "System default" and the per-browser/per-profile structure. The submenu is still
   the one-off surface.
3. Keep all existing helpers you are not changing. ASCII only, existing FileLog style,
   try/catch only at the click entry points (as the file already does).
4. Build: `dotnet build src/CcDirector.Terminal.Avalonia/CcDirector.Terminal.Avalonia.csproj`.

Done when: plain click resolves repo -> global -> OS; setting a repo default and setting
the app-wide default are both possible and unambiguous; one-off picks still open; no new
warnings; you have written a short note of your menu design decision for PROOF.md.

## Task C - Tests  (Worker: "ctxtax-multi-worker-tests")

Files (DISJOINT, yours alone) - CREATE this new file:
- `src/CcDirector.Core.Tests/Browsers/RepoBrowserDefaultStoreTests.cs`

Depends on Task A's symbols. Start after the Manager says Task A's file is written.

Do:
1. Follow the existing config-test pattern EXACTLY (see
   `src/CcDirector.Core.Tests/Configuration/SidebarConfigTests.cs`): redirect
   `CC_DIRECTOR_ROOT` to a temp dir in the constructor, restore + delete in `Dispose`,
   and put the class in `[Collection("CcStorageRoot")]` so it serializes with the other
   root-mutating tests. Seed config.json by writing the file directly when needed.
2. Cover the resolve ORDER against the store (this is the DoD #7 requirement):
   - repo default present -> `Resolve(repo)` returns the repo default (not the global).
   - no repo default, global present -> `Resolve(repo)` returns the global default.
   - neither present -> `Resolve(repo)` returns null (caller would use OS default).
   - `SaveForRepo` round-trips through disk (survives a re-read) and does NOT disturb
     `browser.default` or unrelated config keys.
   - path-key normalization: saving with one form and loading/resolving with a
     trailing-slash / separator / case variant of the same path hits the same entry.
3. Do NOT depend on any real installed browser (do not call `ResolveBrowser`/`DetectBrowsers`
   in these tests) - assert on the stored `BrowserDefault` (exePath/profileFolder) only,
   so the tests are deterministic on any machine.
4. Run: `dotnet test src/CcDirector.Core.Tests/CcDirector.Core.Tests.csproj
   --filter "FullyQualifiedName~RepoBrowserDefaultStore"` and paste the output for PROOF.md.

Done when: the new tests are green and the rest of the Core.Tests suite still passes.

## Integration (Manager owns)

After A, B, C report done:
1. Full solution build: `dotnet build cc-director.sln` - must succeed with NO new warnings.
2. Full Core.Tests run to confirm nothing regressed:
   `dotnet test src/CcDirector.Core.Tests/CcDirector.Core.Tests.csproj`.
3. Write `PROOF.md` at the repo root: files changed (the 3 files), the menu-interaction
   design decision (from Task B), and the pasted test output proving the resolve order.
4. Stage ONLY the feature's own files by name (never `git add -A` on this shared checkout)
   and commit on `exp/ctxtax-multi`. Do NOT push or open a pull request - this is an
   experiment branch; committing on the branch is the finish line for this run.

## Sequencing recommendation

Spawn Worker A first (small, fast). The moment A reports its file written, spawn Workers B
and C in parallel - they build against A's real symbols on the same checkout. Coordinate
with `cc-devthrottle message ask/send`. Keep every worker's context tight: seed each ONLY
its own task slice from this plan plus "read SPEC.md for the finish line", nothing else.
