# Setup health: the tools on PATH must be able to drive this Director

## The symptom

An agent in a session reports "cannot connect to DevThrottle" while the Director's own status
chip says Connected. Both are telling the truth about different things. The Director is healthy
and connected to the Gateway; the `cc-devthrottle` the session reaches through PATH belongs to a
different, older install and cannot authenticate against it.

The cost is not the outage - there is none. The cost is misattribution: the agent concludes the
product is down, says so, and the owner spends the morning on a network that was never broken.

## Why the existing health check missed it

The machinery is already here and already correct in shape:

- `ToolHealthProbe` - the one place that answers "do the tools work?"
- `ToolTestRunner` - runs each tool's declared checks from `tools-manifest.json`
- `ToolsSyncStateMachine` - drives the badge
- `ToolsIndicator` in `MainWindow.axaml` - hidden when healthy, sits directly above the Gateway
  chip, click opens Settings on the Tools tab

What it asks about `cc-devthrottle` is the problem:

```json
"smoke": { "args": ["actions", "--json"], "expectContains": "settings-get" }
```

`actions --json` prints a static local list. It never contacts the Director. It succeeds on a
machine where every Director-facing verb returns 401 - verified at the top of the session that
found this, where `actions --json` passed and `session list` failed in the same shell, seconds
apart.

So the tool is reported Working while it is completely unable to drive the Director. The check is
answerable without the evidence, which makes it not a check.

`doctor_data()` in `setup_ops.py` already gathers nearly the right facts - `installRoot`, `binDir`,
`binDirOnPath`, `ccDevThrottlePath`, `installedBundleVersion` - and already reports "install bin
directory is not on PATH". Nothing joins `ccDevThrottlePath` to `installRoot` to reach the
conclusion, and no health check consumes `doctor` at all.

## Evidence, captured 2026-08-01 on SOREN_NORTH before repair

Two installs. The stale one wins PATH.

```
PATH resolves cc-devthrottle to
  /c/Users/soren/AppData/Local/cc-director/bin/cc-devthrottle

version markers
  flat      %LOCALAPPDATA%\cc-director\config\setup\installed.json
            { "gateway": "1.9.3", "cockpit": "1.0.5", "director": "1.7.1",
              "python-tools": "1.7.1", "cc-launcher": "1.9.3" }
  instance  ...\instances\default\config\setup\installed.json
            { "python-tools": "1.9.3" }

cc_shared package contents
  flat pyenv (on PATH)  ... director.py ...                    <- no director_token.py
  instance pyenv        ... director.py director_token.py ...  <- has the per-instance fix

the two secrets differ
  flat      ...\cc-director\config\director\gateway-token.txt              -> -pIlWKItGG... (len 43)
  instance  ...\instances\default\config\director\gateway-token.txt        -> MNLQQOoUz1... (len 43)

Control API, same endpoint http://127.0.0.1:7879/fleet/sessions
  flat secret     -> HTTP 401  {"error":"missing or invalid token"}
  instance secret -> HTTP 200  [{"sessionId":"c702850e-...

the two command lines, same session, same moment
  PATH copy (flat, 1.7.1)   -> Error: missing or invalid token
  instance copy (1.9.3)     -> lists the whole fleet
```

The stale code path, from the flat pyenv's `cc_shared/director.py`:

```python
local = os.environ.get("LOCALAPPDATA", "")
token_file = Path(local) / "cc-director" / "config" / "director" / "gateway-token.txt"
```

It reads the machine-wide flat root unconditionally. Storage moved to per-instance homes
(`<shared>/instances/<slug>`) and `director_token.py` on main resolves that correctly - the
docstring there describes this exact failure. The fix shipped in v1.9.3. This machine never got it
on PATH, because the migration to instance homes left the old bin, pyenv and token in place and
first in line.

## What this is not

Two things were considered and rejected.

**Do not switch the command line to `CC_DIRECTOR_TOKEN`.** It looks like the clean fix - the
session is handed a valid credential and re-derives its own from disk instead - but the
session-child scope allows only `healthz`, `fleet/sessions`, `fleet/repositories`,
`fleet/worktrees`, its own buffer, and three own-session routes. The command line also calls
`fleet/spawn`, `fleet/rename`, `fleet/hold`, `fleet/done`, `fleet/interrupt`, `fleet/broadcast`,
`fleet/compact`, `fleet/role`, `fleet/machines/{m}/launch` and every `browsers/*` verb, all
refused. Switching would break most of the tool. Widening the scope means letting any agent in any
session spawn, interrupt and rename any session - a security decision, not a tidy-up, and separate
work. (Worth noting on its own someday: the fleet preamble already promises agents those verbs
while their credential cannot carry them.)

**Do not manipulate PATH at launch, and do not have the installer claim it.** An installer that
rewrites PATH to point at itself is exactly what breaks a machine running several Directors - the
most recently installed one hijacks the rest.

**Do not detect "developer machines".** The only available signal is pattern-matching the
executable path against a convention that lives in this repository, not in the product. Every real
user would carry a branch that exists to recognise one machine, and it misfires on any unusual
install path.

## The work

**1. Make the `cc-devthrottle` smoke check exercise the Director round trip.**
A verb that authenticates, not one that prints a local list. The Director runs the PATH-resolved
binary against its own endpoint and sees whether it comes back. This is the whole check:

> Can a session I spawn actually drive me?

Each Director asks only about itself, so "which Director is the main one" never has to be defined -
and a Director running from a development slot is judged by the same rule as any other.

`ToolTestRunner` builds its `ProcessStartInfo` without customising the environment, so it needs to
pass `CC_DIRECTOR_API` (this Director's own endpoint) for the round trip to be possible at all.

This step alone would have caught the machine above.

**2. Join the facts `doctor_data()` already has.**
`ccDevThrottlePath` against `installRoot`: when the resolved binary belongs to a different install,
say so, with both paths and both versions. It has every fact and draws no conclusion.

**3. The explanation and the fix action.**
Settings > Tools is already the click target. It needs the specific sentence and one reversible
action:

```
+-- Setup problem -------------------------------------------+
|  Sessions on this Director cannot reach it from the        |
|  command line.                                             |
|                                                            |
|    Your PATH gives    ...\cc-director\bin\                 |
|                       tools 1.7.1                          |
|    This Director is   ...\instances\default                |
|                       tools 1.9.3                          |
|                                                            |
|  Agents in your sessions will report "cannot connect to    |
|  DevThrottle" even though this Director is healthy.        |
|                                                            |
|      [ Repoint PATH to this install ]     [ Not now ]      |
|  Leaves the old install on disk. Reversible.               |
+------------------------------------------------------------+
```

PATH is shared machine state - it affects other Directors and every open terminal - so this is
offered, never done silently. Repointing is reversible and is the default. Deleting the old install
is a separate, clearly-labelled step, because on a development machine an old install is sometimes
deliberate.

**4. The installer removes or repoints its own predecessor on migration.**
The orphaned 1.7.1 pyenv, its shims and its still-readable fleet token are a one-time migration
gap. A migration that leaves the superseded copy ahead of the new one is not finished.

## Implementation status

Built and proven:

- `src/CcDirector.Core/Setup/FleetToolReachability.cs` - the check. Resolves `cc-devthrottle` with the
  same rules `CreateProcess` uses (`ExecutableResolver`), runs it with `CC_DIRECTOR_API` set to this
  Director's own endpoint, and reads only the exit code. The tool is never asked to judge itself.
- `src/CcDirector.Core/Setup/FleetToolPathRepair.cs` - the fix. Moves this Director's bin directory to
  the front of the user PATH and of the running process's PATH. Non-destructive: nothing is removed,
  the old install stays behind us, the change is reversible by hand.
- 19 tests across `FleetToolReachabilityTests` and `FleetToolPathRepairTests`, all passing.

- `MainWindow.RefreshFleetToolReachabilityAsync` - runs the check alongside each tools health pass,
  using `SessionManager.ControlApiBaseUrl` (the literal address stamped into sessions) and
  `InstallLayout.Default().BinDir`. Before the Control API is listening it does not run, and no verdict
  is left standing as "no verdict" rather than promoted to a pass.
- The verdict feeds `ClassifyToolsProblemAsync` as an **unreconcilable fault**, which was already the
  right concept in `ToolsSyncStateMachine`: installed, failing its own check, no reconcile touches it.
  So the existing badge goes red immediately instead of burning three no-op reconciles first, and no
  new badge or state was needed.
- The red badge names the real thing - "Sessions cannot reach this Director" / "the command line on
  your PATH is from another install" - instead of the generic "Tools need attention", which is true
  here and useless: the tools are fine, they belong to a different install.
- `ToolsView` PATH fault banner plus `ShowFleetToolStatus(...)`. The verdict is PASSED IN from the
  window rather than re-derived, so the rail badge and the banner cannot disagree about one machine at
  one moment. The button repoints PATH, RE-RUNS the check, and only then reports - and it calls back so
  the rail badge clears rather than sitting red behind a dialog that says fixed.
- The button is offered only when the resolved tool is from a different install. Same install and still
  refused is a different fault that repointing PATH would not repair, so the banner states what was seen
  and offers nothing.

Not yet done: the end-to-end proof in a running Director (badge appears on this machine, banner shows,
button clears both), which needs a slot Director launched against the live fault.

## What happened when the button was pressed, 2026-08-01 10:43

It did exactly what it was built to do and could not have worked. From that Director's own log:

```
10:43:16.273 [FleetToolPathRepair] PutFirstOnPath: ...\instances\default\bin
10:43:16.278 [FleetToolPathRepair] PutFirstOnPath done: ...\instances\default\bin is now first
10:43:16.278 [FleetToolReachability] RunAsync: probing cc-devthrottle against http://127.0.0.1:7879
10:43:17.430 [FleetToolReachability] cc-devthrottle at ...\cc-director\bin\cc-devthrottle.CMD
             FAILED to reach http://127.0.0.1:7879: Error: missing or invalid token
```

PATH was repaired correctly - registry and running process both - and the re-check resolved the same
stale copy again, because `instances\default\bin` was **empty**. That Director's own tools had never
been installed. Three minutes later, on the machine's own evidence:

```
10:44:19.283 [ToolReconciler] drift found: missingShims=8
10:44:21.214 [ToolHealthProbe] cc-devthrottle FAILED: binary missing:
             ...\instances\default\pyenv\Scripts\cc-devthrottle.exe
```

and on disk, `instances\default\pyenv` was created at 10:44:25 and the shims at 10:46:20 - both after
the button was pressed. `ExecutableResolver` walks PATH in order and skips a directory with no match,
so promoting an empty one changes the order and nothing about what resolves.

**The root cause: the guard tested the container, not the contents.** `PutFirstOnPath` required only
`Directory.Exists(binDir)`, and the directory had existed - empty - since the instance home was
created. The check above it could see that PATH pointed at another install; it could not see that we
had nothing to point at, because it never asked about our own copy.

The wider fault on that machine was bigger than PATH order: this Director had no tools at all. The
banner named a symptom of that and offered a fix for the symptom.

### What changed

1. **The check asks a second question.** `FleetToolReachability` now runs the same functional probe
   against `expectedBinDir` directly, by full path, whenever the PATH probe fails. `OwnVerdict` splits
   two faults that look identical from outside: PATH resolves someone else's WORKING copy (repoint), or
   we have no working copy (install first, then repoint). It also puts in the log the line whose absence
   made this undiagnosable from the panel: *this Director has no cc-devthrottle of its own*.
2. **The button's precondition is provable.** `CanRepairByRepointingPath` requires our own copy to have
   passed. When it has not, the banner says the tools are not installed and the button installs them
   before touching PATH. `PutFirstOnPath` independently refuses a directory holding no `cc-devthrottle`,
   before it writes anything.
3. **The repair leaves ONE entry per command line.** Two entries serve nobody - only the first can win,
   and the loser waits to win again the moment the order shifts. Superseded DevThrottle directories come
   out of the user PATH: the flat pre-migration `<root>\bin`, anything under the temp directory (a
   wizard test harness had leaked `...\Temp\wizard-harness-home-29ef...\cc-director\bin` onto the real
   user PATH), and install bins gone from disk. **Another live instance's bin is left alone** - a second
   Director in its own instance home is legitimate, and removing its tools to tidy ours would be
   sabotage dressed as hygiene. Entries are decided on the EXPANDED path and written back RAW.
   Nothing on disk is touched.
4. **Sessions no longer depend on machine PATH order.** `SessionManager` puts this Director's own bin
   first on every session's PATH, in the same breath as the `CC_DIRECTOR_API` address the session must
   call. Machine PATH is shared state any other install can win; a session should not have to find us
   when we can tell it. The PATH repair is now a convenience for the user's own terminals rather than
   the mechanism the product depends on.
5. **The panel cannot sit on a stale verdict.** The tools health pass is computed once per run, so the
   verdict handed to the Tools page could be minutes old and describe a machine an intervening repair
   had already healed - which is exactly what the screenshot behind this work was showing. The page
   re-asks on load and repaints.

### Why the original could not have caught this

The design rule "the verdict is FUNCTIONAL, never structural" was applied to the check and not to the
repair. The check ran a tool and read its exit code; the repair asked whether a directory existed. One
of those is a fact about behaviour and the other is a fact about shape, and the shape was true while
the behaviour was absent. The lesson generalises past PATH: **when a remedy depends on a resource, the
guard must exercise the resource, not confirm its container.**

## Two hazards handled, worth not re-introducing

**The user PATH must be written RAW.** Reading it through `Environment.GetEnvironmentVariable` returns
it with `%USERPROFILE%` and friends already expanded; writing that back bakes today's expansion in
permanently and silently destroys every variable reference the user had. The repair reads the registry
with `DoNotExpandEnvironmentNames` and writes back the same `RegistryValueKind` it found. There is a
test pinning that an unexpanded variable survives.

**Persisting PATH does not change a running process.** The Director inherited its environment at
launch, so persisting alone would leave it handing its sessions the stale tool until it restarted - the
badge would not clear and the button would read as broken. The repair updates this process's PATH as
well, so every session spawned from that moment gets the corrected tool. Sessions already running keep
the old PATH; nothing can repair those in place, and the panel must say so rather than implying
otherwise.

## Proving it

The failing state is on SOREN_NORTH now and is the only reproduction; the evidence above is
captured because running the fix action heals it. It is trivially recreatable afterwards by putting
the old bin back at the front of PATH.

The discriminator was proven against the live machine before any code was written, three ways:

```
CC_DIRECTOR_API=http://127.0.0.1:7879

PATH-resolved tool (stale 1.7.1)  -> exit 1   Error: missing or invalid token
instance tool (1.9.3)             -> exit 0   lists the whole fleet
instance tool, dead endpoint      -> exit 1   Cannot reach the Director at ...:59999
```

The third line matters as much as the first two: it shows the check can fail for a reason other than
the one being hunted, so a pass is not merely "the binary ran".

The tests were also shown to fail on purpose. Forcing `RunAsync` to report `Working` regardless of exit
code turned 2 of the 8 reachability tests red; the injection was reverted immediately.

Still to prove, once the wiring exists: the badge appears on this machine driven by the real fault, and
clears after the repair - with new sessions working immediately and no Director restart.
