# Mission: Repositories, handled like they matter (2026-07-23)

Status: ACTIVE. Branch `mission/repositories-full`, worktree `D:\ReposFred\devthrottle-repos-mission`.
Conduct: `.claude/skills/mission/SKILL.md`. Running state: `docs/MISSION-repositories-full-STATE.md`.

## The why
The owner ended up with roughly 300 dangling worktrees eating 100 gigabytes because nothing watched,
nothing warned, and cleanup required git fluency he should never need. When this mission is done,
repositories are a first-class DevThrottle subsystem: the Director always knows every repo, worktree,
branch, change, and who is working where; the Gateway centralizes it so any agent can ask in one call;
the system remembers over time and recommends cleanup in plain language; and agents do the dirty work.
A user who has never heard the word "worktree" stays clean without learning it.

## The specification
GitHub issue thefrederiksen/devthrottle_internal#510 (the full plan with per-phase work items and
tests), building on #507 (the shipped engine v1). Mockups (v4):
https://claude.ai/code/artifact/5a61b4d6-1cd8-4c9d-a6e4-d03b37c78362

## Rulings already made
- All phases (A, C, B, D, E from #510) build on this ONE mission branch; the final merge to
  origin/main happens only after the owner approves the QA report. (Stated by the owner.)
- Order on the branch: A -> B -> C -> D -> E (C's research runs early; C lands after the
  Director-side model is final so the pushed DTO is stable). (Architect ruling.)
- Phase E's scheduled cleanup agent stays OUT (marked optional in #510). (Architect ruling,
  announced to the owner.)
- Disk size: per-worktree sizes are required (they drive "reclaim N GB"); whole-repo sizes may be
  reduced or cached if measurement is too slow. (Architect ruling, announced.)
- The Recommendations panel lives in the Repositories home as a rail page, not on the Home screen.
  (Architect ruling, announced.)
- Destructive actions remain Director-local in this mission (the local reaper with its fail-closed
  verdict + live re-verify). Fleet-remote reap routing is OUT of scope; the Gateway API is
  read-only. (Architect ruling - matches the #510 trust rule.)

## The work, in landing order
1. A-model: worktree list + per-worktree sizes + dirty-since + provisional (verifying) flag on the model.
2. A-unify: the per-session Source Control tab reads the monitor (one brain).
3. A-watcher: FileSystemWatcher, debounced, recompute one repo per change.
4. A/B-detail: repository drill-down screen (tabs) in the Repositories home, reap in the home,
   verifying visual state, plain-language explainers.
5. B: diff viewer; branches with safe-delete verdict; pull requests; history.
6. C: Director push of the repository model to the Gateway; GET /repositories + /worktrees;
   cc-devthrottle repo list / worktree list; local relay fallback.
7. D: Gateway persistence (repo_snapshots, worktree_events + EF migration), recommendation rules
   (folded server-side/service-side), Recommendations page, weekly report section.
8. E: "Hand to an agent" dialog + brief templates + spawn wiring.
9. QA: full suites, slot-5 build from this branch, real-machine QA harness, independent Codex
   inspection, QA report for the owner.

## Out of scope
Scheduled cleanup agent; fleet-remote reap routing; Cockpit web surfaces for repositories; account
catalogs (browse not-yet-cloned repos); GitButler-style branch virtualization.
