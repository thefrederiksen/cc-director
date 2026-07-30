# Handover — Issue #2022 (unify Settings page) — 2026-07-22

Session "devthrottle - settings unify" (2e23cc73). Written on request; then this session went offline.

## Goal
Implement GitHub issue #2022 on thefrederiksen/devthrottle: collapse the Cockpit Settings page to ONE
per-account page identical on self-host and hosted, and move machine settings OFF the web page.

## Where the work lives (IMPORTANT — NOT PUSHED)
- Branch: **`settings-unify`** in worktree **`D:\ReposFred\devthrottle-unify`** (based on the `settings-per-tenant` tip 9bfdb76b, the unmerged #2017 foundation — NOT origin/main).
- Three commits, all authored as the owner (thefrederiksen), no AI attribution. **Local only — nothing pushed to origin.** The commits are safe on disk in that worktree.
  1. `32a633d6` — Part 1: machine settings off the web, page collapsed.
  2. `b41b9fae` — the MTR team's consumer commit `e45a461` cherry-picked (tenant threading through wingman/TTS/narration/car-mode). Conflicts resolved semantically (kept this base's CarMode telemetry naming, took the tenant threading incl. `ICarModeChat.CompleteAsync(TenantId)`; deduped a merge-artifact property). No fail-closed behavior weakened.
  3. `967e5bfa` — Part 2: retired the hosted deny for the per-account routes.
- Worktree is CLEAN (no uncommitted changes).

## What Part 1 did (32a633d6)
- Diagnostics + auto-resolved address + version → the web **About page** (`AboutDto` + `GET /gateway/about`, `AboutView`), read-only, both surfaces.
- Removed machine endpoints: brain restart, brain config, network addressing (GET+PUT), autostart PUT. Slimmed the `/gateway/settings` snapshot.
- Collapsed the Cockpit Settings page to three per-account tabs (Notifications, AI, Car Mode) — no "This machine" tab, pills always "your account", time zone moved onto Notifications, all surface branching removed.
- Autostart → installer + CLI: new Linux `systemd --user` mechanism (`GatewaySystemdAutostart`) joining the existing Windows Run key + macOS launch agent, a cross-OS facade (`GatewayAutostartControl`), `autostart on|off|status` on the setup CLI, and a `cc-devthrottle autostart` passthrough. **Owner approved the command name `cc-devthrottle autostart`.**

## What Part 2 did (967e5bfa)
- **Retired the hosted deny** for the per-account routes so they SERVE on the hosted Gateway (each resolves the caller's tenant, 403 if unresolved, never Local): the settings snapshot, snooze-default, snooze-presets, time-zone, ai-provider, tts-voice, and the five model/voice setters (wingman-model, wingman-fast-model, car-mode-model, car-mode-end-phrase, tts-model).
- **STILL denied on hosted** (mapped onto the group handle): `ai/models` + `ai/test-chat` (they spend the SHARED `DEVTHROTTLE_API_KEY` with no per-caller scoping — architect-approved to keep denied until caller-scoped auth/quotas/spend exist), `transcription-mode`, `injected-text`, `wingman/training-capture`.
- ai-provider snapshot gained a Gateway-owned `catalogAvailable` flag (false on hosted); the AI + Car Mode tabs read it to disable model browsing/Test on hosted with concise non-error text.
- Dropped the two vestigial global fields (training capture, telemetry consent) from the per-account snapshot.
- New test `HostedPerAccountSettingsServeTests` (two enrolled tenants on a hosted host): every newly hosted route serves an enrolled tenant, refuses an unresolved tenant with 403, and one tenant's writes stay invisible to another (time zone, snooze, voice, wingman model, car-mode phrase).

## Gates — what passed
- Gateway builds clean (Debug); cockpit 151/151 + typecheck clean (all workspaces).
- Owner-settings deny + self-host control + the new hostile two-tenant serve/403/isolation tests: green.
- `AiProviderEndpointTests` + `CockpitUrlEndpointTests`: 14/14.
- Cherry-picked threading tests (runtime-threading + CarMode + Wingman): 254/254.
- Setup-engine systemd: 7/7. Setup CLI `autostart status` proven end-to-end on Windows (reads the real Run key). Python passthrough tests pass.
- First FULL Gateway suite run: 3774 pass / 4 fail / 16 skipped. **All 4 were fixed** and re-verified green:
  - 3 were pre-existing #2017 test debt in `AiProviderEndpointTests` (asserted config.json; updated to assert the per-tenant resolver).
  - 1 was `CockpitUrlEndpointTests` asserting settings-denied-on-hosted (updated: it now serves per-account).

## What is NOT done (remaining)
- The full Gateway suite RE-RUN (at 967e5bfa) to confirm 0 failures was **still running** when this session stopped — its terminal number was not yet captured. (Background task id `bvcz3ja0l`.)
- Release build (Debug+Release) not yet run.
- Final cockpit build (`vite build`) not re-run since Part 2.
- QA report + screenshots across the localhost / self-host / hosted matrix: NOT done.
- Compatibility/security scans: NOT done.
- Branch not pushed; MTR architect's independent review not done; owner merge approval not given; not merged.

## How to resume
1. `cd D:\ReposFred\devthrottle-unify` (branch `settings-unify`, tip `967e5bfa`).
2. Confirm the full Gateway gate: `dotnet test src/CcDirector.Gateway.Tests/CcDirector.Gateway.Tests.csproj -c Debug`. Note: full-suite runs can throw spurious contention reds — re-run any red test ALONE to tell regression from contention.
3. Release build + cockpit build; then QA screenshots (build a localhost Gateway per CLAUDE.md slot-5 Task Scheduler rule; hosted behavior is also proven by `HostedPerAccountSettingsServeTests`).
4. Report the terminal totals + clean worktree + SHA + report/screenshot paths to the MTR architect (fleet session `devthrottle - mtr architect`, id b221a6b7) — they said they will then handle the explicit push/review authorization.

## Hard constraints (do not violate)
- NO Claude/AI attribution anywhere (commits, PRs, docs).
- Do NOT merge or deploy without the owner's explicit approval + a QA report + the MTR independent review.
- Keep the branch local/unpushed until the MTR architect authorizes the push.
- Never move the shared-credential routes (`ai/models`, `ai/test-chat`) to hosted until the credential is caller-scoped with quotas/spend controls.

Full context is also in the auto-memory file `issue-2022-settings-unify-mission.md`.
