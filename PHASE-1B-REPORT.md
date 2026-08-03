# Phase 1b: session credentials on the Gateway

Manager: session 193adde7. Branch `mission/remove-network-port`, worktree `D:\ReposFred\devthrottle-noport`.

## What the phase had to produce

The Phase 1 finding: **the Director already has session-scoped credentials; the Gateway does not.** So
repointing the agent tools at the Gateway with the credentials that exist today would mean handing every
agent process the Director's own Gateway key - authority over the whole account, on every machine. That is
a strictly larger hole than the network port the mission removes, so the mission could not proceed until
the Gateway had a session credential of its own.

It now has one.

## The shape of it

**One key per session, minted by the Director, recognised by the Gateway from a stored hash.**

The Director's own session credential (`DirectorScopedToken`) needs no storage: the Director holds the
machine secret, so it can re-derive and verify the signature on every request. The Gateway cannot do that,
and must never be given a secret from which any session's credential can be derived - a Gateway compromise
would then mint credentials for every session on every machine it serves. So a Gateway session key is not
derived from anything. It is 256 bits of randomness, and the Gateway stores only its SHA-256.

| | |
|---|---|
| **Minted** | `GatewaySessionKey.Mint()` in Core, at session launch, by the Director |
| **Delivered** | stamped into that ONE session's environment as `CC_GATEWAY_SESSION_KEY`, beside `CC_GATEWAY_URL` |
| **Registered** | the HASH only, over the tunnel the Director already holds (`DirectorHub.RegisterSessionKey`) |
| **Stored** | session id, owning tenant, Director id, key hash, issued-at, expiry - never the key |
| **Verified** | `AuthMiddleware`, Bearer only, which stamps the calling session id onto the request |
| **Limited** | `SessionKeyGuard` - an allow list of the fleet's agent routes; the account surface is refused |
| **Revoked** | `DirectorHub.RevokeSessionKey`, sent when the session is reaped |
| **Lapsed** | a 12-hour expiry, refreshed on every tunnel reseed, for the paths where no revocation is ever delivered |
| **Retired** | an hourly Gateway sweep tombstones lapsed rows - housekeeping only, since a lapsed key is already refused at resolution |

**The raw key exists in exactly two places** - the Director process that minted it, and the environment of
the one session it belongs to. It is never on the wire, never in the Gateway's memory, and never in its
database. The Director itself keeps only the hash after handing the key over (`SessionGatewayKeys`), so a
Director process is not a place from which every session on the machine could be impersonated.

**The tenant never comes from a payload.** It is taken from the tenant the registering Director's tunnel
bound to at `Hello`, which was resolved there from that Director's authenticated device key. There is
deliberately no tenant field in the registration message. Both mutations - register and revoke - are scoped
by that tenant, so a Director can only ever create or end its own account's session keys.

**No fallbacks.** A session key either works or the call is refused; it is never replaced by a machine
token. A scope refusal is *terminal* in the auth gate - it returns rather than falling through to consider
a cookie the request happens to carry. Without that, any agent that also held a machine credential could
reach the account surface and the refusal would never be visible, which would make the guard advisory
rather than a boundary.

## The line the guard draws

A session key may do the fleet work an agent's command line does: read the roster, repositories, worktrees,
machines, directors and launchers; read a session's terminal; prompt, interrupt, hold, rename, compact, and
mark sessions done; take a role or a mission; spawn a session or launch an application on a machine; read
and publish the fleet's shared skills and workflows.

It may not touch the account: no sign-in or sign-out, no device enrollment or revocation, no credits or
subscription, no Gateway or Director settings, no shutdown, no Director registration, no diagnostics
surface. Those are the owner's.

**Two things in that list are worth the Architect's attention.** `POST /machines/{m}/sessions` (spawn) and
`POST /machines/{m}/launch` (start an application) are both code execution on a computer, and both are
allowed. They are allowed because both are what the fleet's agents do all day through the command line
today, and phase 1b is a credential change rather than a capability change - narrowing them is a product
decision for the owner, not something to smuggle in behind a credential refactor. What the phase *does* add
over today is a boundary that did not exist: the key is bound to one session and one tenant, so those verbs
can only ever act inside the account that issued them.

**A related fact worth recording, because it sizes phase 2.** `ControlApiGuard.CheckSessionChild` limits the
Director-side session credential to its own session plus a tiny discovery set - it cannot spawn, prompt
another session, or broadcast. Agents nevertheless do all of those today, because `cc-devthrottle` does not
use the session credential at all: `tools/cc_shared/director_token.py` reads the machine secret off disk and
mints itself the full-authority `cli` scope. So the agent route set this guard allows is deliberately wider
than `CheckSessionChild` - it has to cover what the tools actually do - and phase 2 repointing the tools is
also the moment the command line stops reading the machine secret.

## Files

New:

- `src/CcDirector.Core/Security/GatewaySessionKey.cs` - mint and hash, in Core so both sides hash identically
- `src/CcDirector.Gateway.Contracts/SessionKeyMessages.cs` - the registration carried over the tunnel
- `src/CcDirector.Gateway/Data/Entities/SessionKeyEntity.cs` - the `session_keys` row
- `src/CcDirector.Gateway/Pairing/SessionKeyRegistry.cs` - register / resolve / revoke / sweep
- `src/CcDirector.Gateway/Util/SessionKeyGuard.cs` - the allow list
- `src/CcDirector.ControlApi/SessionGatewayKeys.cs` - the Director's hash-only record of live sessions
- SQLite migration `20260803154446_AddSessionKeys`, Postgres migration `20260803154516_AddSessionKeys`

Changed:

- `AuthMiddleware` - the session-key verification branch, the calling-session item key, the 403 refusal
- `HostedTenantBoundary` - resolves a session-key caller's tenant from the identity the gate stamped
- `DirectorHub` - `RegisterSessionKey` / `RevokeSessionKey`, both bound to the connection's tenant
- `GatewayStreamClient` - the register and revoke sends, and the reseed leg that re-registers everything live
- `SessionManager` - stamps `CC_GATEWAY_URL` + `CC_GATEWAY_SESSION_KEY` as a pair
- `ControlApiHost` - wires the mint-and-register source and the revoke-on-reap
- `GatewayHost` - constructs the registry, passes it to the auth gate and to the hub, runs the hourly sweep
- the two tenant-scope guard allowlists - `session_keys` declared global, with the reason

## Proof

- **Both parked suites green** - `CcDirector.Gateway.Tests` 2456 passed / 0 failed, `CcDirector.Core.Tests`
  4197 passed / 0 failed. These are the suites the gate flags as a coverage gap for a change like this, and
  they are where `ControlApiGuardTests`, `ControlApiAuthReapplyTests` and the route-surface guards live.
- **Every other project green** across repeated full runs, after deleting every `obj`/`bin` under `src` and
  `tools` (this repo serves stale assemblies on incremental builds).
- **New tests: 49.** `SessionKeyGuardTests` (the allow list, route by route, and the shapes that could walk
  around it - case, trailing slash, method, unknown sub-routes), `SessionKeyRegistryTests` (the key is never
  stored, rotation, revocation, the revoked-not-revived race, cross-account revocation refused, lapsing, the
  sweep, survival of a Gateway restart), `SessionKeyAuthTests` (it works, the session id is stamped, refused
  out of scope with 403, refused when reaped, refused on a cookie, refused with no registry wired, and the
  scope refusal does not fall back to the machine token), `SessionGatewayKeysTests` (the Director keeps no
  raw key, and a forgotten session stops riding the reseed), `GatewaySessionKeyTests` (the hash format,
  pinned in the words of the algorithm rather than by calling the code back on itself).
- **Detectors validated by fault injection**, both after committing:
  - neutering `SessionKeyGuard` so it allows everything: **31 tests red**.
  - neutering the session-key verification branch in `AuthMiddleware`: **5 tests red**.
  - A first injection attempt produced unreachable code, so the build failed and `--no-build` re-ran the
    stale assembly and reported green. That green proved nothing and was discarded; the injections above
    build clean.
- **Both EF snapshots in sync** - `has-pending-model-changes` reports no changes on SQLite and on Postgres.

### The one thing the gate reports red, and why it is not this phase

`CcDirector.Gateway.UnitTests` reports 1-3 failures on most full runs, always the same family:
`InvalidOperationException: The collection has been marked as complete with regards to additions` from
`FileLogWriter.Enqueue`, and occasionally an `ObjectDisposedException` on a SQLite handle. **A different set
of tests fails each time**, and the tests that fail pass when run alone and passed on an identical-binary
rerun of the whole suite (2871/2871, zero failures).

I did not leave that as an assumption. I cut a worktree at `origin/main` - none of this phase's code in it -
built it, and ran the same suite **four times**: 2, 1, 3 and 3 failures, with the identical exception and a
different set of tests each run. So the flake is pre-existing and has nothing to do with session keys. It is
a teardown race in the shared test fixtures, and the fleet is already on it: there is a live worktree
`dt-filelog-lifetime` on branch `fix/filelog-writer-lifetime`.

None of the failures across any run was one of this phase's tests.

## What is NOT proven, and the one honest gap

**The launch window.** The registration is sent the instant the key is minted - inside the environment
build, before the agent process is launched, let alone booted - so it goes up an already-open tunnel while
the agent is still starting. In practice it is accepted long before the agent's first command. But it is
not *awaited*: session creation must never block on the network, and `CreateSession` is synchronous. So a
sufficiently slow Gateway and a sufficiently fast agent could in principle produce one refused call. It
would be refused, not silently downgraded, and the next reseed registers the key. I am recording it rather
than claiming it is impossible.

**No end-to-end run.** Nothing here has been exercised against a live Director and a live Gateway with a
real agent presenting the key - the proof is the unit and integration level plus the fault injections. The
end-to-end exercise arrives naturally in phase 2, when the tools actually present this credential; until
then nothing reads `CC_GATEWAY_SESSION_KEY`, so an end-to-end test would have to fabricate its own caller.

**Nothing consumes the calling session id yet.** `AuthMiddleware` stamps it and `HostedTenantBoundary`
resolves the tenant from it; no endpoint reads the session identity to scope a decision. That is correct for
this phase - the credential exists before anything uses it - but it means the "stamps the calling session id
onto the request" half is proven by a test rather than by a consumer.

**`MISSION-PLAN.md` does not exist.** `MISSION.md` line 81 points at it for the full phase detail and route
inventory. It is not in the worktree and not in the branch's history. Flagging it rather than inventing one.
