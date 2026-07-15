# Manager brief - Tier 2 items 5, 6, 7 (Stable Release, v1.3.0)

Mission id: `ac6883bb-09e2-4b5a-96bf-df3eae8d9f63`
Architect: session `2eef41a3` ("Stable Release - Architect").

Read the mission brief for the WHY: `docs/architecture/stable-release-mission-2026-07-14.html`

**The mission is LATE. The owner has waited two days. Move.**

## Standing authority - read this first

- **The Architect holds the owner's delegated authority to COMMIT.** He granted it in his own words:
  *"You have full authority to commit. Just don't merge it to master. Just commit and keep moving."*
  You do NOT wait for the owner's word on a commit. You do not ask. Commit and keep going.
- **NOTHING merges to main.** Not by you, not by me. Push the branch, open the pull request, leave it OPEN.
- Do not stop for approvals. Do not batch. Report each item as it lands and move to the next.

## Your three items

Tier 1 is done and merged: dropped-command timeout (`bcb94dbb`), the blue fix (`5b09f71f`), plus the
loopback bind / settings route / verify-handshake deletion sitting in an open pull request. Do not redo any
of it.

### 5. Delete the dead code - ONE focused, statically-safe deletion

Named by both reviews: the register and heartbeat cluster inside `GatewayClient`; `DirectorForwarding` and
the unused forwarder registration; the orphaned `DispatchContracts`; the `CockpitWsUrls` and
`CockpitShotUrls` contracts; the now-inert `streamMode` configuration key; the never-assigned
`_serveProvisioner` field with its dependent branches.

**THE STANDING DEAD-CODE LIST IS UNRELIABLE. Verify callers before deleting a single file.** It names files
by their RETIRED PURPOSE, not by who still calls them. Proven false positive:

- **`GatewayConnectivitySelfTest` is NOT dead** - the desktop Gateway panel's diagnostics ladder still uses
  it. Deleting it breaks the panel. This was caught before it cost anything; do not un-catch it.

The rule, learned twice today in opposite directions: **a call site is not a caller, and a retired purpose
is not a dead file.** `git grep` real callers on `origin/main` and confirm the path that reaches them
actually runs. `_serveProvisioner` is genuinely never assigned (declared at `ControlApiHost.cs:101`, only
ever set to null) - its dependent branches are dead. `GatewayClient.Start` never starts the register /
heartbeat / verify loops (see the comment at `GatewayClient.cs:588`), so that cluster is genuinely
unreachable.

**Verified orphaned by Tier 1 item 4** - fold these in: `src/CcDirector.Gateway.Contracts/DirectorVerification.cs`
(`DirectorVerifyRequest` + `DirectorVerifyResultDto`, now zero users repo-wide), and
`DirectorDto.TwoWayVerifiedAt`, declared but written nowhere - it serialises null forever to every caller,
its own small lie on the wire.

### 6. Fix the public API reference

`docs/public/api/01-control-api.md` still describes sessions, prompts, buffers, git and handover endpoints
on the Director. The Director floor has about a dozen routes. **This document is public and is actively
misleading anyone reading it today** - it is the same failure mode that cost the owner yesterday, still
live. Nine references to deleted session endpoints, verified.

Do not guess the real surface. Read it: `git show origin/main:src/CcDirector.ControlApi/ControlEndpoints.cs`
and list the actual `app.Map*` routes. Document what IS, not what was.

### 7. Delete the documents describing worlds that never shipped

Five architecture documents describe designs never built or fully dead. No salvage value; every one is a
trap for the next reader. Identify them from the two reviews
(`docs/reviews/tunnel-only-review-codex-2026-07-14.md`, `docs/reviews/tunnel-only-review-claude-2026-07-14.md`).

**Do not delete the state/colour documents** - they were corrected and superseded earlier today by
`docs/architecture/session-state-authoritative-2026-07-14.html`. That work is done; leave it alone.

## Also in scope if cheap - stale comments that now contradict each other

7 stale "fall back to the HTTP dial" comments in `GatewayEndpoints` that the cut made false, while
`ICronWorkListDrainLauncher.cs:39` and `MachineSessionSpawner.cs:23-26` already say the endpoint is ignored
post-cut. Two sets of comments contradicting each other in one repo. Fold in; do not make a project of it.

## Explicitly NOT yours - do not start these

- The LAN addressing option being a user-visible control that now does nothing (owner's call).
- `TunnelFailure`'s default branch dropping Director messages into a bare 502 across ~20 legs.
- `docs/cencon/proof/issue-509/ask-sequence.log` - a tracked file a test rewrites with fresh GUIDs each run.
- Anything about session state, colours, or the rail.

## The rules that are not negotiable

1. **Verify before you claim.** `git show origin/main:<path>` / `git grep <pattern> origin/main`.
2. **Work in a worktree cut from origin/main.** Never `git checkout -b` in the shared checkout - it is on
   another session's live `feat/prompt-log`. Do not touch it.
3. **Proof:** items 5 and 7 are deletions - the proof is that the solution still builds and all seven suites
   stay green, plus a running Director boots and its diagnostics panel still works (you deleted around it).
   Item 6 is a document - the proof is that every route it lists exists on `origin/main`.
4. **No fallbacks.** Fix the root cause or fail loudly.
5. **Plain English, ASCII only.** No abbreviations, no jargon, no emoji.
6. **Commit freely under the Architect's delegated authority. NEVER merge.**
7. Report to the Architect. Fleet messages are ONE line. Never message the owner.

## Testing

**Seven** test projects, not two - Core plus Gateway alone is a false green. The last full run was 5517
passed. A deletion that drops the count needs an explanation.

## The fleet

| Director | Port | Use |
|---|---|---|
| slot 6 | 7883 | **NOT throwaway** - hosts REAL user sessions. Restarting it KILLS them. Leave it alone. |
| slot 2 | 7884 | Permanent. The Architect runs here. Do not disturb. |
| installed app | 7879 | v1.1.0, the broken build. Never test against it. |
| your own slot | pick 8+ | Your own proof Director, its OWN scheduled task, own root via `CC_DIRECTOR_ROOT`. |

Never kill a `cc-director*.exe` you did not launch. Shut your own down with
`POST http://127.0.0.1:<port>/shutdown` - a force-kill leaves a phantom session.

## Definition of done

Each item: fixed at the root, seven suites green, committed and pushed, pull request OPEN and not merged.
Report each as it lands. Then hand me anything Tier 2 orphaned for the release notes.
