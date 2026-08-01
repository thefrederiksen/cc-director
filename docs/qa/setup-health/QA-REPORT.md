# QA report: the Director detects and explains a command line that cannot reach it

Branch `setup-health-badge` · worktree `devthrottle-setup-health` off `origin/main` (b85fe492d)
Verified 2026-08-01 on SOREN_NORTH, slot 7 (`instances\slot-7`), against the machine's real fault.

**Nothing is committed. Nothing is merged. The repair button was NOT clicked - the machine is still
in its faulty state, exactly as asked.**

---

## The bug this closes

An agent in a session reports "cannot connect to DevThrottle" while the Director's own status chip
says Connected. Both are true. The Director is healthy; the `cc-devthrottle` that PATH resolves
belongs to an older install and cannot authenticate against it.

The cost is not downtime - there is none. It is misattribution: the agent blames the network, and
the owner spends the morning there.

### Why nothing caught it

`cc-devthrottle`'s only health check was:

```json
"smoke": { "args": ["actions", "--json"], "expectContains": "settings-get" }
```

`actions --json` prints a static local list and never contacts the Director. It passes on a machine
where every Director-facing verb returns 401 - verified in one shell, seconds apart. A check
answerable without the evidence is not a check.

---

## What was built

| Component | What it does |
|---|---|
| `Core/Setup/FleetToolReachability.cs` | Resolves `cc-devthrottle` with the same rules `CreateProcess` uses, runs it against this Director's own endpoint, reads only the exit code |
| `Core/Setup/FleetToolPathRepair.cs` | Moves this Director's bin to the front of the user PATH and the running process's PATH. Non-destructive |
| `Core/Home/HomeStatus.cs` | New **Sessions** row, so the Home page cannot say "All systems go" over this fault |
| `MainWindow` wiring | Runs the check each tools pass; feeds it in as an *unreconcilable* fault; badge names the real cause |
| `Controls/ToolsView` | The explanation banner and the repair button |

Two design rules the code holds to, both learned the expensive way during this work:

**The verdict is functional, never structural.** "Is the resolved path under my install directory"
looks like the same question and is not - a development build matches no install root and would be
reported broken on a healthy machine. The Director runs the tool PATH actually gives and sees whether
it comes back. Path comparison only writes the explanation.

**The Director judges; the tool is never asked to judge itself.** The case this exists to catch is a
tool too OLD to be correct - and such a tool is also too old to know about any self-report added
today. An earlier draft put the probe inside `cc-devthrottle`'s own `doctor`; that would have gone
silent in exactly the case that matters.

---

## Evidence

### 1. The discriminator separates the cases (before any code was written)

```
CC_DIRECTOR_API=http://127.0.0.1:7879

PATH-resolved tool (stale 1.7.1)  -> exit 1   Error: missing or invalid token
instance tool (1.9.3)             -> exit 0   lists the whole fleet
instance tool, dead endpoint      -> exit 1   Cannot reach the Director at ...:59999
```

The third line matters as much as the first two: the check can fail for a reason other than the one
being hunted, so a pass is not merely "the binary ran".

### 2. The machine's actual fault, captured before repair

```
PATH resolves cc-devthrottle to   C:\Users\soren\AppData\Local\cc-director\bin\cc-devthrottle

version markers
  flat      { "director": "1.7.1", "python-tools": "1.7.1", ... }
  instance  { "python-tools": "1.9.3" }

cc_shared contents
  flat pyenv (on PATH)  ... director.py ...                    <- no director_token.py
  instance pyenv        ... director.py director_token.py ...  <- has the per-instance fix

secrets differ
  flat      -pIlWKItGG... (len 43)
  instance  MNLQQOoUz1... (len 43)

Control API, same endpoint /fleet/sessions
  flat secret     -> HTTP 401  {"error":"missing or invalid token"}
  instance secret -> HTTP 200  [{"sessionId":"c702850e-...
```

Which tool copy can actually drive a Director:

| directory | version | drives the Director |
|---|---|---|
| `cc-director\bin` (what PATH gives) | 1.7.1 | **no** |
| `instances\default\bin` | 1.9.3 | yes |
| `instances\slot-7\bin` | 1.9.3 | yes |

### 3. The running Director catches it

From `instances\slot-7\logs\director\director-2026-08-01-26292.log`:

```
[Program] Instance: slug=slot-7, isDefault=False, explicit=True, port=7887
[ControlApiHost] Kestrel listening on http://127.0.0.1:7880
[FleetToolReachability] RunAsync: probing cc-devthrottle against http://127.0.0.1:7880
[FleetToolReachability] cc-devthrottle at C:\Users\soren\AppData\Local\cc-director\bin\cc-devthrottle.CMD
                        FAILED to reach http://127.0.0.1:7880: Error: missing or invalid token
[MainWindow] tools indicator state: InSync -> NeedsAttention (reconcilableDrift=False, toolFault=True)
```

`reconcilableDrift=False, toolFault=True` is the classification working: it goes red immediately
rather than spending three no-op reconcile attempts first.

An earlier boot of the same build, on a brand-new instance home, showed the other half:
`InSync -> Syncing (reconcilableDrift=True, toolFault=True)` then, once the reconcile finished,
`Syncing -> NeedsAttention (reconcilableDrift=False, toolFault=True)`. Real drift was repaired; the
fault a reconcile cannot touch survived and was reported.

### 4. All three surfaces, screenshotted, agreeing

**Home page** - `01-home-sessions-row.png`

Says **NEEDS ATTENTION**, with the Sessions row naming the cause and offering a route to the repair.
Before this change the same machine printed "All systems go - 8 of 8 tools passing".

**Settings > Tools** - `02-tools-banner.png`

```
(!) Sessions cannot reach this Director
  Your PATH gives   C:\Users\soren\AppData\Local\cc-director\bin\cc-devthrottle.CMD
  This Director is  C:\Users\soren\AppData\Local\cc-director\instances\slot-7\bin
  The command line on your PATH belongs to another install, so agents in your sessions report
  "cannot connect to DevThrottle" even though this Director is healthy and connected.
  [ Repoint PATH to this install ]
  Nothing is deleted. The other install stays on PATH behind this one. Sessions already
  running keep the old PATH until they restart.
```

**Rail badge** - `03-rail-badge.png`

"Sessions cannot reach this Director / the command line on your PATH is from another install",
red, above the connection pill - not on it. The connection indicator was the one thing telling the
truth throughout this bug, and it keeps its own meaning.

### 5. Tests

Full `CcDirector.Core.Tests` suite, on this branch, with everything in:

```
Passed!  - Failed: 0, Passed: 4137, Skipped: 8, Total: 4145, Duration: 15m 17s   exit 0
```

The count reconciles exactly: 4119 on the baseline without these tests, plus the 26 added here.

26 new tests across `FleetToolReachabilityTests`, `FleetToolPathRepairTests`,
`HomeStatusSessionsRowTests`.

They were shown to **fail on purpose**, twice:

- Forcing `RunAsync` to report `Working` regardless of exit code turned 2 of 8 reachability tests red.
- Forcing the Sessions row to return null turned 6 of 7 Home tests red.

Both injections were reverted immediately and the suite re-run.

Two hazards are pinned by tests because they are easy to reintroduce:

- **The user PATH is written RAW.** Reading it via `Environment.GetEnvironmentVariable` returns
  `%USERPROFILE%` already expanded; writing that back would permanently destroy every variable
  reference in the user's PATH. The repair reads the registry with `DoNotExpandEnvironmentNames`.
- **Repointing does not require a restart.** Persisting PATH alone would leave the Director handing
  sessions the stale tool until it restarted - badge unchanged, button apparently broken. The repair
  updates the process PATH too. Sessions already running keep the old PATH; nothing can fix those in
  place, and the banner says so.

---

## Bugs found and fixed during verification

**The check named the broken directory as the good one.** It asked `InstallLayout.Default()` for "my
bin directory", which returns the flat `%LOCALAPPDATA%\cc-director\bin` - the pre-instance-homes path,
and exactly where the superseded tools were left behind. `IsDifferentInstall` came out false, the
button was hidden, and had it shown it would have repointed PATH at the stale copy it exists to
escape. Now uses `InstanceContext.InstanceHome\bin`. Caught before the button was ever clicked; the
button's presence in `02-tools-banner.png` is the proof the fix landed.

**The badge alone was not enough.** It hides while the Home page is up, to avoid reporting one fault
twice - which assumed Home carried the same news. It did not. On a Director with no sessions there is
nowhere to navigate, so the badge was unreachable at exactly the moment it matters most: a fresh
Director on a broken machine. That is why the Sessions row exists.

---

## Known gaps - deliberate, and your call

**`8 tools 8 PASS 0 FAIL` still appears above the banner** (visible in `02-tools-banner.png`). That
count answers a narrower question - "is each tool installed and runnable" - and by that measure it is
true. I did NOT change `cc-devthrottle`'s smoke check to require a Director round trip, because
`ToolTestRunner` is also driven by the Control API and the setup wizard, where no Director endpoint is
available; a tool that failed there would report a false RED on machines with nothing wrong. A false
red is worse than a weak green. The Sessions row now carries the real verdict. If you want the smoke
check strengthened anyway, it needs `CC_DIRECTOR_API` plumbed through `ToolTestRunner` with an
explicit "no endpoint means not applicable" rule.

**A test host crash I cannot explain.** One full-suite run aborted with `Test host process crashed`
at roughly test 1777. Since then: three clean runs at the same commit - one excluding these tests
(4111 passed), and two full runs including them (4130 and 4137 passed). One crash, three clean, never
reproduced. That is not "resolved" - a flake seen once can be seen again, and nobody has explained the
mechanism. It is recorded here so the next person who hits it knows it predates their change.

**`--instance <slug>` silently falls back to default for an unregistered slug.** Not caused by this
work and not fixed here, but it cost time and will cost someone else time. It is why slot 7 had to be
hand-registered in `named-instances.json`.

---

## Before this merges: the base has moved

The worktree was cut from `b85fe492d`; `origin/main` is now **5 commits ahead**:

```
b8ac09f45 feat(repositories): copy a report to the clipboard instead of handing off to an agent (#2364)
8d08d47dc The lock-removal qualification harness: a controlled bypass and the concurrency soak (#2358)
58a996d66 feat(browsers): teach the empty state, and call the thing a profile (#2363)
9271c6598 feat(browsers): drive Brave and Opera, and show the browser list instantly (#2359)
3c13a9997 Three review findings: the safe recorder state becomes the free one, ... (#2356)
```

One of them touches `MainWindow.axaml.cs`, which this work also changes. So everything above was
verified on a base that is no longer current: it needs a rebase onto `origin/main`, a rebuild, and a
re-run of the slot test before merging. Nothing is committed, so the rebase is cheap - but the
evidence in this report does NOT carry over to the rebased result on its own.

## What is NOT verified

- The repair button has never been clicked. Its logic is unit-tested and the PATH rewriting is pinned,
  but no run has proven the badge CLEARS after a real repair. That is the post-release step.
- macOS and Linux: `FleetToolPathRepair` throws `PlatformNotSupportedException` there by design.
- No Gateway-attached Director was tested; slot 7 ran with no Gateway.

---

## Test machine state

Left running for inspection: `cc-director7.exe` on `instances\slot-7`, port 7880, launched via the
`cc-director-pathcheck` scheduled task. Your main Director (81552) and slot 2 (15700) were untouched
throughout.

Two changes made to machine config to enable testing, both reversible:
- `named-instances.json` gained a `slot-7` entry (backup at `named-instances.json.bak-pathcheck`).
- `instances\slot-7\config\config.json` has `onboarding.completed = true`, so the first-run wizard did
  not cover the screen under test.

The machine's PATH is untouched, so the fault - and the ability to re-verify - is still there.
