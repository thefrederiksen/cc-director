# Issue 1214 - Session menu actions - proof

Phase 5 of the Cockpit improvement plan. A session menu (Rename, Put on hold / Resume,
Handover info, Close session) is on the session page and on every rail card. All actions go
Cockpit -> Gateway with relative URLs through the shared client. Handover info reads a new
read-only Gateway endpoint that proxies the owning Director (the browser never learns a
Director address).

## What changed (Cockpit UI)

- `apps/cockpit/src/sessions/SessionMenu.tsx` (new) - the three-dot menu with all four actions,
  used by BOTH the session page and the rail cards. Close asks for confirmation. Rename is a
  small dialog. Handover info fetches `getHandover` and shows the fields. A failed action shows
  a visible error (never a silent failure). The dropdown is portalled to `document.body` so the
  scrolling rail never clips it.
- `apps/cockpit/src/sessions/SessionDetail.tsx` - the menu in the session header (top right),
  Close navigates back to the roster.
- `apps/cockpit/src/sessions/SessionRoster.tsx` + `styles.css` - the same menu on each rail card.

## Backend (merged from the handover-info work, folded into this pull request)

- `GET /sessions/{sessionId}/handover` on the Gateway proxies the owning Director and returns
  `{ sessionId, displayName, repoPath, directorId, machineName, version }` - the same identity
  block the desktop "Copy Handover Info" shows, minus the Director address (never leaked). Same
  Bearer/device-key auth as the other session routes: 401 without a valid credential.
- `getHandover(sessionId)` in `packages/client-core/src/api/client.ts`.
- Gateway + client-core unit tests (auth-401, happy path, unknown-session-404).

## Proof screenshots (dev server against the live Gateway, Playwright)

- `menu-page-open.png` - the session-page menu open: Rename, Put on hold, Handover info, Close
  session; the same three-dot is on every rail card.
- `menu-rename.png` - the Rename dialog, prefilled with the current name (Cancel / Save).
- `menu-close-confirm.png` - Close asks for confirmation before it removes the session.
- `menu-rail-open.png` - the SAME menu open on a rail card (portalled, not clipped by the rail).
- `menu-handover.png` - the Handover info dialog. NOTE: the LIVE production Gateway on this
  machine runs the old build without the new `/handover` endpoint, so this capture shows the
  dialog surfacing the Gateway's error ("The Gateway rejected the request (error 404)") - which
  also demonstrates the "a failed action shows a visible error, never silent" criterion. Once
  this pull request's Gateway is deployed, the dialog shows the handover fields (the endpoint is
  unit-tested for its shape).

## Automated checks

- `tsc --noEmit` (cockpit) clean; `vite build` clean.
- client-core `vitest run` passes (includes the merged `handover.test.ts`).

## Owner-hardware acceptance items (live mutation + the desktop app)

Rename / Hold-Resume / Close call the exact shared functions the mobile app already uses in
production (`renameSession`, `holdSession`, `killSession`); I deliberately did not mutate the
owner's live working sessions from the harness. Please verify on a REMOTE session:
- Rename it from the browser and see the desktop app show the new name.
- Hold then Resume it and see both clients agree.
- Close it (confirm) and see it leave the roster within one refresh.
- Open Handover info against the deployed Gateway and compare the fields with the desktop
  "Copy Handover Info".
