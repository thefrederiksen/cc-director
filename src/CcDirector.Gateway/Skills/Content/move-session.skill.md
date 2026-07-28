# Move Session

Relocate a live Claude Code session from one cc-director slot to another - on the
same Director or across Directors. The move reads the source session's context,
creates a fresh target session seeded with a handover SUMMARY (which states it is a
MOVED session continuing the source), names the TARGET with the source's ORIGINAL
name, VERIFIES the target picked it up, renames the SOURCE `[MOVED -> ...]`, and PUTS
THE SOURCE ON HOLD so it stops working while it waits to be closed. This skill never
kills the source - the user closes it once they see the `[MOVED]` marker.

## READ FIRST: the control surface is the GATEWAY, not the Director

The tunnel-only migration (v1.1.0) REMOVED the per-session control routes from the
Director's loopback HTTP floor. On any Director port (`:7879`, `:7883`, ...) these
now return **404** and MUST NOT be used:

- `POST {director}/handover`
- `GET {director}/sessions`, `GET {director}/sessions/{id}`
- `PATCH {director}/sessions/{id}` (rename)
- `POST {director}/sessions/{id}/hold`, `.../prompt`, `.../context`

The move/session-control routes you need are NOT on the Director floor. The Director
loopback still serves `/healthz`, the agent-facing `/fleet/*` verbs, and some agent
hook routes (e.g. `/sessions/{sid}/claude-hook`, `/sessions/{sid}/fleet-preamble`),
but not the session-control REST the move needs. Everything this move needs lives on
the **Gateway** (`http://127.0.0.1:7878`) and on the **`cc-devthrottle`** CLI. If a
step here tells you to curl a Director port for a session route, the step is stale -
stop and re-read this section.

Two surfaces, and when to use each:

- **`cc-devthrottle` CLI (preferred where it covers the step).** Token-free,
  address-free - it reaches the fleet through your own Director. Use it to list,
  rename, and hold sessions.
- **Gateway REST (`http://127.0.0.1:7878`, Bearer token).** The only surface that
  exposes the handover itself and the composer nudge. Token comes from
  `%LOCALAPPDATA%\cc-director\config\director\gateway-token.txt` (source of truth:
  `DirectorAuth.cs`). Send it as `Authorization: Bearer <token>` on every Gateway
  call - `/sessions` and all mutation routes are token-gated.

Verify the Gateway is up before you trust any step below:

```bash
curl -s -o /dev/null -w "%{http_code}\n" http://127.0.0.1:7878/healthz
```

`200` = go. Anything else: the Gateway is down or on another port - find its listen
port (`Get-NetTCPConnection -State Listen -OwningProcess <pid>`) and confirm the URL
with the user before proceeding.

## WHY a move happens: CONTEXT RELIEF (read this second)

The whole point of a move is **context relief**. You move a session because it is
LOW ON / OUT OF context. The target MUST start FRESH, with almost its entire context
window free, seeded only with a compact **handover SUMMARY** of where the work is and
what is next. The new session then has headroom to keep going.

Two hard consequences:

1. **DEFAULT ROUTE = the handover summary (`POST {gateway}/handover`).** It creates a
   fresh session and delivers the source's context as a generated summary prompt.
   This is the only route that achieves context relief. Use it unless the user
   explicitly asks for the full verbatim transcript.
2. **NEVER use the resume route for a context-relief move.** `resumeSessionId`
   re-attaches the ENTIRE original conversation - every turn - so the target comes up
   already full. That defeats the purpose. The resume route is a clearly-warned
   opt-in only (see "Resume route"), never the default.

### The target comes up on the configured default agent

The handover does not force a model. The fresh session launches on whatever the
target Director has configured as its default agent - the code creates the target
with no model arguments (same-Director `CreateSession` with `userArgs: null`,
cross-Director `NewSessionRequest` with no `Args`), so the model and window are the
Director's configured defaults, not a value this skill sets. In the current
deployment that default is Opus 4.8 with the 1-million-token window (`opus[1m]`), and
a cross-Director handover was observed landing at `windowTokens: 1,000,000`
(2026-07-08) - but that is the deployment's configured default, NOT a guarantee the
handover makes. Do not promise Opus+1M or tell the user to disregard a smaller
window; if the Director's default ever changes, the target follows it.

(There is no programmatic way for this skill to CONFIRM the window: the Gateway
exposes no `/context` route, and the old `GET {director}/sessions/{id}/context` check
is gone with the rest of the Director session floor. If you must verify, read the
window in the desktop app's session context readout.)

## Session naming convention (applies to EVERY name this skill writes)

Every session name starts with the repo's directory name - the leaf folder of
`repoPath` (e.g. `cc-consult` for `D:\ReposFred\cc-consult`) - followed by a short
description:

```
<repo-dir> - <short description>
```

Examples: `cc-consult - make-money execution`, `mindzieWeb - fix login MFA loop`.

Rules:

- **Fresh name requested:** compose `<repo-dir> - <short description>` from the
  target's repo and the work at hand. Propose it in the plan before approval.
- **Retaining the source's original name (default):** if the source's name already
  carries the repo prefix, keep it verbatim. If not, prepend the prefix
  (`<repo-dir> - <original name>`) and mention the normalization in the plan.
- **The `[MOVED -> ...]` marker** wraps whatever name applies; it never replaces the
  repo prefix (`[MOVED -> 7886] cc-consult - make-money execution`).
- Keep the description SHORT - the rail is narrow and truncates.

Lifecycle in one line:

```
plan -> USER APPROVAL -> Gateway handover -> NAME TARGET (source's original name) -> verify target working -> rename source "[MOVED -> ...]" -> HOLD source -> user closes source
```

## Quick Reference

All Gateway calls use base `http://127.0.0.1:7878` with `Authorization: Bearer <token>`.

| Action | How |
|--------|-----|
| List / number sessions | `cc-devthrottle session list` (fleet-wide) or `GET {gateway}/sessions` (JSON, each entry carries `directorId`, `activityState`, `onHold`) |
| DEFAULT move (context relief) | `POST {gateway}/handover` with `fromSessionId` + `toRepoPath`. Fresh target, summary seed, Director's default agent. Without `toDirectorId` the target is created on the SOURCE's Director; add `toDirectorId` to place it on a different Director. |
| Move into an existing empty slot | `toSessionId` instead of `toRepoPath` (send exactly one). SAME Director as the source only - cross-Director `toSessionId` is rejected; use `toRepoPath` + `toDirectorId` instead. |
| Add a "continue by doing X" note | `extraContext` on the request (MUST open with the moved-session statement) |
| Name the TARGET (retain the name) | `cc-devthrottle session rename <target-id> "<source's original name>"` right after the handover |
| Verify target picked up | `cc-devthrottle session list` (state flips to Working) or `GET {gateway}/sessions/{tid}/buffer` (bytes grow) |
| Unstick a parked seed prompt | `POST {gateway}/sessions/{tid}/prompt` `{"text":"\r","appendEnter":false}` |
| Mark source as moved | `cc-devthrottle session rename <source-id> "[MOVED -> <target>] <old name>"` |
| Put the source on hold | `cc-devthrottle session hold <source-id> --minutes 720` |
| Close the source | THE USER does this, never the skill |

## CRITICAL: the three rules

1. **USER APPROVAL REQUIRED.** `POST /handover` changes live state - it spawns a real
   session and sends it a prompt. ALWAYS show the move plan (exact source, exact
   target, what gets seeded) and wait for an explicit go-ahead before the POST. Never
   fire from a bare slot number without confirmation.
2. **VERIFY BEFORE MARKING.** The source is renamed `[MOVED]` only after the target
   has demonstrably picked up the handover (state `Working`, buffer growing, or the
   seeded prompt visibly submitted). A failed or parked move must never leave a source
   falsely marked as moved.
3. **NEVER KILL THE SOURCE.** This skill renames and holds the source so the user can
   SEE it is safe to close; closing it is the user's call, in the UI, on their own
   time. No delete, no kill, no process termination.

## What a "slot" means

A slot is one session in a Director. The API does not number slots; it returns a list
of sessions (GUID, three-digit number, name, repo, state, `directorId`). This skill
numbers that list 1..N for conversation only. Listing order is NOT the on-screen grid
order, so never move on a bare slot number - always show the numbered list with names
and repos and confirm the exact source.

## Workflow

### Step 1: List sessions and number them

```bash
cc-devthrottle session list
```

For the machine-readable form (needed to capture `directorId` and GUIDs), read the
Gateway roster:

```bash
python - <<'PY'
import os, json, urllib.request
tok = open(os.path.join(os.environ['LOCALAPPDATA'],'cc-director','config','director','gateway-token.txt')).read().strip()
req = urllib.request.Request('http://127.0.0.1:7878/sessions',
    headers={'Authorization': f'Bearer {tok}', 'Accept': 'application/json'})
for s in json.load(urllib.request.urlopen(req, timeout=10)):
    print(s['sessionId'][:8], '|', s.get('directorId','')[:8], '|', s.get('name'), '|', s.get('repoPath'), '|', s.get('activityState'))
PY
```

Present a numbered table including which Director each session lives on:

```
| Slot | Name | Repo | State | Director |
|------|------|------|-------|----------|
| 1 | banya-fixes | D:\ReposFred\cc-consult | Idle | a3a971fa |
```

### Step 2: Identify the source

- "this session" / "move me" -> `$CC_SESSION_ID`.
- a three-digit number (e.g. "412") -> the session with that number.
- "slot N" -> the session at position N in your listing - confirm by name.
- a name or id prefix -> match against `name` / GUID (confirm if ambiguous).

Capture the source `sessionId`, `name`, `repoPath`, `agent`, and its `directorId`.
The target reuses the source's repo and agent by default.

### Step 3: Identify the target

- **Fresh slot on the SOURCE's Director (simplest):** set `toRepoPath` only. With no
  `toDirectorId`, the Gateway creates the target on the source session's own Director
  (it does NOT load-balance across Directors).
- **Fresh slot on a DIFFERENT Director:** `toRepoPath` + `toDirectorId` (the target
  Director's id from the roster).
- **An existing empty session:** `toSessionId` instead of `toRepoPath` (mutually
  exclusive - send exactly one). SAME Director as the source only: a cross-Director
  `toSessionId` is rejected (`not supported in v1`) - to reach another Director, use
  `toRepoPath` + `toDirectorId`.

Set `toAgent` to the source's agent (e.g. `ClaudeCode`).

### Step 4: Optional - gather a continue note

Ask if there is anything the source did not write down that the new session should
pick up. Put it in `extraContext`; skip if nothing.

Whatever else goes in, `extraContext` MUST OPEN with the moved-session statement so
the target describes itself correctly to the user and the wingman:

```
This is a MOVED session: it continues session <source-guid> ("<original name>")
from Director <source directorId/port>. <then any continue note>
```

(The auto-generated handover prompt says "picking up an in-progress session", but
only this line carries WHICH session and WHERE from.)

### Step 5: APPROVAL GATE - confirm the plan

Show the plan in plain language and WAIT for an explicit yes:

```
Move plan:
  From:   slot 1 "banya-fixes" (5a4f4929...)  D:\ReposFred\cc-consult  (Idle)  Director a3a971fa
  To:     NEW session, same repo, agent ClaudeCode, on Director <id or "Gateway picks">
          named "banya-fixes" (the source's name, retained)
  Route:  Gateway http://127.0.0.1:7878/handover
  Seed:   moved-session statement + auto handover summary + note: "continue by wiring the webhook handler"
  After:  verify target picks up, rename source "[MOVED -> ...] banya-fixes", hold it.
          Source keeps running - YOU close it when ready.

Proceed?
```

Do not POST until the user approves. If they adjust anything, re-present and re-confirm.

### Step 6: Fire the handover (Gateway)

Shell quoting of JSON is fragile on Windows; build and send with Python:

```bash
python - <<'PY'
import os, json, urllib.request
tok = open(os.path.join(os.environ['LOCALAPPDATA'],'cc-director','config','director','gateway-token.txt')).read().strip()
body = {
    'fromSessionId': os.environ.get('CC_SESSION_ID') or 'PASTE-SOURCE-GUID',
    'toRepoPath': r'D:\ReposFred\cc-consult',
    'toAgent': 'ClaudeCode',
    'archiveToVault': True,
    # 'toDirectorId': 'TARGET-DIRECTOR-GUID',  # a DIFFERENT Director; omit -> target lands on the source's Director
    # 'toSessionId': 'GUID',                   # instead of toRepoPath for an existing empty slot
    # 'extraContext': 'This is a MOVED session: it continues ...  then: continue by ...',
}
req = urllib.request.Request('http://127.0.0.1:7878/handover',
    data=json.dumps(body).encode('utf-8'),
    headers={'Content-Type': 'application/json', 'Authorization': f'Bearer {tok}'},
    method='POST')
with urllib.request.urlopen(req, timeout=60) as r:
    print(r.status)
    print(r.read().decode('utf-8'))
PY
```

`201` + `accepted: true` means the handover was dispatched. Capture
`targetSession.sessionId` from the response. `archivedAt` may come back null even with
`archiveToVault: true` (observed on the Gateway route) - note it in the report rather
than retrying.

### Step 7: NAME THE TARGET - retain the source's original name

The handover creates the target with `name: null` (renders as "(unnamed)"). Name it
immediately, BEFORE the verification wait, so the user watching the UI sees the
familiar name from the first second:

```bash
cc-devthrottle session rename <TARGET-SESSION-GUID> "banya-fixes"
```

(The plain name, no markers - the `[MOVED]` marker belongs on the SOURCE only.)

### Step 8: VERIFY the target picked it up

Poll (~10s, up to ~60s):

```bash
cc-devthrottle session list        # target state should flip to Working
```

For buffer-level proof:

```bash
python - <<'PY'
import os, json, urllib.request
tok = open(os.path.join(os.environ['LOCALAPPDATA'],'cc-director','config','director','gateway-token.txt')).read().strip()
tid = 'TARGET-SESSION-GUID'
req = urllib.request.Request(f'http://127.0.0.1:7878/sessions/{tid}/buffer',
    headers={'Authorization': f'Bearer {tok}'})
print(urllib.request.urlopen(req, timeout=10).read().decode('utf-8')[:400])
PY
```

Healthy signs: state `Working`, buffer bytes growing, the agent responding.

**KNOWN GOTCHA - parked composer:** the seeded context lands in the target composer
but the submit Enter never fires. Symptoms: state stuck `WaitingForInput`, buffer
frozen with the seed visible at the prompt. Fix - send a raw Enter to the TARGET
through the Gateway:

```bash
python - <<'PY'
import os, json, urllib.request
tok = open(os.path.join(os.environ['LOCALAPPDATA'],'cc-director','config','director','gateway-token.txt')).read().strip()
tid = 'TARGET-SESSION-GUID'
body = {'text': '\r', 'appendEnter': False}
req = urllib.request.Request(f'http://127.0.0.1:7878/sessions/{tid}/prompt',
    data=json.dumps(body).encode(),
    headers={'Content-Type': 'application/json', 'Authorization': f'Bearer {tok}'},
    method='POST')
print(urllib.request.urlopen(req, timeout=10).status)
PY
```

(`{"text":"","appendEnter":true}` is a 400 - the endpoint requires non-empty text,
so send the literal `\r` with `appendEnter: false`.) Then re-verify: buffer must grow
and state must reach `Working`.

If verification fails after the nudge, STOP. Report what the target shows. Do NOT mark
the source as moved.

### Step 9: Mark the SOURCE as moved

Only after Step 8 verification passes:

```bash
cc-devthrottle session rename <SOURCE-SESSION-GUID> "[MOVED -> <target>] banya-fixes"
```

Naming: `[MOVED -> <target director id/port or slot>] <original name>`. Keep the
original name so the user still recognizes it; the marker tells them, in the UI, that
this session's context lives elsewhere and it is safe to close.

### Step 10: Put the SOURCE on hold

Immediately after the rename (and ONLY after Step 8 passed):

```bash
cc-devthrottle session hold <SOURCE-SESSION-GUID> --minutes 720
```

Notes:
- Hold is a flag, not a kill - it pauses the session's work but does not terminate it.
- Holding the very session you are driving the move from is safe: a hold asked for
  mid-turn is DEFERRED and applies when the turn settles (the reply says `pending`).
- ONLY THE OWNER lifts a hold (by releasing it, typing into the session, or the
  `--minutes` timer expiring). Another agent messaging it no longer un-holds it.

### Step 11: Report

Tell the user:
- target session id, repo, Director, its retained name, and that it is verifiably
  working;
- the source's new `[MOVED -> ...]` name, WHERE it is, and that it is now ON HOLD;
- that the source is still running (held, not killed) and THEY close it when ready;
- the vault archive path if `archivedAt` was returned (or that it came back null).

## API reference

All Gateway calls: base `http://127.0.0.1:7878`, header `Authorization: Bearer <token>`
(token from `%LOCALAPPDATA%\cc-director\config\director\gateway-token.txt`).

### POST /handover  (GATEWAY - `GatewayEndpoints.cs`, MapPost("/handover"))

Request body (`HandoverRequest`, camelCase):

| Field | Required | Notes |
|-------|----------|-------|
| `fromSessionId` | yes | Source session GUID |
| `toSessionId` | one of two | Existing target session; mutually exclusive with `toRepoPath`. SAME Director as the source only - cross-Director `toSessionId` returns 400 (`not supported in v1`) |
| `toRepoPath` | one of two | Repo for a brand-new target session |
| `toDirectorId` | no | Target Director for a new session. Omit -> the target is created on the SOURCE's Director (no load-balancing). Set it to place the target on a different Director |
| `toAgent` | when `toRepoPath` | `ClaudeCode` (default), `Pi`, `Codex`, `Gemini`, `OpenCode`, `Grok`, `Copilot` |
| `extraContext` | no | Free text appended to the auto-generated context; MUST open with the moved-session statement |
| `archiveToVault` | no | Default true. The same-Director path writes the archive and returns its path; the cross-Director path skips it and returns `archivedAt: null` |

Response (`HandoverResponse`): `accepted`, `targetSession` (read `sessionId`),
`contextSent`, `archivedAt`, `error`.

### Rename, hold, verify - use `cc-devthrottle`

- `cc-devthrottle session rename <target-or-name> "<new name>"` - rename any session
  (defaults to the current one if the target is omitted).
- `cc-devthrottle session hold <target> --minutes <n>` - park a session; owner-only
  release.
- `cc-devthrottle session list` - fleet-wide roster with state.

The equivalent Gateway REST (if you must script raw HTTP) is `PATCH /sessions/{sid}`
`{"name":"..."}`, `POST /sessions/{sid}/hold` `{"onHold":true|false}`, and
`GET /sessions` / `GET /sessions/{sid}/buffer`. Prefer the CLI - it is token-free and
reaches the fleet through your own Director.

### POST /sessions/{sid}/prompt  (GATEWAY - the unstick nudge)

Body `{"text":"\r","appendEnter":false}` sends a raw Enter to the composer. Non-empty
`text` is required (empty + `appendEnter:true` = 400).

## Resume route (OPT-IN ONLY - NOT for context relief)

> WARNING: Do NOT use this route for a normal move. A move exists for CONTEXT RELIEF;
> `resumeSessionId` re-loads the ENTIRE exhausted transcript into the target - it comes
> up already full, exactly the failure this skill must avoid. Use the default
> `/handover` summary route instead.
>
> Use this route ONLY when the user EXPLICITLY asks to carry the full verbatim
> conversation AND accepts that the target inherits the source's used context.

Create the target with `resumeSessionId`, which re-attaches the entire Claude
conversation, via the Gateway's spawn-on-a-named-Director route:

```
POST http://127.0.0.1:7878/directors/{targetDirectorId}/sessions
Authorization: Bearer <token>
{
  "repoPath": "<source's repoPath>",
  "agent": "ClaudeCode",
  "resumeSessionId": "<CLAUDE conversation id>",
  "prePrompt": "This is a MOVED session: it continues <claude-id> ('<name>'). <state + next step>. Confirm to the user the move worked."
}
```

Gotchas:
- `resumeSessionId` is the CLAUDE conversation id (the `.jsonl` filename under
  `~/.claude/projects/<repo-slug>/`), NOT the Director session GUID. From inside the
  session being moved it is the current Claude id; otherwise find the newest `.jsonl`
  that greps for a phrase from that conversation.
- Resuming forks the transcript at the POST: anything the source says AFTER the fork
  is not in the target. Put the missing tail in `prePrompt`.
- The 201 `sessionId` is the new Director session GUID; the naming contract (Step 7,
  Step 9) applies unchanged.
- ClaudeCode only; other agents fall back to the `/handover` summary route.
- Build and send from a single Python stdin script - Windows shell quoting mangles
  JSON backslashes.

## Troubleshooting

| Symptom | Cause / fix |
|---------|-------------|
| Any Director port (`:7879`, `:7883`) returns 404 for `/handover`, `/sessions/{id}`, `PATCH /sessions`, `/hold`, `/prompt` | EXPECTED - those routes moved off the Director loopback in the tunnel-only cut. Use the GATEWAY (`:7878`) and `cc-devthrottle`. |
| Gateway `:7878` 401 on `/sessions` or a mutation | Missing/blank Bearer token. Read it from `gateway-token.txt` and send `Authorization: Bearer <token>`. |
| Gateway `:7878` refused | Gateway down or on another port - find its listen port and confirm the URL with the user. |
| 400 `exactly one of toSessionId or toRepoPath is required` | Send exactly one. |
| 400 `toRepoPath does not exist` | Re-read the source's `repoPath`. |
| 400 `unknown agent` | `toAgent` must be a supported agent kind. |
| 404 source/target not found | Stale GUID. Re-list and re-resolve. |
| Target stuck `WaitingForInput`, seed parked in composer | Parked-composer gotcha. Send `{"text":"\r","appendEnter":false}` to `{gateway}/sessions/{tid}/prompt`, re-verify. |
| Target never picks up even after the nudge | Check state/buffer; if the session exited, dispatch was skipped - re-run the handover. Do NOT mark the source. |
| Target shows "(unnamed)" | Step 7 skipped - the handover creates the target with name null. Rename it with the source's original name. |
| `archivedAt` null on the Gateway route | Known behavior. Note it; do not retry the whole handover for this. |
| Marked the source but the move actually failed | Rename it back to the original name and say so plainly. The `[MOVED]` marker must never lie. |


**Skill Version:** 3.1
**Last Updated:** 2026-07-16
**Changes in 3.1:** Corrected four claims that the Gateway code does not back (independent
review of #1720). (1) Target PLACEMENT: `toRepoPath` with no `toDirectorId` creates the
target on the SOURCE's Director - the Gateway does NOT load-balance across Directors
(`GatewayEndpoints.cs` same-Director proxy). (2) `toSessionId` (existing target) is
SAME-Director only; cross-Director `toSessionId` is rejected with `not supported in v1`
- use `toRepoPath` + `toDirectorId`. (3) The target comes up on the target Director's
CONFIGURED DEFAULT agent (the handover passes no model args); Opus+1M is this
deployment's default, not a guarantee the handover makes - dropped the "do not warn
about a 200K downgrade" instruction. (4) `archivedAt` is written on the same-Director
path and null on the cross-Director path (not a generic "may be null"). Also narrowed
the Director-floor wording (it also serves agent hook routes, not only `/healthz` +
`/fleet/*`).
**Changes in 3.0:** Retargeted to the tunnel-only reality (the whole reason the v2.x
skill broke). The Director loopback no longer serves `/handover`, `GET/PATCH
/sessions`, `/hold`, or `/prompt` - those moved to the GATEWAY (`:7878`) at the
tunnel-only cut (v1.1.0), and hitting a Director port for them now 404s. Every step is
now driven through the Gateway (Bearer token from `gateway-token.txt`) and the
`cc-devthrottle` CLI (list / rename / hold), which is token- and address-free. Removed:
the per-Director base URLs, the port-scan topology step, the `GET
{director}/sessions/{id}/context` window check (no Gateway route; the desktop app shows
the window), and the force-the-model `POST {director}/sessions {args:"--model"}`
fallback (depended on removed Director routes; the target follows the Director's default
agent regardless). Rename
and hold now use `cc-devthrottle session rename` / `session hold` (issue #1514 - the
rename 404 - was a wrong-route/stale-Director problem, closed 2026-07-14; the CLI
verbs work). Added the "control surface is the GATEWAY" preamble and a Gateway health
precheck. The context-relief philosophy, approval gate, naming contract,
verify-before-marking, never-kill-the-source, moved-session statement, and the resume
opt-in are unchanged in substance.
**Source of truth:** `src/CcDirector.Gateway/Api/GatewayEndpoints.cs` (`POST /handover`,
`GET /sessions`, `PATCH /sessions/{sid}`, `POST /sessions/{sid}/hold`, `.../prompt`,
`.../buffer`), `src/CcDirector.Gateway.Contracts/HandoverRequest.cs`,
`src/CcDirector.ControlApi/DirectorAuth.cs` (token path); `cc-devthrottle session
list|rename|hold`.
