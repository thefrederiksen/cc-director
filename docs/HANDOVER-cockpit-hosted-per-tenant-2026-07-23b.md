# Handover — Cockpit per-tenant on hosted Gateway (2026-07-23, rev B)

Reconstructed from the dead "Cockpit Fix" session's terminal (devthrottle 2c5a56d8 /
Claude transcript `fafb8a10`). That session died on "Prompt is too long" (context
exhaustion) mid-cleanup. This document supersedes
`HANDOVER-cockpit-hosted-per-tenant-2026-07-23.md` and corrects it on two points: the
full Gateway test suite DID finish green, and the exact origin/main state is now confirmed.

## The one thing to know
The remaining cockpit work is **a DEPLOY, not more fixing** — plus landing ONE last
pull request (#2059 Transcription Health). Five of the six broken hosted pages are fixed
and merged to origin/main; the live hosted Gateway is just running old code, so it still
LOOKS broken. Do a full review of reality before touching anything.

## The mission (unchanged)
Get the ENTIRE Cockpit working per-tenant on the hosted, multi-tenant Gateway, merged to
origin/main, then verified against the live hosted Gateway. Confirmed-broken-on-hosted
pages were: Settings, Your Throttle (Stats), Voice Recorder, Transcription Health,
Dictionary, plus Injected launch text. Owner's directive: make them WORK per-tenant, do
NOT hide them.

## The per-tenant pattern every fix follows
Partition the backing store by tenant. The route resolves the CALLER's tenant with
`GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary)`, serves only that tenant's data,
and returns **403 when no tenant resolves — NEVER the Local partition**. Self-host resolves
to the single `TenantId.Local` and keeps the existing flat store unchanged. Every fix ships
a hostile two-tenant test (serves for an enrolled tenant; 403 for an unbound device; tenant
A cannot read tenant B). Reference tests: `HostedPerAccountSettingsServeTests`,
`HostedStatsServeTests`, `HostedRecordingServeTests`, `HostedTranscriptionServeTests`.

## DONE — merged to origin/main (verified 2026-07-23, tip `db5908e3`)
- **Settings** — `7a9d1b96` (#2049). Per-tenant settings store/resolver + migrations; one
  three-tab per-account page (Notifications, AI, Car Mode); machine settings moved off the
  web (About page + `cc-devthrottle autostart` CLI); hosted deny retired for per-account
  routes. Also removed wingman training capture + the A/B-test-over-saved-sessions feature
  (owner's call). Closed #2017, #2022.
- **Your Throttle / Stats** — `bbd34e8e` (#2053). Stats deny retired; `/stats/data` serves
  the caller's tenant, 403 when unresolved. Closed #1848.
- **Injected launch text** — `458919e9` (#2063). Per-tenant injected text in the
  tenant_settings store (read-time fallback to config.json for self-host); Director
  downloads its own tenant's text from the same route (no Director change).
- **Voice Recorder + Dictionary** — `4921ae76` (#2067). Per-tenant recording store
  (per-tenant `RecordingIngestService` cache) + per-tenant glossary; `/ingest` exclusive
  deny retired.
- Supporting deploy tooling also landed: `2bac471b` (#2062, hosted deploy made
  manual-only) and `db5908e3` (#2065, deploy skill + only-latest-deploy-wins).

## DONE, GREEN, but NOT MERGED — Transcription Health (#2059, still OPEN)
- Branch **`land/transcription-per-tenant`** is on origin at tip **`5b1fb587`**. It was
  pushed but **no pull request was ever opened**, and it is **not merged** into origin/main.
- It was based one commit behind the current origin/main tip (`db5908e3`), so it needs a
  trivial **rebase onto origin/main** before merge.
- What it does: `TranscriptionHistoryLog.DirectoryFor(tenant)` / `ForTenant(tenant)` is the
  single per-tenant-dir source of truth (Local = flat dir; else subdir);
  `TranscriptionAudioArchive` gets the matching `DirectoryFor`. `TranscribeAsync` takes an
  optional `TenantId`; `RecordHistory` writes to that tenant's log — dictation, voice
  (utterance + one-shot), and batch all pass their resolved tenant; the recording path
  already injects its own per-tenant log. The hosted history write-skip guard was REMOVED
  (safe now that every write is per-tenant). `TranscriptionAnalysisEndpoint` un-denied:
  resolves caller tenant, reads that tenant's history/audio, 403 unresolved. Deny assertions
  removed from `HostedContentReadDenyTests` + `HostedContentDenyGroupFilterTests`;
  `HostedTranscriptionServeTests` added.
- **TEST STATUS — CORRECTED:** the prior handover said the full suite result "was NOT
  captured, RE-RUN it." It DID finish, and it PASSED. Background task `bi9lqpddm` ("Full
  Gateway suite for transcription per-tenant") completed with **exit code 0:
  Failed 0, Passed 3636, Skipped 17, Total 3653, Duration 11m5s** — the notification landed
  as the session died. That green was on tip `5b1fb587`. Because a rebase is still needed,
  **re-run the full suite once after rebasing** to confirm nothing shifted, then merge.

## NOT DEPLOYED — the live hosted Gateway is stale
The live hosted Gateway (`gateway.devthrottle.com` / `devthrottle-gw.azurewebsites.net`)
`/healthz` reports `version 1.7.4`, but **that version string does not prove the running
commit** (it lies on direct pushes — see memory `hosted-gateway-verify-running-commit`).
The prior session determined the actual running commit is **`bbd34e8e`** (Settings + Your
Throttle only). So on the live Gateway right now, **Injected text, Voice Recorder,
Dictionary, and Transcription still show old/broken behaviour** — this is un-deployed old
code, not a broken fix. The owner asked for ONE deploy covering all five once #2059 lands.

## What the next session must do — in order
1. **Verify reality first (do not trust this doc blindly).** Confirm the merged tips above
   against origin/main; confirm issue #2059 is still open and the branch still at
   `5b1fb587`; confirm the live Gateway's ACTUAL running commit via the ACR digest /
   `COCKPIT_COMMIT` (not just `/healthz`).
2. **Land #2059.** Cut a fresh worktree at origin/main (or re-checkout
   `land/transcription-per-tenant`), `git rebase origin/main`, rebuild, **re-run the full
   Gateway suite** (`dotnet test src/CcDirector.Gateway.Tests/...`, ~11–16 min; a lone red
   test is usually contention — re-run it ALONE; memory
   `gateway-tests-run-failing-test-alone-...`). Confirm the tenant-isolation architecture
   gate (#2052 `TenantGateArchitectureTests`) passes. On green: push, open a PR (body
   "Closes #2059"), `gh pr merge <n> --squash --delete-branch`. The local
   `fatal: 'main' is already used by worktree` from `gh` is HARMLESS — the remote merge
   succeeds; verify with `gh pr view`.
3. **Deploy hosted ONCE** from a fresh worktree cut at the new origin/main tip. Mechanics:
   memories `hosted-gateway-azure-deploy-mechanics`, `hosted-deploy-by-digest-not-tag`,
   `hosted-gateway-release-manual-only`. De-risk first with a local publish
   (`dotnet publish src/CcDirector.Gateway.Host/... -c Release -m:1 -p:RunMobileBuild=true
   -p:RunCockpitBuild=true -p:RunWorkspaceTypecheck=true -o <tmp>`). The `az acr build`
   glyph crash on Windows is EXPECTED; the script's polling rides through it. Verify the
   RUNNING commit via the ACR build's `COCKPIT_COMMIT` + digest pin + `/healthz` 200 +
   `provider=Postgres` — do NOT trust `/healthz` version alone.
4. **Self-verify live** with browser-harness `cencon` profile (soren@centerconsulting.com):
   open `gateway.devthrottle.com/cockpit`, walk all 14 pages — each should render
   per-tenant, none should show "Failed to load / rejected the request (404)". Curl cannot
   test this: hosted requires a device-key credential, so the self-host loopback token 401s.

## Cleanup owed
- **Orphaned directory `D:/ReposFred/devthrottle-txn`.** The dead session ran
  `git worktree remove --force` (failed: "Directory not empty") then `git worktree prune`,
  and deleted the local branch. The dir now has NO `.git` and is not a registered worktree
  — it is dead build output. Safe to `rm -rf`. The actual work is preserved on origin at
  `land/transcription-per-tenant` (`5b1fb587`); do NOT delete that remote branch until
  #2059 is merged.

## Known follow-ups (not blockers)
- Generated client `schema.ts` is STALE (lists routes #2022 removed + training-capture). It
  is generated, does not break the build — regenerate via `gen:api` against a running Gateway.
- Transcription **troubleshooting audio** is still hosted-write-skipped: the Transcription
  Health METRICS (stats/turns/terms) work per-tenant on hosted, but per-turn audio clips are
  not archived on hosted. Secondary (24h troubleshooting). `TranscriptionAudioArchive.DirectoryFor`
  exists; removing its `TrySave` hosted skip + passing a per-tenant archive at the transcribe
  sites finishes it.

## Coordination note
Open PRs at handover time were unrelated: #2020 (device registry cutover) and #1937
(dirty-tree run guard). Neither touches the cockpit/transcription work.

## Hard rules
- NO Claude / AI / assistant attribution anywhere (commits, PRs, issues, docs). Write as the
  owner.
- Never work in the shared checkout `D:/ReposFred/devthrottle`; cut worktrees from origin/main.
- Merged to origin/main is the only "done"; merge on green, QA after.
