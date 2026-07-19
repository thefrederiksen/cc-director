# Driving the hosted Gateway with a real Director

How to stand up a genuine Director, enrolled at the **hosted** Gateway, running a genuine agent,
and how to confirm each leg actually worked.

**Why this document exists.** Every hosted proof before 2026-07-19 used a synthetic client that
spoke the real wire protocol. That proved the tunnel, the read path and tenant isolation, and it
hid a defect that made hosted unusable: the roster was tenant-aware but every per-session route
was not, so a hosted user could see their sessions and do nothing with them (issue #1869). A
synthetic client never drove the down-channel, so nothing noticed. **A hosted change that passes
unit tests green can still leave hosted dead.** This rig is how you find that out.

---

## Safety rules - read before anything else

These are not style preferences. Each one has cost real time.

1. **Never run the installer, or repoint or restart the machine's own Director, to test hosted.**
   On a fleet machine that Director hosts every running session, including the agent doing the
   testing. Use a dedicated slot, always.
2. **Never set `CC_DIRECTOR_ROOT` at User or Machine scope.** It would redirect the *owner's* next
   Director into your test root. Set it in the launching process only - the wrapper script below
   is the whole mechanism.
3. **Confirm the slot is genuinely unoccupied immediately before you launch**, not from memory:
   `Get-Process cc-director16`. Slots 1-4 are the owner's long-lived Directors; never touch them.
4. **Shut your test Director down gracefully** with `POST /shutdown`. A force-kill leaves a phantom
   "interrupted" entry in the fleet (issue #960).
5. **Do not tear the rig down after a successful run** if a hosted fix is pending verification.
   Rebuilding it is pure waste.

## `CC_DIRECTOR_ROOT` redirects WRITES, not the migration SOURCE

This is the trap that makes an "isolated" root not isolated.

On first boot `CcStorageMigration.EnsureMigrated()` copies from **fixed legacy paths** -
`%LOCALAPPDATA%\CcDirector`, `Environment.SpecialFolder.MyDocuments` + `\CcDirector`,
`%LOCALAPPDATA%\cc-myvault`, the comm queue - into the current root, whatever `CC_DIRECTOR_ROOT`
says. So a brand-new test root fills with the machine owner's real accounts, repository list,
session history and vault. Measured on one machine: **~567 MB**, including **224 session history
files, 7 of them carrying a `FirstPromptSnippet` and 36 carrying `TurnSummaries`** - real prompt
text.

Tracked as issue #1879.

**Do not check `%USERPROFILE%\Documents\CcDirector` to decide whether there is anything to
migrate.** When Documents is OneDrive-redirected, `MyDocuments` resolves somewhere else entirely -
on the machine this was written on, `D:\Personal\OneDrive\Documents`, which is where the data
actually was. The literal user-profile path reported "absent" and gave false confidence. Resolve
the real path instead:

```powershell
[Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
```

**Check what landed in your root BEFORE you start the Director, not after.** Treat any such root as
containing real user data: do not commit it, do not attach it to a pull request, do not paste its
contents.

### What actually leaves the machine

The Director reseeds to whatever Gateway that root points at, so the migrated data is *adjacent* to
the wire. What the reseed sends is narrower than what the migration copies -
`GatewayStreamClient.ReseedAsync` sends only:

- a `Hello`: director id, version, **machine name**, **username**, pid, started-at
- `PushSnapshot(seq, <live session list>)`

The session DTO carries no history, no prompt text and no terminal buffer contents
(`totalBufferBytes` is a count, not content). Session history, the repository list and
`recent-sessions.json` are **not** in the push path at all.

So on the run recorded below, nothing real was transmitted: the reseed carried zero sessions, and
the only session that ever reached hosted was the one the test deliberately created. **Treat that
as a property of today's push payload, not as isolation** - verify it rather than assume it, by
querying the Gateway's `/sessions` immediately after the reseed line appears in the log.

**To block a specific file from migrating**, pre-create it in the destination. `CopyFileIfNewer`
skips a destination that already exists, is **10 bytes or larger**, and has a **newer**
last-write-time than the source. A destination under 10 bytes is treated as empty and gets
overwritten, so padding matters.

`sessions.json` under `config\director` is the one that reaches the Gateway, so pre-seed it first.
Note that this technique does **not** scale to the `sessions\` history folder, which is copied
file by file and ran to 224 files on the machine this was written on - another reason #1879 wants
a real opt-out rather than a workaround.

---

## Standing the rig up

### 1. Build a test slot

Build from a worktree at `origin/main`, into a slot the owner is not using. Slots 1-4 are theirs;
5 and above are for testing.

```powershell
.\scripts\local-build-avalonia.ps1 -Slot 16
```

### 2. Write the launch wrapper

`CC_DIRECTOR_ROOT` has to be set for the Director process without leaking to any other process, so
it is set inside a wrapper the launcher runs. `local_builds\launch-hosted-test-director.cmd`:

```bat
@echo off
REM Launches the slot-16 TEST Director against an ISOLATED storage root, so nothing it does
REM touches the machine's real cc-director config (which the owner's live Directors read).
REM CC_DIRECTOR_ROOT is set HERE, in this process only - never at User or Machine scope.
set CC_DIRECTOR_ROOT=D:\ReposFred\_wt\hosted-clientleg\testroot
start "" "D:\ReposFred\_wt\hosted-clientleg\local_builds\cc-director16.exe"
```

### 3. Register a scheduled task - do not launch from the agent's own shell

A Director launched from inside a Claude Code session's pseudo-console gives the `claude.exe`
processes **it** spawns a nested pseudo-console. Those grandchildren detect a non-TTY environment
and exit within about three seconds. Task Scheduler runs the Director under `svchost.exe`, outside
that console, and its sessions survive.

```powershell
$action  = New-ScheduledTaskAction -Execute "D:\ReposFred\_wt\hosted-clientleg\local_builds\launch-hosted-test-director.cmd"
$trigger = New-ScheduledTaskTrigger -Once -At (Get-Date).AddYears(5)   # on-demand only
Register-ScheduledTask -TaskName "cc-director16-launch" -Action $action -Trigger $trigger -Force
```

### 4. Enroll at the hosted Gateway

From the setup CLI, with the same isolated root. This is the client leg under test: it signs in
through a real browser loopback sign-in, posts the **account access token** to the hosted Gateway,
and the Gateway mints a per-device key bound to that account's tenant.

```powershell
$env:CC_DIRECTOR_ROOT = "D:\ReposFred\_wt\hosted-clientleg\testroot"
cc-director-setup enroll --hosted
```

`--hosted` and `--gateway <url>` are mutually exclusive - hosted has no address to give.
`DEVTHROTTLE_HOSTED_GATEWAY_URL` points the same code at a staging box; set but unusable **throws**
rather than quietly enrolling into production.

On success this writes, into the isolated root:

- `config\director\gateway-token.txt` - the per-device key, 43 characters
- `config\config.json` - `gateway.url`, `gateway.token`, `gateway.streamMode = true`

Neither the account token nor the device key is ever logged. The account token is held in memory
only; a workstation persists no account credential.

### 5. Launch

```powershell
if (Get-Process cc-director16 -EA SilentlyContinue) { throw "slot 16 is occupied - do not launch" }
Start-ScheduledTask -TaskName "cc-director16-launch"
```

---

## Confirming each leg

Do not infer any of these from a log line that says it tried. Check the artifact.

### The device key is real - 200 with it, 401 without it

This is the check that proves enrollment produced a credential the hosted Gateway actually honours,
rather than a string that merely got persisted. Both halves matter: a 200 alone does not
distinguish a working key from an unauthenticated route.

```powershell
$k = (Get-Content "$root\config\director\gateway-token.txt" -Raw).Trim()
$b = "https://devthrottle-gw.azurewebsites.net"

# With the key -> 200
(Invoke-WebRequest "$b/directors" -Headers @{Authorization="Bearer $k"} -UseBasicParsing).StatusCode
(Invoke-WebRequest "$b/sessions"  -Headers @{Authorization="Bearer $k"} -UseBasicParsing).StatusCode

# Without it -> 401
try { Invoke-WebRequest "$b/directors" -UseBasicParsing } catch { $_.Exception.Response.StatusCode.value__ }
```

Never echo the key itself.

### The tunnel is up

Read the Director log at `$root\logs\director\director-<date>-<pid>.log`. The port line also tells
you the Control API port, which you need below.

```
[ControlApiHost] Kestrel listening on http://127.0.0.1:<port>
[GatewayStreamClient] connected to https://devthrottle-gw.azurewebsites.net
[GatewayStreamClient] reseeded full snapshot seq=1
[GatewayConnectionMonitor] tunnel connected (two-way stream up)
```

### The Director is on the hosted roster

`GET /directors` with the key returns exactly this Director, and nothing belonging to another
tenant.

### A real agent runs and reaches hosted

Spawn against the test Director's own Control API. Both variables are required: the root so the
CLI reads the test credential, the API so it talks to slot 16 and not the machine's Director.

```powershell
$env:CC_DIRECTOR_ROOT = $root
$env:CC_DIRECTOR_API  = "http://127.0.0.1:<port>"
cc-devthrottle session spawn <repo> --agent ClaudeCode --name "hosted-proof" `
  --prompt "Reply with exactly this token and nothing else: HOSTED-REAL-DIRECTOR-OK"
```

Then confirm, in order:

1. **The agent really ran** - read the transcript at the `claudeTranscriptPath` on the session and
   check the assistant actually emitted the token. A session that exists is not a session that ran.
2. **It reached hosted** - `GET /sessions` with the device key lists it, with a
   **Gateway-assigned number** in the 100-999 range. The Gateway issuing the number proves it ruled
   on the roster rather than passing the Director's value through.
3. **The Gateway ruled on the display state** - `effectiveColor`, `stateLabel`, `triageBucket` and
   `voiceDisplay` are populated on the DTO. The client renders these verbatim; the Gateway owns
   every verdict.

### The down-channel works - the leg that hid issue #1869

**Do not skip this.** The roster being healthy says nothing about whether anything can be done with
it. Exercise the per-session surface directly against hosted:

```powershell
POST /sessions/{sid}/prompt      # body: { "text": "..." }
POST /sessions/{sid}/interrupt
GET  /sessions/{sid}/buffer?lines=5
GET  /sessions/{sid}/summary
```

A 404 `{"error":"session not found across any director"}` while `GET /sessions` lists that same
session is the signature of a route resolving the wrong tenant. `/buffer` in particular is the
terminal view: dead `/buffer` means a hosted user cannot see their own terminal.

### Teardown ages out correctly

```powershell
Invoke-WebRequest "http://127.0.0.1:<port>/shutdown" -Method POST -UseBasicParsing
```

The Director exits, and within `GatewayConfig.DefaultStreamStaleAfterSeconds` (20 seconds) the
hosted `/directors` and `/sessions` both return `[]`. **Query after the threshold, not inside it** -
a roster that still lists a Director five seconds after shutdown is correct behaviour, not a bug.

---

## Known-good result

Recorded 2026-07-19, the first time hosted was driven by a real Director (issue #1857 gave
`POST /devices/enroll-hosted` its client):

- enrolled through real sign-in; key 200s on `/directors` and `/sessions`, 401 without it
- tunnel connected, full snapshot reseeded
- a real Claude Code session answered `HOSTED-REAL-DIRECTOR-OK`
- visible on hosted as session number 100, with folded colour, label, triage bucket, current model
  and token totals
- clean shutdown aged out at the 20-second threshold

Defects this rig found on first contact: **#1869** (every per-session route 404s on hosted),
**#1870** (`ViewUrl` carries `gw=http://` on an HTTPS-only host), and live reproductions of
**#1855** and **#1856**.
