# Mission: Source Control tab + orphaned-worktree reaper in the Director (2026-07-23)

## Why (the point of this mission)
Worktrees and stranded branches pile up because a merge is a GitHub event and a worktree is a
local disk artifact, and nothing links the two - so nobody knows what is safe to delete without
digging by hand. A recent cleanup went from 82 worktrees to 8, all by hand. We want the Director
to make this visible and one-click safe: a Source Control tab with a red-dot badge and a single
"Remove N orphaned worktrees" button that only ever removes worktrees whose work is provably on
origin/main.

## The specification - READ IT FIRST
The full, build-ready design is GitHub issue **thefrederiksen/devthrottle_internal#503**. Read it
before writing anything:
```
gh issue view 503 --repo thefrederiksen/devthrottle_internal
```
Everything below is a pointer to that spec, not a replacement for it. If the spec and this brief
ever disagree, the issue wins - and tell the owner.

## Where the code lives
- The Director is the Avalonia desktop app in this repo (`src/CcDirector.Avalonia`), binary
  `cc-director.exe`. The Source Control tab is a new view there.
- The worktree/branch detection and the safe-to-reap verdict belong in a testable service (Core or
  a Gateway-adjacent service), NOT in the view - the view renders what the service decides. Follow
  the "dumb client" rule in this repo's CLAUDE.md: the verdict is computed once in one place and the
  view only renders it.
- The reaper runs git commands (`git worktree list --porcelain`, `git worktree remove`,
  `git worktree prune`, `git fetch --prune`, `git cherry`, `git ls-remote`).

## Phased plan - prove each phase before the next, each phase merges to origin/main on its own
1. **Detector service** (no UI): enumerate every worktree for a repo and compute the fail-closed
   safe-to-reap verdict EXACTLY as the spec defines it (not primary checkout; clean tree; work
   proven merged by pull-request-merged OR origin-branch-gone OR `git cherry` clean; detached HEAD
   is an ancestor of origin/main). Unit tests are the heart of this phase: plant a worktree with
   uncommitted work and prove it is NEVER safe; plant a squash-merged-then-deleted branch and prove
   it IS safe. This phase carries the two must-pass acceptance tests from the issue.
2. **Read-only UI**: the Source Control tab with the red-dot badge + count and the two-group listing
   (safe-to-reap vs needs-attention), plus the copy-to-clipboard report. No delete button yet.
   Comply with `docs/VisualStyle.md` - all UI changes must.
3. **The reaper**: the "Remove N orphaned worktrees" button. Re-check safety immediately before
   acting; remove only the safe set; run `git worktree prune`; and HANDLE the Windows locked-file
   case (a `git worktree remove` that deregisters the worktree but fails to delete the folder with
   "Directory not empty" because bin/obj DLLs are locked) - report which folders remain rather than
   claim success. Log every removal with its proof-of-safety.

## Hard rules
- Merged to origin/main is the ONLY done. Each phase drives to a merged pull request.
- NEVER work in the shared checkout `D:\ReposFred\devthrottle`. Cut a worktree from origin/main per
  piece of work: `git fetch origin` then `git worktree add ../devthrottle-<task> -b <branch> origin/main`.
- Every phase builds AND tests green before it merges. Merge on green.
- NO Claude / AI / assistant attribution anywhere - commits, pull requests, issues, comments, docs.
  Write everything as the owner.
- Follow this repo's CLAUDE.md: responsive UI (immediate feedback), enterprise logging on public
  methods, tests for public methods and every bug fix, try-catch at entry points only, no fallback
  programming (fail closed with a clear message).
- To TEST the Director UI without killing the owner's running Directors: build to slot 5+
  (`scripts\local-build-avalonia.ps1 -Slot 5`) and launch via the `cc-director-launch` Windows
  scheduled task, per CLAUDE.md rule 0b. NEVER taskkill or Stop-Process the owner's Directors.

## Coordination
- Two other sessions are live on this repo: "repo cleanup" (id 40ed) and "Cockpit Fix". Do NOT touch
  their branches (anything settings-*, land/settings-*, mtr/*, the open pull requests). Ask if unsure:
  `cc-devthrottle message ask 40ed "<question>"`.
- Ping the owner (and 40ed) at each phase merged.
