# Gateway load-test harness

The instrument built by mission 05 (devthrottle_internal issue #1173, under the read-model epic #1159).
It finds the hosted Gateway's ceiling on a test rig - the load at which it degrades, and which resource
gives first - so the read-model epic knows where to aim. It measures; it does not fix.

The plan it implements: `devthrottle_internal/docs/load-test/gateway-load-test-plan.md`.
Baseline reports live in `devthrottle_internal/docs/load-test/runs/`.

## The one hard rule

**NEVER run any of this against production.** Every tool refuses production-looking hosts
(azurewebsites.net, anything containing "devthrottle") with no override, allows loopback freely, and
requires any other host to be named explicitly in `LOADTEST_ALLOW_HOST` (for a dedicated staging rig).
The guard lives in `Shared/LoadTargetGuard.cs` and is mirrored inside `stage1-roster.js`. Only synthetic
tenants, only a throwaway database, torn down after every run.

## What is here

| Piece | What it does |
|---|---|
| `LoadRig/` | Boots the REAL `GatewayHost` in hosted mode (Postgres) in-process, seeds synthetic tenants + device keys through the real registries, writes the key files, then serves as the target. |
| `DirectorSim/` | Stage 2 driver: N SignalR connections to `/director-stream`, real `Hello` + `PushSnapshot` + `PushDelta` contract, strictly increasing per-connection sequences. `EVENTS_PER_SEC=0` holds a silent background fleet for Stage 1. |
| `stage1-roster.js` | Stage 1 driver: k6 ramping viewers polling `GET /sessions` every 2 s (the real client cadence), climbing 100 -> 10,000 viewers. |
| `scripts/start-postgres.ps1` / `stop-postgres.ps1` | The throwaway Postgres container (`dt-loadtest-pg`, port 55442). Stopping REMOVES it and its data - that is the tenant teardown. |
| `scripts/run-stage0.ps1` | The floor: resets the metrics window, polls `GET /sessions` 30 times at the real 2 s cadence, writes an artifact with the counts, the client-side latencies, the machine state and a provenance block. **No k6 needed.** |
| `scripts/run-stage1.ps1` | Wraps k6: resets the metrics window, runs the climb, scrapes `/diag/loadmetrics` every 10 s into a JSONL beside the k6 summary. |
| `Shared/LoadTargetGuard.cs` | The never-production guard, compiled into both consoles. |
| `scripts/loadtarget-guard.ps1` | The same guard for the PowerShell side, dot-sourced by both run scripts (one copy, so it cannot be tightened in one place only). |

The Gateway-side instrumentation (Stage 0 of the plan) lives in the product at
`src/CcDirector.Gateway/Diagnostics/LoadTestMetrics.cs`, served at `GET /diag/loadmetrics`
(auth-gated; `?reset=true` starts a fresh window). It measures: snooze-gate lock wait, snooze database
reads (the N+1 counter), fold duration, roster latency, sweep duration + overlap count + SKIPPED count,
DirectorHub connection count, push in-flight and duration, device-credential lookups, and process numbers
(CPU, working set, GC, thread pool).

Two counters changed meaning when the fixes landed, and both are stated here so a re-run is read
correctly against the 31 July baseline:

- **`snoozeDbReads` is now ONE PER FOLD**, not three per session per fold. The identity to check is
  `counters.snoozeDbReads == foldDurationMs.count`, exactly and with no remainder - subject to a fold over
  zero sessions taking no read at all, which is what an empty tenant does on a multi-tenant rig.
- **`sweepSkipped` is new**, and `sweepTicks` still means "the timer fired". The display sweep now skips a
  tick while a pass is still running, so `sweepTicks - sweepSkipped` is how many ran, and `sweepOverlaps`
  should be ZERO. The overlap counter was deliberately left in place rather than removed: it is the
  instrument that measured the defect (91 of 98 ticks), and a zero from an instrument that could no longer
  report anything else would be no evidence at all.

## How to run STAGE 0 - the floor, and the cheapest measurement here

Stage 0 needs **no k6 and no quiet machine**, because what it produces is COUNTS, and a count means the
same thing on a loaded machine as on an idle one. About fifteen minutes end to end. It is a **complete,
self-contained sequence** - do not mix it with the Stage 1 recipe below, which seeds a different rig.

```powershell
# 1. Throwaway database (container dt-loadtest-pg on 127.0.0.1:55442).
powershell -NoProfile -File tools/loadtest/scripts/start-postgres.ps1

# 2. The rig. DEBUG here, to match the 31 July baseline, which states it measured a Debug build.
#    Leave LOADTEST_MIRROR_CONSOLE unset - the baseline had the console mirror OFF.
dotnet build tools/loadtest/LoadRig/LoadRig.csproj -c Debug
$env:CC_GATEWAY_DB_CONNECTION = "Host=127.0.0.1;Port=55442;Database=gateway_loadtest;Username=loadtest;Password=loadtest"
$env:LOADTEST_TENANTS = "1"; $env:LOADTEST_DIRECTORS_PER_TENANT = "1"
$env:LOADTEST_OUT_DIR = "$env:TEMP\loadtest-out"
Remove-Item Env:LOADTEST_MIRROR_CONSOLE -ErrorAction SilentlyContinue
tools\loadtest\LoadRig\bin\Debug\net10.0\LoadRig.exe
# wait for: RIG READY url=http://127.0.0.1:7891 tenants=1 directors=1 ...

# 3. (second terminal) ONE silent Director carrying EIGHT sessions. Stage 0 measures nothing without it:
#    a viewer that sees zero sessions folds nothing, takes no read, and produces a zero that means the rig
#    was mis-wired. Wait for the CONNECTED line before step 4.
dotnet build tools/loadtest/DirectorSim/DirectorSim.csproj -c Debug
$env:GATEWAY_URL = "http://127.0.0.1:7891"
$env:KEYS_FILE = "$env:TEMP\loadtest-out\directors.json"
$env:DIRECTORS = "1"; $env:SESSIONS_PER_DIRECTOR = "8"; $env:EVENTS_PER_SEC = "0"
tools\loadtest\DirectorSim\bin\Debug\net10.0\DirectorSim.exe
# wait for: [DirectorSim] CONNECTED 1/1 directors ... 8 sessions pushed

# 4. (third terminal) Stage 0 itself - 30 polls at the real 2-second cadence, about a minute.
#    EVERY ONE of these arguments is required; the script refuses to run without them, because a run whose
#    build configuration nobody wrote down is not comparable to a baseline captured under a stated one.
powershell -NoProfile -File tools/loadtest/scripts/run-stage0.ps1 `
    -GatewayUrl http://127.0.0.1:7891 -OutDir "$env:TEMP\loadtest-out" `
    -BuildConfiguration Debug -ConsoleMirror off `
    -Tenants 1 -DirectorsConnected 1 -SessionsPerDirector 8 `
    -Label "what this run is"

# 5. TEARDOWN - removes the database and every synthetic tenant. Always do this.
powershell -NoProfile -File tools/loadtest/scripts/stop-postgres.ps1
```

**Read the result by the identity, not by the total:** `counters.snoozeDbReads` should equal
`foldDurationMs.count` exactly, with no remainder - one set-based read per fold. The 31 July baseline
recorded 1,032 reads for 43 folds, which is 24 per fold.

**On the one-tenant rig, stated honestly.** The seeded tenant count of the rig the baseline's own Stage 0
ran on is **not recorded anywhere** - its `rig-provenance.json` describes a rig booted three and a half
minutes AFTER the Stage 0 artifact was captured. One tenant is therefore **this recipe's deliberate
choice, not a reproduction of a known baseline setting**. It is mechanically harmless for these numbers:
the display sweep folds `PushedSessionStore.KnownTenants`, the tenants with a tunnel-bound Director, and
the roster serves only the caller's own tenant - so with ONE Director connected, exactly one tenant is
folded per sweep whether the rig seeded one tenant or twenty. It also removes an ambiguity, since with one
tenant the viewer key and the Director key cannot belong to different tenants.

## How to run STAGE 1 and beyond (local rig, one machine)

Prerequisites: .NET 10 SDK, Docker Desktop, and k6 (one static binary -
`winget install --id GrafanaLabs.k6`, or unzip a release from github.com/grafana/k6/releases onto PATH).
The package id used to be written here as `k6.k6`, which matches nothing; the working one is
`GrafanaLabs.k6`. **Record the k6 version in the run report** - the client-side percentiles come from it,
and the 31 July baseline did not record which version produced its own.

```powershell
# 1. Throwaway database (container dt-loadtest-pg on 127.0.0.1:55442).
powershell -NoProfile -File tools/loadtest/scripts/start-postgres.ps1

# 2. Build and start the rig (seeds tenants, writes key files, serves as the target).
dotnet build tools/loadtest/LoadRig/LoadRig.csproj -c Release
$env:CC_GATEWAY_DB_CONNECTION = "Host=127.0.0.1;Port=55442;Database=gateway_loadtest;Username=loadtest;Password=loadtest"
$env:LOADTEST_TENANTS = "20"; $env:LOADTEST_DIRECTORS_PER_TENANT = "5"
$env:LOADTEST_OUT_DIR = "$env:TEMP\loadtest-out"
tools\loadtest\LoadRig\bin\Release\net10.0\LoadRig.exe
# wait for: RIG READY url=http://127.0.0.1:7891 ...

# 3. (second terminal) Background fleet for Stage 1: hold 100 Directors x 8 sessions open.
dotnet build tools/loadtest/DirectorSim/DirectorSim.csproj -c Release
$env:GATEWAY_URL = "http://127.0.0.1:7891"
$env:KEYS_FILE = "$env:TEMP\loadtest-out\directors.json"
$env:DIRECTORS = "100"; $env:SESSIONS_PER_DIRECTOR = "8"; $env:EVENTS_PER_SEC = "0"
tools\loadtest\DirectorSim\bin\Release\net10.0\DirectorSim.exe

# 4. (third terminal) Stage 1: the roster-polling climb.
powershell -NoProfile -File tools/loadtest/scripts/run-stage1.ps1 `
    -GatewayUrl http://127.0.0.1:7891 -OutDir "$env:TEMP\loadtest-out" -MaxVus 5000

# 5. Stage 2: the Director push climb (stop the hold-mode sim first; seed a bigger rig for more
#    directors, e.g. LOADTEST_TENANTS=50 LOADTEST_DIRECTORS_PER_TENANT=10 for 500).
$env:EVENTS_PER_SEC = "1000"; $env:DIRECTORS = "500"; $env:DURATION_SECONDS = "300"
$env:METRICS_FILE = "$env:TEMP\loadtest-out\stage2-metrics.jsonl"
$env:METRICS_KEY = (Get-Content "$env:TEMP\loadtest-out\viewers.json" | ConvertFrom-Json)[0].deviceKey
tools\loadtest\DirectorSim\bin\Release\net10.0\DirectorSim.exe

# 6. Stage 3 (combined) = run steps 4 and 5 at the same time.

# 7. TEARDOWN - removes the database and every synthetic tenant. Always do this.
powershell -NoProfile -File tools/loadtest/scripts/stop-postgres.ps1
```

Every knob each tool takes is documented at the top of its `Program.cs` (or the k6 script header).

## Reading a run

- The k6 summary (`stage1-<stamp>-summary.json`) has client-side p50/p95/p99 and error rate per the
  plan's thresholds: p95 < 300 ms, p99 < 800 ms, errors < 0.1 percent.
- The scrape file (`stage1-<stamp>-loadmetrics.jsonl`) has the inside view over time: watch
  `snoozeLockWaitMs.p95Ms` (the shared-gate wait), `counters.snoozeDbReads / counters.rosterRequests`
  (the N+1 ratio - 3 x sessions-per-tenant before the batched read, roughly 1 per fold after),
  `foldDurationMs`, `sweepOverlaps` and `sweepSkipped`, and `process.cpuTotalSeconds` deltas.
- The CEILING is the load step where a threshold crosses; WHICH resource gave first is read from the
  scrape (lock wait vs CPU vs GC vs thread-pool starvation vs sockets).
- Write the result to `devthrottle_internal/docs/load-test/runs/<date>-<stage>.md` (rig identified as
  "local" or by staging digest), per section 8 of the plan.

## Honesty notes (rig vs production)

- The rig boots `GatewayHost` directly (hosted-by-environment, NOT the hosted image). That is exactly
  the seam the Gateway's own hosted tests use; it auto-seeds an entitlement per synthetic tenant so the
  hosted 402 gate stays out of the measurement.
- The production image mirrors every log line synchronously to the console; the rig does not, unless
  you set `LOADTEST_MIRROR_CONSOLE=1`. State which mode a run used in its report.
- **A run being compared against a baseline must match that baseline's build configuration**, and the
  steps above say `-c Release` while the 31 July baseline states it measured a **Debug** build. Build
  whichever the comparison needs and record it - `run-stage0.ps1` refuses to run until you state it.
  Machine noise makes a run look worse and is visible in the numbers; a build-configuration difference
  makes it look better and cannot be seen in them afterwards.
- A single-machine run has the drivers, the Gateway, and Postgres sharing one CPU. Note the machine in
  the report; treat absolute numbers as a floor and the SHAPE (which resource saturates first, where
  the knee is) as the finding.
