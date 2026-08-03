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

## How to run (local rig, one machine)

Prerequisites: .NET 10 SDK, Docker Desktop, and k6 (one static binary - `winget install k6.k6`, or
unzip a release from github.com/grafana/k6/releases onto PATH).

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

# 2b. Stage 0 - the floor. Needs no k6 and no quiet machine, because what it produces is COUNTS. Run it
#     on a rig seeded LOADTEST_TENANTS=1 LOADTEST_DIRECTORS_PER_TENANT=1 with a DirectorSim holding
#     DIRECTORS=1 SESSIONS_PER_DIRECTOR=8 EVENTS_PER_SEC=0 - the shape the 31 July baseline used.
powershell -NoProfile -File tools/loadtest/scripts/run-stage0.ps1 `
    -GatewayUrl http://127.0.0.1:7891 -OutDir "$env:TEMP\loadtest-out" -Label "what this run is"

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
- A single-machine run has the drivers, the Gateway, and Postgres sharing one CPU. Note the machine in
  the report; treat absolute numbers as a floor and the SHAPE (which resource saturates first, where
  the knee is) as the finding.
