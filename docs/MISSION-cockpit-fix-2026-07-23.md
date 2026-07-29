# Mission: Cockpit Fix (2026-07-23)

## Why (the point of this mission)
We are relaunching the hosted, multi-tenant Gateway. The Cockpit web app is broken on several
pages in that hosted environment. Two pages are confirmed broken and are the priority: the
**Settings** page and the **Your Throttle** (Stats) page. Your job is to get the ENTIRE cockpit
fully working per-tenant on the hosted Gateway, with the code MERGED to origin/main so it can be
tested against the live hosted Gateway.

## Definition of done
- Merged to origin/main is the ONLY done. Committed, pushed, or an open pull request are all still
  in progress. Each page you fix drives all the way to a merged pull request on origin/main.
- Every page you touch must be built AND tested green before it merges. Merge on green; QA on main.
- You are allowed to deploy the hosted Gateway as often as you want to verify against it.

## Order of work
1. **Settings** (first)
2. **Your Throttle / Stats** (second)
3. Then sweep every other cockpit page and confirm it works per-tenant on hosted.

## Settings - the implementation already exists, land it
- The full implementation is on branch **`settings-unify`**, worktree **`D:\ReposFred\devthrottle-unify`**,
  tip `967e5bfa`. It is a 10-commit stack implementing GitHub issues #2017 + #2022: a per-tenant
  settings store + resolver + migrations, the collapsed one-per-account Settings page, machine
  settings moved off the web to the CLI, and retirement of the hosted deny so the routes serve
  per-tenant. It builds clean and has hostile two-tenant isolation tests.
- It is built on `settings-per-tenant` (on origin) + a cherry-pick of `mtr/settings-runtime-threading`.
  It is roughly 16 commits behind current origin/main and NOT pushed.
- On CURRENT origin/main the whole owner-settings group is DENIED on hosted (#1898) - that is why the
  Settings page is broken/empty on hosted. This stack is the fix.
- Task: cut a fresh worktree from origin/main, land the settings-unify work onto it (rebase or
  re-apply), re-run the gates, open a pull request, merge. Read the full handover first:
  `docs/HANDOVER-issue-2022-settings-unify-2026-07-22.md` and the auto-memory
  `issue-2022-settings-unify-mission.md`.

## Your Throttle / Stats - write the fix fresh on origin/main
- On current origin/main the Stats / Your Throttle page is intentionally DENIED (404) on hosted:
  `src/CcDirector.Gateway/Stats/StatsPageEndpoint.cs`, method `DenyOnHosted()`, gated on
  `GatewayHostedMode.IsHosted`.
- The stats aggregator is ALREADY fully tenant-aware on main: every read takes a `TenantId`
  (`aggregator.CurrentTotals(tenant)`, `HourlyTurns(tenant)`, `RepoTotals(tenant)`, etc.). The
  self-host path hardcodes `TenantId.Local`.
- The fix: on hosted, stop denying; resolve the CALLER's tenant, return 403 if the tenant cannot be
  resolved (never fall back to Local), and serve that tenant's totals. This mirrors EXACTLY what
  settings-unify did for the settings routes - use the same tenant-boundary pattern.
- DO NOT resurrect either of these - both are traps:
  - branch/worktree `throttle-on` (`4c6ada91`, "demo: serve Your Throttle... override the deny"):
    a demo hack that rips out the deny and knowingly leaks COMBINED all-tenant totals. Unsafe.
  - `wip/1848-stats-tenant-partition-not-for-merge`: a stale earlier attempt, ~113 commits behind,
    superseded by main's own #1848 work. Marked not-for-merge.
- Write the fix fresh on a worktree cut from current origin/main, with hostile two-tenant tests that
  prove one tenant cannot read another's throttle.

## Hard rules
- NEVER work in the shared checkout `D:\ReposFred\devthrottle` (it is ~117 commits behind and other
  sessions use it). Cut a worktree from origin/main for every piece of work:
  `git fetch origin` then `git worktree add ../devthrottle-<task> -b <branch> origin/main`.
- To READ shipped code, read origin/main directly (`git show origin/main:path`), never the stale tree.
- NO Claude / AI / assistant attribution anywhere - commits, pull requests, issues, comments, docs.
  The code is the owner's; write commits and pull requests as the owner.
- A branch lives less than a day; split work so each piece merges the same day.
- Deploy mechanics for the hosted Gateway are in the auto-memory files
  `hosted-gateway-azure-deploy-mechanics.md`, `hosted-deploy-by-digest-not-tag.md`, and
  `hosted-gateway-verify-running-commit.md` (the /healthz version can lie on direct pushes; verify
  the actual running commit via the ACR digest / live Postgres / file-share logs).

## Coordination
- A separate session ("repo cleanup", id 40ed) is landing `mtr/g3-paywall-reader-gate` and purging
  stale branches/worktrees. Do NOT touch the g3-paywall branch or the cleanup branches. If you need
  something from that work, ask it: `cc-devthrottle message ask 40ed "<question>"`.
- Ping at milestones (each page merged, each hosted deploy verified).
