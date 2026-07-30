# Handover — Cockpit Fix: make the whole cockpit work per-tenant on hosted (2026-07-23)

Session "Cockpit Fix" (2c5a56d8). Written at context end; another session must finish.

## READ THIS FIRST — it is NOT-DEPLOYED, not not-fixed
The owner saw the **Voice Recorder** page show "Failed to load. Is the Gateway running?" on the
live hosted cockpit. That is **the un-deployed OLD code**, not a broken fix. Voice Recorder (#2058)
IS fixed and MERGED to origin/main — but the live hosted Gateway is still running commit
**`bbd34e8e`**, which predates every fix except Settings + Your Throttle. So on the live Gateway
right now: Injected text, Voice Recorder, Dictionary all still show the old broken behaviour, and
Transcription isn't even merged. **The remaining work is a DEPLOY (plus merging #2059), not more
fixing.**

## FIRST STEP FOR THE NEXT AGENT
Do a **full review of everything outstanding before touching anything**: confirm which PRs are on
origin/main, confirm the live hosted Gateway's ACTUAL running commit (ACR digest / COCKPIT_COMMIT,
not just /healthz), confirm the `#2059` branch state, then execute the ordered plan below. Do not
assume this document is complete — verify against origin/main and the live Gateway.

## The mission
Get the ENTIRE Cockpit working per-tenant on the hosted Gateway, merged to origin/main, then
tested against the live hosted Gateway. Two pages were the confirmed-broken priority (Settings,
Your Throttle); an audit then found three more pages broken on hosted (Voice Recorder,
Transcription Health, Dictionary) plus Injected text. The owner directed: make them all WORK
per-tenant (do NOT hide them).

## The per-tenant pattern (every fix follows it)
Partition the backing store by tenant; the route resolves the CALLER's tenant with
`GatewayEndpoints.ResolveReadTenant(ctx, tenantBoundary)`, serves only that tenant's data, and
returns **403 when no tenant resolves — NEVER the Local partition**. Self-host resolves to the
single `TenantId.Local` and keeps the existing flat store, unchanged. Every fix carries a hostile
two-tenant test (serve for an enrolled tenant; 403 for an unbound device; tenant A cannot read
tenant B). Reference: the shipped `HostedPerAccountSettingsServeTests`, `HostedStatsServeTests`,
`HostedRecordingServeTests`, `HostedTranscriptionServeTests`.

## DONE — merged to origin/main
- **#2049 Settings** (squash `7a9d1b96`) — per-tenant settings store/resolver + migrations; one
  three-tab per-account page (Notifications, AI, Car Mode); machine settings off the web (About
  page + `cc-devthrottle autostart` CLI); hosted deny retired for per-account routes. **Also removed
  wingman training capture + the A/B-test-over-saved-sessions feature entirely** (owner's call).
- **#2053 Your Throttle** (squash `bbd34e8e`) — Stats deny retired; `/stats/data` serves the
  caller's tenant, 403 unresolved.
- **#2057 Injected text** (squash `458919e9`) — per-tenant injected launch text in the
  tenant_settings store (read-time fallback to config.json for self-host); Director downloads its
  own tenant's text from the same route (no Director change).
- **#2058 + #2060 Voice Recorder + Dictionary** (squash `4921ae76`) — per-tenant recording store
  (per-tenant `RecordingIngestService` cache) + per-tenant glossary; `/ingest` exclusive deny
  retired.

Closed issues: #2017, #2022, #1848, #2054 (already satisfied), #2057, #2058, #2060.

## DONE but NOT MERGED — #2059 Transcription Health
- Branch **`land/transcription-per-tenant`**, worktree **`D:/ReposFred/devthrottle-txn`**, tip
  **`5b1fb587`**. Was **1 commit behind origin/main** (`db5908e3`) at handover — REBASE first.
- What it does: `TranscriptionHistoryLog.DirectoryFor(tenant)` / `ForTenant(tenant)` is the ONE
  per-tenant-dir source of truth (Local = flat dir; else subdir); `TranscriptionAudioArchive`
  gets the matching `DirectoryFor`. `TranscribeAsync` takes an optional `TenantId` and
  `RecordHistory` writes to that tenant's log — dictation, voice (utterance + one-shot), and batch
  all pass their resolved tenant; the recording path already injects its own per-tenant log. The
  **hosted history write-skip guard was REMOVED** (safe now that every write is per-tenant).
  `TranscriptionAnalysisEndpoint` un-denied: resolves caller tenant, reads that tenant's
  history/audio, 403 unresolved. Deny assertions removed from `HostedContentReadDenyTests` +
  `HostedContentDenyGroupFilterTests`; `HostedTranscriptionServeTests` added.
- Gates so far: Gateway + tests build clean; targeted suites green
  (`HostedTranscriptionServe`/`HostedContentDenyGroupFilter` 14/14). **The FULL Gateway suite
  (background task `bi9lqpddm`) was still running at session end — its result was NOT captured.
  RE-RUN it before merging.**

## NOT DEPLOYED — the hosted Gateway is stale
The live hosted Gateway (`devthrottle-gw.azurewebsites.net`) is still running **`bbd34e8e`**
(Settings + Your Throttle only). **None of Injected text / Voice Recorder / Dictionary /
Transcription are live yet.** The owner asked for ONE deploy covering all five once #2059 lands.

## What the next session must do (in order)
1. `cd D:/ReposFred/devthrottle-txn`; `git fetch origin`; `git rebase origin/main`; rebuild; run
   the FULL Gateway suite (`dotnet test src/CcDirector.Gateway.Tests/...`, ~12–16 min; a red test
   may be contention — re-run it ALONE). Also confirm it passes the tenant-isolation architecture
   gate (#2052, `TenantGateArchitectureTests`).
2. On green: `git push -u origin land/transcription-per-tenant`; open a PR (body: "Closes #2059");
   `gh pr merge <n> --squash --delete-branch`. (The local `fatal: 'main' is already used by
   worktree` error from gh is HARMLESS — the remote merge succeeds; verify with `gh pr view`.)
3. Deploy hosted ONCE from a FRESH worktree cut at the new origin/main tip. Mechanics: memory
   `hosted-gateway-azure-deploy-mechanics`; run
   `AZURE_CONFIG_DIR="%LOCALAPPDATA%\cc-director\config\azure-hosted-gw" bash deploy.sh <checkout>`
   from `D:/ReposFred/devthrottle_internal/docs/architecture/step3-azure-deploy/`. De-risk first
   with a local Docker-equivalent publish:
   `dotnet publish src/CcDirector.Gateway.Host/... -c Release -m:1 -p:RunMobileBuild=true
   -p:RunCockpitBuild=true -p:RunWorkspaceTypecheck=true -o <tmp>`. The `az acr build ✓`-glyph
   crash on Windows is EXPECTED; the script's polling rides through it. Verify the RUNNING commit
   via the ACR build's `COCKPIT_COMMIT` + digest pin + `/healthz` 200 + `provider=Postgres` (do
   NOT trust `/healthz` version alone — memory `hosted-gateway-verify-running-commit`).
4. Self-verify live: browser-harness `cencon` profile (soren@centerconsulting.com), open
   `gateway.devthrottle.com/cockpit`, walk all 14 pages — all should render per-tenant, none should
   show "Failed to load / rejected the request (404)". (Curl can't test: hosted requires a
   device-key credential, so the self-host loopback token 401s.)
5. Clean up: `git worktree remove D:/ReposFred/devthrottle-txn` + delete the branch after merge.

## Known follow-ups (not blockers)
- Generated client `schema.ts` is STALE (lists routes #2022 removed + training-capture). It is a
  generated file that does not break the build — regenerate via `gen:api` against a running Gateway.
- Transcription **troubleshooting audio** is still hosted-write-skipped: the Transcription Health
  METRICS (stats/turns/terms) work per-tenant on hosted, but per-turn audio clips are not archived
  on hosted. Secondary (24h troubleshooting). `TranscriptionAudioArchive.DirectoryFor` exists;
  removing its `TrySave` hosted skip + passing a per-tenant archive at the transcribe sites would
  finish it.

## Hard rules
- NO Claude / AI / assistant attribution anywhere (commits, PRs, issues, docs). Write as the owner.
- Never work in the shared checkout `D:/ReposFred/devthrottle`; cut worktrees from origin/main.
- Merged to origin/main is the only "done"; merge on green, QA after.
