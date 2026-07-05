# Cockpit: the React rebuild (current design)

**Status: COMPLETE.** Epic #967 rebuilt the Cockpit as a React single-page app and the final cutover
(issue #979) retired the Blazor Server Cockpit. This document is the authoritative current design and
supersedes the Blazor design in [COCKPIT_DESIGN.md](COCKPIT_DESIGN.md) (kept for historical background).

## What the Cockpit is now

The Cockpit is a **React + TypeScript single-page app** (`apps/cockpit`) served by the Gateway at the
site root `/`. It is thin over one shared TypeScript library, `@devthrottle/client-core`
(`packages/client-core`), which the mobile shell (`apps/mobile`, served at `/m`) shares. Every shared
concern - the typed Gateway client, history rendering, the terminal byte-stream engine, dictation,
device auth, and session ordering - lives in the package; each app owns only its screens, routing, and
styles.

## Why the rebuild (the problem with Blazor Server)

The old Cockpit was a Blazor Server app: a stateful server-side component tree driven over a SignalR
circuit. That model hurt exactly where the Cockpit is most used - remotely:

- **Connection loss froze the interface.** A dropped SignalR circuit froze the page behind a
  "Reconnecting..." overlay; exceeding the reconnection window lost component state and forced a reload.
- **Terminal keystrokes rode the circuit.** Output already bypassed SignalR (a direct WebSocket), but
  input did not, so a degraded circuit let you watch a session but not type into it.
- **We paid the circuit cost for no benefit.** The Cockpit was already a thin REST client wearing a
  stateful server circuit; all real intelligence runs on the Director behind REST.

The React app talks **only to the Gateway** through root-relative paths, so there is no server-side
circuit to lose: a connection blip is a normal fetch retry, and the last view stays on screen.

## Hard rule (must not regress)

**The browser talks only to the Gateway, never a Director directly.** Every request is a root-relative
path; the token rides as the `cc-gateway-token` cookie for the WebSocket. An ESLint "ingress" rule bans
absolute `http`/`https`/`ws`/`wss` literals in `packages/**` and `apps/**` so a Director address can
never leak to the client. This keeps one inbound port to the whole fleet.

## How it is served (the one front door)

- The Gateway serves the built React bundle as static files at `/` and falls every unknown Cockpit page
  path back to `index.html` so the React router resolves it (`CockpitReactApp` in
  `src/CcDirector.Gateway/Cockpit/`). This is the exact analog of how the mobile app is served at `/m`.
- Dual-use paths that are BOTH a JSON API and a Cockpit page (`/sessions`, `/directors`, `/cockpit`) are
  disambiguated by `Accept: text/html`: a browser navigation is served the React shell, a program gets
  JSON.
- The direct-WebSocket terminal is reverse-proxied by the Gateway unchanged
  (`/sessions/{sid}/stream`).

## How it ships

- **Dev / isolated builds:** a routine `dotnet build` does NOT run the front-end build. A Release
  publish runs the release-gated `BuildCockpitApp` MSBuild target on `CcDirector.Gateway.csproj`, which
  builds `apps/cockpit` and stages `dist/**` into `wwwroot/c` beside the Gateway exe.
- **Production release:** the single-file Gateway exe carries no loose content, so the built `wwwroot/c`
  ships as its own side-car asset `devthrottle-gateway-cockpit-win-x64.zip` (the same delivery pattern
  as the mobile app). The setup engine's `CockpitAssetPackage` unpacks it into `wwwroot/c` beside the
  exe on install and self-update.
- **Dev fleet deploy:** `scripts/redeploy-gateway.ps1` copies the whole published `wwwroot` tree
  (`wwwroot/m` + `wwwroot/c`) beside the exe. There is no separate Cockpit deploy script any more.

## What the #979 cutover removed

- The `src/CcDirector.Cockpit` Blazor project and its test project, and their solution entries.
- The Gateway's Blazor reverse-proxy and supervisor (`CockpitProxy`, `CockpitSupervisor`) and the
  side-by-side `/c` vs Blazor routing split - the React app is now the Cockpit at the canonical path.
- The Blazor Cockpit as an installable/updatable component (`ComponentRegistry.Cockpit`,
  `CockpitPackage`, `CockpitUpdater`, `ComponentKind.Cockpit`) and its separate `devthrottle-cockpit`
  process/port (7470).
- The Blazor `build-cockpit-win` CI job and the `deploy-cockpit.ps1` dev-deploy script.

## See also

- `apps/cockpit/README.md` - the app's own build/run/layout notes.
- `../gateway/` - the Gateway/Director split the Cockpit builds on.
- [COCKPIT_DESIGN.md](COCKPIT_DESIGN.md) - the historical Blazor design.
