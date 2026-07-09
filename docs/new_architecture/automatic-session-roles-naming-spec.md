# Automatic session roles + naming - implementation spec

**For:** STREAM WORKER 2 (`0557263a`). **Controller:** `c9f9a8e3`. Branch `feat/director-gateway-stream-1a` (the saved pile - build on top). Build/test `dotnet` + npm. **Do NOT commit** (controller commits after review).
**Design owner for role semantics:** session `f33df855` (fleet-mgr); this spec is the agreed division. Related: `docs/new_architecture/session-roles-semantics.md` (main tree).

## What the owner asked for ("automatic naming")
Sessions automatically know WHAT THEY ARE and name/color themselves accordingly - no manual tagging:
- A session spawned BY another session = **Worker**: stays quiet, reports to its manager, its "needs you" never surfaces red to the human.
- A session started by the human/desktop = **Manager** or **Standalone**: human-facing, red allowed.
- Each auto-gets a name reflecting its role + job.

## The model (agreed with the roles owner)
- **SessionRole { Standalone, Worker, Manager }.**
  - **Worker** = RAW, at birth: the session has a `ControllerSessionId` (was spawned by another session). Signal already exists as `Session.IsControlled`.
  - **Manager vs Standalone** = COMPUTED from the fleet, NOT stored: a non-worker that controls >= 1 LIVE worker is a Manager; otherwise Standalone. Dynamic (becomes Manager when it gains a live worker, reverts when the last dies). Compute this at the Gateway aggregation (where the whole fleet is visible), like `EffectiveColor`.
  - **Nesting (v1):** a Worker that spawns sub-workers KEEPS the Worker label (its red routes to ITS manager), is NOT relabeled Manager; it still gets the viewer-relative manager highlight for its own children (that highlight is f33d's rail lane, keyed on `controllerSessionId == viewer`, independent of the label).
- **Job-type axis is SEPARATE** (do NOT conflate): the CLI's dangling `--type` (Developer/Implementation/Discuss/Product/QA/Support) is an orthogonal job-type, silently dropped server-side today. Do NOT build SessionRole on it and do NOT collide. Leave it for a separate small decision (f33d flags to Soren).

## TWO REQUIRED GUARDS (correctness, from f33d - non-negotiable)
1. **Opt-out:** the auto-worker default must be overridable - a session must be able to spawn a PEER (human-facing, not a subordinate). Provide `--standalone` (or `--controlled-by none`) on `cc-devthrottle session spawn`.
2. **Handover-exclude:** the handover / move-session flow pre-creates a target (via `toSessionId`) as a CONTINUATION/PEER of the source, NOT a subordinate. Auto-set-controller must NOT fire on that path - otherwise a moved session's red gets suppressed toward the human and the user loses sight of it. Ensure the auto-controller default applies ONLY to `session spawn`, never the handover/move-session flow.

## Chunks (build + test + report to controller + wait, per chunk)

### Chunk 1 - SessionRole on the wire + the fold (Gateway/Contracts, MY core)
- Add `SessionRole` (enum or string) to `SessionDto`, COMPUTED at the Gateway aggregation: `IsControlled && controller-alive -> Worker`; else `controls >=1 live worker -> Manager`; else `Standalone`. (Reuse the fleet view the aggregation already has; "controller-alive" is the same liveness check the slate rule uses.)
- **Fold change** in `SessionOrdering.EffectiveColor` (the single fold): a **Worker** (IsControlled + controller alive) -> its "needs you"/red is SUPPRESSED and it reads quiet/manager-facing (recede, like slate) - red does NOT break through for a Worker. Manager/Standalone -> human-facing, red allowed. **Escape hatch:** Worker with a DEAD controller -> red ALLOWED (surfaces to human) - already native to the fold's controller-alive keying. This is the "workers never nag the human" enforcement (global Layer 1; the viewer-relative manager highlight is f33d's rail, Layer 2).
- Expose on the DTO the four facts f33d's rail consumes (already present or add): SessionRole, ControllerSessionId, ActivityState, and `NeedsManager` (add as an optional bool raw fact, default false - for a still-Working worker to explicitly escalate; wire minimally now).
- Tests: role computed correctly (worker/manager/standalone incl. the dynamic manager-gains/loses-worker); the fold suppresses red for a live-controlled Worker, allows red for Manager/Standalone and for a dead-controller Worker; existing SessionOrdering cases unchanged.

### Chunk 2 - auto-set controller on spawn + the two guards (CLI + create)
- `cc-devthrottle session spawn` (`tools/cc-devthrottle/src/{cli.py,session_ops.py}`): DEFAULT `controllerSessionId = CC_SESSION_ID` when that env var is present (the spawner), so a session-initiated spawn is automatically a Worker. `--standalone` (guard 1) opts out (no controller = peer). Human/desktop create (no CC_SESSION_ID) is unaffected.
- Handover-exclude (guard 2): confirm the handover / move-session path does NOT go through `session spawn`'s auto-default; if it shares code, gate the auto-default so it never fires for a pre-created `toSessionId` continuation/peer.
- Tests: spawn with CC_SESSION_ID set -> controllerSessionId defaulted; `--standalone` -> none; handover path -> never auto-set.
- **ALSO in this chunk (same files, avoids a merge conflict):** REMOVE the dead `--type` CLI option - `cli.py` (~line 342, the `--type` option) + `session_ops.py` (~line 354, `session_type -> body['type']`). It is pure dead code (the server has no `Type` field and drops it), the owner decided to remove it (management axis only, no job-type axis), and it lives in the exact files you're editing here. Delete both; nothing server-side to change.

### Chunk 2.5 - ARCHITECT (4th role) + the explicit-role layer (added by owner mid-build)
Soren made Architect a 4th role. It cannot be auto-derived (you can't infer "is designing architecture" from the spawn graph), so it adds an explicit-role layer over the auto-derivation.
- Add `Architect` to the `SessionRole` enum: Standalone, Manager, Worker, Architect.
- Add an EXPLICIT-ROLE raw fact: a stored, sticky nullable role on `Session` (e.g. `ExplicitRole`), mapped onto the DTO. Settable two ways: at spawn via a new `NewSessionRequest.Role`, and post-spawn via a new `set-role` command verb (in `SessionCommandExecutor`, routed through the same DirectorCommandRouter as the other verbs) so a session can be made or self-declare an Architect.
- **Resolution precedence** in the aggregation post-pass (`StampFleetRolesAndFold`): `ExplicitRole` if set WINS; else Worker (controlled AND controller alive); else Manager (controls >= 1 live session); else Standalone. An explicit role is sticky and auto-derivation NEVER overwrites it.
- **Manager-derivation now excludes Architect:** a non-worker, NON-ARCHITECT controlling >= 1 live session is a Manager.
- **Fold: NO change.** The fold only suppresses Worker; it reads the RESOLVED role, so an explicit Architect (even one that happens to have a controller) stays Architect and is human-facing (red allowed, never suppressed) because explicit wins over the Worker derivation.
- Tests: explicit role wins over derivation; an explicit Architect with a controller is Architect (red NOT suppressed); Manager-derivation excludes Architect; `set-role` changes the role and it sticks; existing worker/manager/standalone derivation unchanged when no explicit role.
- **INCLUDE the minimal `--role` CLI passthrough in THIS chunk** (f33d confirmed - branch topology forces single ownership of `cli.py`/`session_ops.py` this window): add a `--role` option to `cc-devthrottle session spawn` that forwards VERBATIM to `NewSessionRequest.Role` (valid values Standalone/Manager/Worker/Architect). Land it in the SAME chunk as the server-side `NewSessionRequest.Role` field so there is never a sent-but-silently-dropped flag (the exact `--type` situation we just removed).
- NOT in this chunk (f33d's lane): the badge (4th glyph), the higher-level "become architect" UX, the spawn-recipe doc, the deferred UI, and the Architect BEHAVIOR (design-only, notifies its manager) - all calling your field / set-role verb / `--role`.

### Chunk 3 - auto-naming + IsAutoNamed (Core + create + rename)
- Auto-compose a role-flavored name at birth (Worker -> its task/purpose-flavored name; Manager/Standalone -> repo default as today). Hook: `nameFactory` / `SessionName.Compose` (`SessionCommandExecutor.cs:357`).
- Add persisted `Session.IsAutoNamed` (bool). Set true when the name was auto-composed; a later explicit `RenameSession` sets it false (a self/human name always wins and is never re-auto-named). This closes the recon gap (today rename blindly overwrites, no auto-vs-explicit marker).
- Tests: auto-named worker gets a role-flavored name + IsAutoNamed=true; an explicit rename wins and clears IsAutoNamed; an auto-name is not clobbered by role recompute once a human has renamed.

## Non-negotiables
- Additive; the fold Worker-suppression is the one deliberate behavior change (owner-approved via the roles direction). CodingStyle (no `!`, FileLog, try-catch at boundaries only, warnings-as-errors, tests). Do NOT commit. Report each chunk to the controller and WAIT for verify before the next.
