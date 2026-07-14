# Mission: Mission Screen

- Date opened: 2026-07-12
- GitHub issue: #1405
- Architect: session #106 "Mission Screen - Architect" (id 3457ddcf)
- Manager: session "Mission Screen - Manager" (spawned on the same Director)
- Mockup: https://claude.ai/code/artifact/37e81651-5243-47a3-8901-b67292563589

---

## WHY (mandatory - this comes first)

When many agents are running at once, the owner loses track of what is being built and why.
Holding every mission in your head does not scale past a handful of sessions, so missions drift
and low-value "because it's cool" work sneaks in. The Mission Screen gives one place that shows
every mission - **with its WHY front and center** - grouped by the unit of work the owner actually
thinks in (the mission, not the session), plus (later) something to talk to about "what should we
do next." It keeps the owner oriented and keeps us honest about what deserves to be worked on.

**Rule this mission also establishes for ALL missions:** every mission must state a clear WHY, and
that WHY is shown front and center on the Mission Screen. A mission with no WHY is a red flag the
screen makes obvious. This is dogfooded here: the section you are reading is that WHY.

---

## What we are building

A new **Missions** page in the Cockpit (a left-rail nav destination under "Fleet"). Same live fleet
data as the Fleet Map, seen a different way: sessions grouped into **missions**, alphabetical, with a
**Standalone** group for sessions that are not part of a mission. Each mission is a card showing its
name, its WHY, its repo, a status summary, and one row per session that links to open that session.
Later, a chat panel ("Mission Control") to reason about the missions.

Principle: **code builds the map, the LLM reasons over it.** The list is deterministic and needs no
model; the chat (a later phase) is where judgment lives.

---

## Phases

- **Phase 1a (Manager's job NOW): the grouped mission list + linking.** Pure client. Ships today.
- **Phase 1b (right after 1a, same mission): the WHY** - a durable, shared store for each mission's
  WHY, shown on every card, editable inline, with a loud "No why set" flag when missing.
- **Phase 2 (later): the chat panel** - a Car-Mode-style tool-calling brain over the fleet tools.
- **Phase 3 (later): first-class mission objects + an API** so Car Mode can query the Mission Screen.

Prove each phase in the running Cockpit before starting the next (see Working rules).

---

## Phase 1a - concrete scope (do this first)

Build a read-only **Missions** page that groups the live fleet by mission and links into sessions.

Acceptance (prove in the running Cockpit, with a screenshot in the PR):
1. New nav item **"Missions"** under the Fleet section; route `/missions` renders the page.
2. The page reads the ONE shared roster store (no new poll, no Director address).
3. Sessions are grouped into missions, **alphabetical**, **Standalone** group last.
4. Each mission is a card: mission name, repo(s), session count, a status summary, and a **WHY slot**
   (Phase 1a shows "No why set - add one" until 1b wires the store).
5. Each session renders as a row: number badge, role, machine, live state (reuse the shared color +
   label rule), and clicking it opens the session (`/session/:id`) - this is the linking.
6. Loading / empty / error states handled like the Fleet Map.
7. New CSS file for the page; follow `docs/VisualStyle.md` (the Cockpit dark palette).

### How to derive the mission (important)

The Cockpit's `SessionDto` (from the Gateway roster) does **NOT** carry `missionName` / `sessionRole`
- those exist in the `cc-devthrottle session list` CLI view but are not in the Cockpit schema
(`packages/client-core/src/api/schema.ts`, the generated `SessionDto`). So for Phase 1 derive the
mission from the session **name**, which follows the convention **"<Mission> - <Role>"** (dash, role
second - roles seen: Architect, Manager, Worker):

- If the name matches `"<something> - <Role>"`, the mission is `<something>` and the role is `<Role>`.
- Otherwise the session goes in **Standalone** (its own group), shown by its own name.
- Group case-insensitively; display the mission name as first seen.

Do NOT add backend fields for Phase 1a. (Exposing `missionName`/`sessionRole` on the Cockpit
`SessionDto`, or making missions first-class Gateway objects, is a Phase 3 decision - bring it to the
Architect, do not decide it solo.)

## Phase 1b - the WHY store (right after 1a)

The WHY must be **durable and shared** (not per-browser localStorage - every client and the future
chat/API must read the same WHY). Recommended smallest form: a Gateway **mission-notes** store keyed
by mission name (GET to read all, PUT `{mission, why}` to set one), surfaced on the card and editable
inline. A missing WHY renders as a loud flag, never silently blank. **Confirm this shape with the
Architect before building the endpoint** - it is the first toe into "missions as real objects."

---

## Codebase pointers (verified 2026-07-12)

- **Nav + shell:** `apps/cockpit/src/AppShell.tsx` - `NAV_SECTIONS`, add `{ to: "/missions", label:
  "Missions" }` under the "Fleet" section.
- **Routes:** `apps/cockpit/src/main.tsx` - register the `/missions` route and import the new view +
  its CSS (follow how `FleetMapView` / `fleetmap.css` are wired).
- **The data + patterns to copy:** `apps/cockpit/src/fleet/FleetMapView.tsx` - reads
  `useSharedRoster()` from `@devthrottle/client-core/fleet/rosterStore` (sessions, machineErrors,
  directors, error). It already groups the fleet by pivots (machine/repo/agent) and links cards via
  `navigate('/session/:id')`. The Missions page is the same idea with a "by mission" grouping and a
  card-per-mission layout. Reuse it as the template; do not re-invent the roster plumbing.
- **Shared color + label:** `dotColor`, `effectiveColor`, `stateLabel` from
  `@devthrottle/client-core/sessions/ordering` - use these so a status dot matches every other surface.
- **Types:** `SessionDto` from `@devthrottle/client-core/api/client` (generated in
  `packages/client-core/src/api/schema.ts`). Fields you have: `name`, `number`, `repoPath`,
  `machineName`, `directorId`, `sessionId`, `groupId`, `groupRole`, `effectiveColor`,
  `lastStatusReason`, `lastActivityAt`, `createdAt`, `agent`. (No mission/role field - derive from name.)
- **CSS reference:** `apps/cockpit/src/fleet/fleetmap.css` for the card / lane look; `docs/VisualStyle.md`
  for the palette (panel #1E1E1E, sidebar/card #252526, accent #007ACC, red = needs you, blue = working).
- **Format helpers:** `apps/cockpit/src/fleet/format.ts` (`repoBasename`, `relativeTime`).

---

## Design decisions already made by the Architect (do not re-litigate)

1. Grouping is **derived from the session name** for now (no backend change in Phase 1a).
2. Phase 1a ships the mission **list full width** - NO empty chat pane. The two-pane (chat left,
   list right) layout arrives with the chat in Phase 2. Do not ship a placeholder chat pane.
3. Reuse the shared roster store and the shared color/label rule - never a second poll or a Director
   address, never a private color mapping.
4. The WHY is a first-class slot on every card from Phase 1a (shown empty with a flag until 1b).

## Bring these to the Architect (design questions - not the user)

- The WHY store shape (Phase 1b) before building any endpoint.
- Whether/when to make missions first-class Gateway objects and expose mission/role on the Cockpit
  `SessionDto` (Phase 3).
- Any layout question about how the chat and list share the page in Phase 2.

The Architect (session #106) settles design; you drive the build. Ping the Architect at each
milestone, decision, or block - do not stall silently.

---

## Working rules (fleet standards)

- Trunk-based: build on a branch in your **own git worktree** off `origin/main` (never `checkout -b`
  in the shared tree), open a pull request, drive it to **merged on origin/main** - that is the only
  "done". Delete the branch and worktree on merge.
- Stage only the files you created/changed **by name** - never `git add -A` in the shared tree.
- Commit only when the owner asks; but once a phase is approved, finish it to a merged PR.
- Prove it in the running Cockpit (screenshot in the PR) before calling a phase done. Do not
  "merge-for-hours-then-test".
- Plain English, no abbreviations. **No unicode / emoji anywhere** (ASCII only) - this repo is strict.
- Enterprise standards apply (`CLAUDE.md`, `docs/CodingStyle.md`, `docs/VisualStyle.md`): responsive
  UI, logging, no fallback programming, tests for logic (the mission-derivation parser needs unit tests).
