# QA verification - Issue #979 (retire the Blazor Cockpit, cutover) - FAILED

QA independently built, stood up an isolated Gateway + Director (own root via `CC_DIRECTOR_ROOT`,
Gateway on `:8479`, own PR build `1.0.5+0b50e439`), and drove the running app. One acceptance
criterion fails.

## Verdict: FAIL - 1 reproducible defect

### DEFECT: a hard navigation / browser reload / deep-link to `/lists` returns raw JSON, not the Lists page

**Acceptance criterion missed:** "A full drive-through on a real fleet (... every page) works end to
end" and the epic #967 hard rule "deep links route into the shell."

**What happens:** with the React Cockpit served at the site root `/`, requesting `/lists` with a
browser `Accept: text/html` (i.e. reload/F5 on the Lists page, opening a `/lists` bookmark/deep-link,
or opening `/lists` in a new tab) returns `200 application/json` `{"lists":[]}` - the raw Work-List
API payload - instead of the React shell. Every other client route (`/fleet`, `/directors`,
`/schedule`, `/wingman`, `/dictionary`, `/transcripts`, `/exes`, `/learn`, `/telemetry`, `/account`,
`/about`, `/feedback`) correctly falls back to the shell.

**Proof (live, isolated Gateway :8479):**
```
GET /lists      (Accept: text/html) -> 200 application/json; charset=utf-8   {"lists":[]}   <-- WRONG
GET /schedule   (Accept: text/html) -> 200 text/html   <!doctype html> (React shell)         OK
GET /fleet      (Accept: text/html) -> 200 text/html   <!doctype html> (React shell)         OK
... (all other routes serve the shell)
```
See `page-lists.png` (Chrome renders the JSON body for `/lists`) next to `page-root-sessions.png`
and the other `page-*.png` (the correct React pages).

**Root cause:** `WorkListEndpoints.cs:54` maps `MapGet("/lists", () => Results.Json(new { lists = ... }))`,
an explicit GET endpoint that wins over the SPA `MapFallback`. The dual-use browser-page detection
that is supposed to serve the shell to a person on such a shared API/page path -
`CockpitReactApp.BrowserPageRoots = { "cockpit", "sessions", "directors" }` - omits `"lists"`, so
`IsBrowserPageRequest("/lists")` is false and the request is served the JSON endpoint.

**Introduced by this issue:** before the cutover the React app lived at `/c`, so the Lists page was
`/c/lists` and `/lists` was API-only - no collision. Flipping the front door to `/` created the
collision; the fix belongs in this PR's own routing layer.

**In-app navigation still works** (the nav uses react-router `NavLink`, client-side), so this only
bites on hard navigation: reload, deep-link, bookmark, new tab.

**Expected:** `GET /lists` with `Accept: text/html` serves the React shell (200 text/html), exactly
like `/schedule`/`/fleet`.
**Actual:** `GET /lists` serves `200 application/json {"lists":[]}`.

**Suggested fix:** add `"lists"` to `CockpitReactApp.BrowserPageRoots` (and audit every other
top-level GET JSON endpoint for the same client-route collision; `/lists` is the only one among the
current client routes). Add a routing test asserting `/lists` with an HTML Accept serves the shell.

## What PASSED (for context - do not re-verify, just the failing criterion needs a fix)

- Build gate all green: cockpit `npm run build` (root-relative `/assets/...`), `npm run lint` (0),
  `dotnet build -p:RunCockpitBuild=true` (stages `wwwroot/c`), `dotnet build cc-director.sln`
  (0 warnings, 0 errors).
- Blazor fully gone: `src/CcDirector.Cockpit(.Tests)` deleted; zero references anywhere;
  `CockpitProxy`/`CockpitSupervisor`/`build-cockpit-win` removed; YARP remains only for the terminal
  WebSocket proxy (intended). `/_blazor` now serves the shell (no live circuit).
- React served at the canonical root `/`: live routing matrix confirmed (shell at `/`, assets,
  SPA fallback, dual-use JSON vs HTML for `/sessions` + `/directors`, `/cockpit` in-process no `:7470`).
- Live session drive-through: a real RawCli PowerShell session rendered in the terminal; **typing
  reached the PTY** end-to-end through the Gateway WebSocket proxy (`echo SLOWMARK987` executed);
  Brief/History/Awareness tabs, Queue/Screenshots, Stop/Interrupt, and the composer all render.
- Deploy story coherent: `release.yml` packages `devthrottle-gateway-cockpit-win-x64.zip` from
  `wwwroot/c`; `redeploy-gateway.ps1` asserts `wwwroot/c/index.html`; self-update applies
  `CockpitAssetPackage`. No script calls the deleted `deploy-cockpit.ps1`.
- Baseline: the pre-existing red `NoCrossMachineLoopbackGuard` test's two files
  (`DesktopHostedAiCta.cs`, `GatewayConfig.cs`) are NOT changed by this PR - the failure is
  pre-existing, not introduced by #1016.
